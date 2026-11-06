using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Conveyors;
using Keen.VRage.Core.Plugins;
using Keen.VRage.DCS.Definitions;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.DCS.Builders;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Utils;
using Keen.VRage.Library.Serialization.ObjectBuilders;
using MonoMod.RuntimeDetour;

namespace SorterUnlock;

/// <summary>
/// Turns the vanilla (cosmetic) Conveyor Sorter into a working filter by adding the game's own
/// FilterComponent to the sorter's server composition in memory, right before the game builds
/// conveyor graphs. Nothing on disk is changed; remove the plugin and the game is vanilla again.
/// All the actual filtering logic and the terminal controls are Keen's own code.
/// </summary>
public class SorterUnlockPlugin : IPlugin
{
    // Stable slot id so the component's saved state maps back to the same slot on every load.
    private static readonly Guid FilterSlotId = new("5a0e7e11-50c7-4f11-b0c5-000000005047");
    private static readonly StringId FilterTag = StringId.Get("FilterComponent");

    private static Hook? _postProcessHook;
    private static Hook? _initHook;
    private static Hook? _prefabHook;
    private static Hook? _prefabInitHook;
    private static Hook? _wcfHook;
    private static Hook? _feobHook;
    private static Hook? _prefabGetHook;
    private static FieldInfo? _prefabEntity;
    private static MethodInfo? _assign;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<EntityCompositeDefinition, byte> Patched = new();
    private static bool _keenAlreadyDidIt;

    private delegate void OrigInit(EntityCompositeDefinition self, EntityCompositeDefinitionObjectBuilder ob, ObjectBuilderLoadingContext context);
    private delegate void OrigPostProcess(ConveyorGraphPostProcessor self, EntityCompositeDefinition composition);

    public SorterUnlockPlugin()
    {
        Log.Write($"SorterUnlock {typeof(SorterUnlockPlugin).Assembly.GetName().Version} initializing...");
    }

    public SorterUnlockPlugin(PluginHost host) : this()
    {
        try
        {
            _assign = typeof(EntityCompositeDefinition).GetMethod("Assign", BindingFlags.Instance | BindingFlags.NonPublic)
                      ?? throw new MissingMethodException("EntityCompositeDefinition.Assign not found (game update?)");

            MethodInfo target = typeof(ConveyorGraphPostProcessor).GetMethod(
                                    nameof(ConveyorGraphPostProcessor.PostProcess),
                                    BindingFlags.Instance | BindingFlags.Public,
                                    null, new[] { typeof(EntityCompositeDefinition) }, null)
                                ?? throw new MissingMethodException("ConveyorGraphPostProcessor.PostProcess not found (game update?)");

            // v0.2: patch the composition right after it is built from data, BEFORE prefabs and
            // default object builders are generated from it. Patching later (0.1.x) left those
            // caches with one component fewer -> IndexOutOfRange on world load and block placement.
            MethodInfo init = typeof(EntityCompositeDefinition)
                                  .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                                  .FirstOrDefault(m => m.Name == "Init" && m.GetParameters().Length == 2
                                                       && m.GetParameters()[0].ParameterType == typeof(EntityCompositeDefinitionObjectBuilder))
                              ?? throw new MissingMethodException("EntityCompositeDefinition.Init(ob, context) not found (game update?)");
            _initHook = new Hook(init, typeof(SorterUnlockPlugin).GetMethod(nameof(HookedInit), BindingFlags.Static | BindingFlags.NonPublic)!);
            Log.Write("Hook installed on EntityCompositeDefinition.Init");

            // v0.2.1: prefabs (e.g. ConveyorSorter150_Server.def) carry a pre-built entity object builder
            // with one OB per composite slot. Add the matching default FilterComponent OB so counts agree
            // ("Mismatch in Entity parts OBs:6 IDs:7" in 0.2.0).
            _prefabEntity = typeof(PrefabDefinition).GetField("_entity", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new MissingFieldException("PrefabDefinition._entity not found (game update?)");
            MethodInfo prefabPostInit = typeof(PrefabDefinition).GetMethod("PostInit", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                                        ?? throw new MissingMethodException("PrefabDefinition.PostInit not found (game update?)");
            _prefabHook = new Hook(prefabPostInit, typeof(SorterUnlockPlugin).GetMethod(nameof(HookedPrefabPostInit), BindingFlags.Static | BindingFlags.NonPublic)!);
            Log.Write("Hook installed on PrefabDefinition.PostInit");

            // v0.2.2: validation of prefabs runs BEFORE PostInit on the first load passes, so the OB must
            // be added already in Init (right after _entity is assigned from data).
            MethodInfo prefabInit = typeof(PrefabDefinition).GetMethod("Init", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(PrefabDefinitionObjectBuilder) }, null)
                                    ?? throw new MissingMethodException("PrefabDefinition.Init(ob) not found (game update?)");
            _prefabInitHook = new Hook(prefabInit, typeof(SorterUnlockPlugin).GetMethod(nameof(HookedPrefabInit), BindingFlags.Static | BindingFlags.NonPublic)!);
            Log.Write("Hook installed on PrefabDefinition.Init");

            // Safety nets: prefabs restored from the binary content cache may skip Init/PostInit.
            // Every consumer goes through Get() (validation, block placer, spawning).
            // Universal choke point: every EntityObjectBuilder -> entity conversion goes through
            // EntityBuilder.WithComponentsFrom (validation, placement, spawning, save load).
            try
            {
                MethodInfo wcf = typeof(EntityBuilder).GetMethod("WithComponentsFrom", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(EntityObjectBuilder) }, null)
                    ?? throw new MissingMethodException("EntityBuilder.WithComponentsFrom");
                _wcfHook = new Hook(wcf, typeof(SorterUnlockPlugin).GetMethod(nameof(HookedWithComponentsFrom), BindingFlags.Static | BindingFlags.NonPublic)!);
                Log.Write("Hook installed on EntityBuilder.WithComponentsFrom");
            }
            catch (Exception ex) { Log.Error("hook WithComponentsFrom", ex); }
            try
            {
                MethodInfo feob = typeof(EntityBuilder).GetMethod("FromEntityObjectBuilder", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(EntityObjectBuilder) }, null)
                    ?? throw new MissingMethodException("EntityBuilder.FromEntityObjectBuilder");
                _feobHook = new Hook(feob, typeof(SorterUnlockPlugin).GetMethod(nameof(HookedFromEob), BindingFlags.Static | BindingFlags.NonPublic)!);
                Log.Write("Hook installed on EntityBuilder.FromEntityObjectBuilder");
            }
            catch (Exception ex) { Log.Error("hook FromEntityObjectBuilder", ex); }
            try
            {
                MethodInfo get = typeof(PrefabDefinition).GetMethod("Get", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)
                    ?? throw new MissingMethodException("PrefabDefinition.Get");
                _prefabGetHook = new Hook(get, typeof(SorterUnlockPlugin).GetMethod(nameof(HookedPrefabGet), BindingFlags.Static | BindingFlags.NonPublic)!);
                Log.Write("Hook installed on PrefabDefinition.Get");
            }
            catch (Exception ex) { Log.Error("hook PrefabDefinition.Get", ex); }

            Lang.Install();
            UiFix.Install();
            Diag.Install();
            AccessFix.Install();
            Pumps.Install();
            FilterSync.Install();
            FilterPanel.Install();

            _postProcessHook = new Hook(target, new Action<OrigPostProcess, ConveyorGraphPostProcessor, EntityCompositeDefinition>(HookedPostProcess));
            Log.Write("Hook installed on ConveyorGraphPostProcessor.PostProcess. Log: " + Log.FilePath);
        }
        catch (Exception ex)
        {
            Log.Error("initialization failed, plugin is inactive (game stays vanilla)", ex);
        }
    }

    private static void HookedInit(Action<EntityCompositeDefinition, EntityCompositeDefinitionObjectBuilder, ObjectBuilderLoadingContext> orig,
                                   EntityCompositeDefinition self, EntityCompositeDefinitionObjectBuilder ob, ObjectBuilderLoadingContext context)
    {
        orig(self, ob, context);
        try
        {
            if (Contains(self.DebugName))
                TryUnlock(self);
        }
        catch (Exception ex)
        {
            Log.Error($"patching {Describe(self)} failed, leaving it vanilla", ex);
        }
    }

    private static void HookedPrefabInit(Action<PrefabDefinition, PrefabDefinitionObjectBuilder> orig, PrefabDefinition self, PrefabDefinitionObjectBuilder ob)
    {
        orig(self, ob);
        EnsurePrefab(self, "Init");
    }

    private delegate EntityBuilder OrigWcf(ref EntityBuilder self, EntityObjectBuilder entity);

    private static EntityBuilder HookedWithComponentsFrom(OrigWcf orig, ref EntityBuilder self, EntityObjectBuilder entity)
    {
        FixEob(entity, "WithComponentsFrom");
        return orig(ref self, entity);
    }

    private static EntityBuilder HookedFromEob(Func<EntityObjectBuilder, EntityBuilder> orig, EntityObjectBuilder entity)
    {
        FixEob(entity, "FromEntityObjectBuilder");
        return orig(entity);
    }

    private static int _fixLogs;

    private static void FixEob(EntityObjectBuilder? eob, string where)
    {
        try
        {
            EntityCompositeDefinition? def = eob?.Definition;
            if (eob == null || def == null || !def.Ids.Contains(FilterSlotId)) return;
            var obs = eob.ObjectBuilders;
            if (obs.Keys.Contains(FilterSlotId) || obs.Count != def.Ids.Length - 1) return;
            obs.Add(FilterSlotId, new FilterComponentObjectBuilder());
            if (_fixLogs++ < 50) Log.Write($"FIX {where}: added FilterComponent OB to {Describe(def)} (now {obs.Count})");
        }
        catch (Exception ex) { Log.Error("FixEob " + where, ex); }
    }

    private static EntityObjectBuilder HookedPrefabGet(Func<PrefabDefinition, EntityObjectBuilder> orig, PrefabDefinition self)
    {
        EnsurePrefab(self, "Get");
        return orig(self);
    }

    private static void HookedPrefabPostInit(Action<PrefabDefinition> orig, PrefabDefinition self)
    {
        EnsurePrefab(self, "PostInit");
        orig(self);
    }

    /// <summary>Server-side sorter prefab: make sure it carries a default OB for our FilterComponent slot.</summary>
    private static void EnsurePrefab(PrefabDefinition self, string stage)
    {
        try
        {
            string? name = self.DebugName;
            if (!Contains(name) || name!.Contains("Client", StringComparison.OrdinalIgnoreCase))
                return;
            if (_prefabEntity!.GetValue(self) is not EntityObjectBuilder eob || eob.ObjectBuilders == null)
            {
                Log.Write($"PREFAB {name} [{stage}]: no entity OB yet");
                return;
            }
            var comp = eob.Definition;
            // If the game itself ever ships a FilterComponent (not our slot), stay out of it.
            if (comp != null && comp.ComponentCount > 0 && comp.Types.Any(t => t == typeof(FilterComponent)) && !comp.Ids.Contains(FilterSlotId))
            {
                Log.Write($"PREFAB {name} [{stage}]: composite already has a native FilterComponent - untouched");
                return;
            }
            if (eob.ObjectBuilders.Keys.Contains(FilterSlotId))
                return; // already done
            eob.ObjectBuilders.Add(FilterSlotId, new FilterComponentObjectBuilder());
            Log.Write($"PREFAB {name} [{stage}]: added default FilterComponent OB (OBs {eob.ObjectBuilders.Count}, composite {(comp?.ComponentCount.ToString() ?? "?")})");
        }
        catch (Exception ex)
        {
            Log.Error($"patching prefab {self.DebugName} [{stage}] failed", ex);
        }
    }

    private static void HookedPostProcess(OrigPostProcess orig, ConveyorGraphPostProcessor self, EntityCompositeDefinition composition)
    {
        try
        {
            // Verification only: never patch this late (that is what crashed 0.1.x).
            if (Contains(composition.DebugName) && !composition.DebugName!.Contains("Client", StringComparison.OrdinalIgnoreCase))
            {
                bool has = composition.Types.Any(t => t == typeof(FilterComponent));
                Log.Write($"check {Describe(composition)} at conveyor post-process: FilterComponent={(has ? "YES" : "NO (Init hook missed it, sorter stays vanilla)")}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"patching {Describe(composition)} failed, leaving it vanilla", ex);
        }
        orig(self, composition);
        try { DirectionFix.Apply(self, composition); }
        catch (Exception ex) { Log.Error($"direction fix for {Describe(composition)}", ex); }
    }

    private static void TryUnlock(EntityCompositeDefinition c)
    {
        if (!IsSorter(c))
            return;

        // v0.1.1: only the SERVER composition gets the filter. Patching the client composition in 0.1.0
        // broke world loading. The terminal UI is server-streamed, so the client does not need it.
        if (c.DebugName != null && c.DebugName.Contains("Client", StringComparison.OrdinalIgnoreCase))
        {
            Log.Write($"skip {Describe(c)}: client composition");
            return;
        }

        bool hasConveyor = c.Types.Any(t => typeof(ConveyorComponent).IsAssignableFrom(t));
        if (!hasConveyor)
        {
            Log.Write($"skip {Describe(c)}: no ConveyorComponent (client-side composition?)");
            return;
        }

        if (c.Types.Any(t => t == typeof(FilterComponent)))
        {
            if (!Patched.ContainsKey(c) && !_keenAlreadyDidIt)
            {
                _keenAlreadyDidIt = true;
                Log.Write($"{Describe(c)} already has FilterComponent from the game itself. " +
                          "Keen made sorters work - this plugin is no longer needed, you can remove it.");
            }
            return;
        }

        bool hasPowerable = c.Types.Any(t => typeof(PowerableBlockComponent).IsAssignableFrom(t));
        bool hasBlock = c.Types.Any(t => typeof(CubeBlockComponent).IsAssignableFrom(t));
        if (!hasPowerable || !hasBlock)
        {
            Log.Write($"skip {Describe(c)}: missing dependencies (Powerable={hasPowerable}, CubeBlock={hasBlock})");
            return;
        }

        int n = c.ComponentCount;
        var b = new EntityCompositeDefinition.Builder(n + 1);
        for (int i = 0; i < n; i++)
        {
            b.Ids[i] = c.Ids[i];
            b.Types[i] = c.Types[i];
            b.Definitions[i] = c.Definitions[i];
            b.Tags[i] = c.Tags[i];
        }
        // Appended last: FilterComponent depends on Powerable/Conveyor/CubeBlock, which are all earlier.
        b.Ids[n] = FilterSlotId;
        b.Types[n] = typeof(FilterComponent);
        b.Definitions[n] = null;
        b.Tags[n] = ImmutableArray.Create(FilterTag);
        b.InvalidateTagIndex();

        _assign!.Invoke(c, new object[] { b });
        ClearComponentIndexCache(c);
        Patched[c] = 0;

        Log.Write($"UNLOCKED {Describe(c)}: FilterComponent added (slot {n}, tag '{FilterTag}')");
    }

    private static void ClearComponentIndexCache(EntityCompositeDefinition c)
    {
        var field = typeof(EntityCompositeDefinition).GetField("_componentIndices", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(c) is System.Collections.IDictionary dict)
            dict.Clear();
        else
            Log.Write("note: _componentIndices cache not found/cleared");
    }

    private static bool IsSorter(EntityCompositeDefinition c) => Contains(c.DebugName);

    private static bool Contains(string? s) => s != null && s.Contains("ConveyorSorter", StringComparison.OrdinalIgnoreCase);

    private static string Describe(EntityCompositeDefinition c) => $"'{c.DebugName}' ({c.Guid})";
}
