using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Conveyors;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Conveyors;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.SecureIds;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;

namespace SorterUnlock;

/// <summary>
/// 0.3.0 server side of the item filter:
///  - saved filter items reach the conveyor graph on load (Keen creates the graph filter with an EMPTY item set);
///  - whitelist/blacklist toggle actually updates the graph filter (Keen only updates the direction);
///  - registry EntityId (as the control panel sees it) -> sorter entity, so the client panel can find its FilterComponent.
/// </summary>
internal static class FilterSync
{
    private static readonly List<object> Keep = new();
    private static readonly object Gate = new();
    private static readonly Dictionary<EntityId, WeakReference<Entity>> Registry = new();
    private static MethodInfo? _updateFilter;

    public static void Install()
    {
        try
        {
            _updateFilter = typeof(FilterComponent).GetMethod("UpdateFilter", BindingFlags.Instance | BindingFlags.NonPublic);
            InstallInsertFix();
            InstallWhitelistFix();
            InstallRegistry();
            InstallGraphMapping();
        }
        catch (Exception ex) { Log.Error("FILTER install", ex); }
    }

    // ---- saved items -> graph on insertion ----
    private static void InstallInsertFix()
    {
        try
        {
            var target = typeof(ConveyorElementTranslator).GetMethod("InsertIntoConveyorGraph", BindingFlags.Public | BindingFlags.Static)!;
            var ctor = typeof(ConveyorFilter).GetConstructors().Single();
            var getDirected = typeof(FilterComponent).GetProperty("IsDirected")!.GetGetMethod()!;
            var fix = typeof(FilterSync).GetMethod(nameof(FixNewFilter), BindingFlags.Public | BindingFlags.Static)!;
            Keep.Add(new ILHook(target, il =>
            {
                var c = new ILCursor(il);
                if (!c.TryGotoNext(MoveType.After, i => i.MatchNewobj(ctor))) { Log.Write("FILTER: ConveyorFilter ctor not found, load fix inactive"); return; }
                // find the local holding filterComponent: the instruction right before 'callvirt get_IsDirected'
                Instruction? load = null;
                for (var p = c.Prev; p != null; p = p.Previous)
                {
                    if (p.MatchCallOrCallvirt(getDirected)) { load = p.Previous; break; }
                }
                if (load == null || !(load.OpCode.Name.StartsWith("ldloc") || load.OpCode.Name.StartsWith("ldarg")))
                {
                    Log.Write("FILTER: filterComponent local not found, load fix inactive");
                    return;
                }
                c.Emit(OpCodes.Dup);
                if (load.Operand == null) c.Emit(load.OpCode); else c.Emit(load.OpCode, load.Operand);
                c.Emit(OpCodes.Call, fix);
                Log.Write("FILTER: saved items are now applied when a sorter enters the conveyor graph");
            }));
        }
        catch (Exception ex) { Log.Error("FILTER insert fix", ex); }
    }

    private static int _fixLogs;
    public static void FixNewFilter(ConveyorFilter filter, FilterComponent comp)
    {
        try
        {
            if (filter == null || comp == null) return;
            ApplyGraph(comp, filter);
            var n = comp.FilteredItems?.Count ?? 0;
            if (n > 0 && _fixLogs++ < 20) Log.Write($"FILTER: restored {n} item(s), whitelist={comp.IsWhitelist}");
        }
        catch (Exception ex) { Log.Error("FILTER FixNewFilter", ex); }
    }

    /// <summary>
    /// What the conveyor network actually gets from a sorter's settings (0.6.1):
    ///  - nothing selected = no filtering, in both modes;
    ///  - maintenance ("перекачка") = fully open pipe both ways.
    /// The sorter's saved settings are never touched.
    /// </summary>
    public static void ApplyGraph(FilterComponent comp, ConveyorFilter g)
    {
        if (Maintenance.IsInMaintenance(comp))
        {
            g.Items = new HashSet<ItemDefinition>();
            g.IsWhitelist = false;
            g.AllowUnfilteredOppositeFlow = true;
            return;
        }
        var items = comp.FilteredItems;
        g.Items = items != null ? new HashSet<ItemDefinition>(items) : new HashSet<ItemDefinition>();
        g.IsWhitelist = comp.IsWhitelist && g.Items.Count > 0;
        g.AllowUnfilteredOppositeFlow = comp.AllowOppositeFlow;
    }

    public static ConveyorFilter? GraphFilter(FilterComponent comp, ConveyorSystemComponent sys)
    {
        var idx = ConveyorElementTranslator.TryGetNodeIndex(comp, sys);
        if (!idx.HasValue) return null;
        var node = sys.ConveyorGraph.GetNode(idx.Value);
        return node.FilterDecoration.HasValue ? sys.ConveyorGraph.GetFilter(node.FilterDecoration.Value) : null;
    }

    private delegate void OrigSetFilter(FilterComponent f, ConveyorSystemComponent sys);
    private static void HSetFilter(OrigSetFilter orig, FilterComponent f, ConveyorSystemComponent sys)
    {
        orig(f, sys);
        try { var g = GraphFilter(f, sys); if (g != null) ApplyGraph(f, g); } catch (Exception ex) { Log.Error("FILTER SetFilter post", ex); }
    }

    private delegate void OrigSetOpp(FilterComponent f, ConveyorSystemComponent sys, bool on);
    private static void HSetOpposite(OrigSetOpp orig, FilterComponent f, ConveyorSystemComponent sys, bool on)
    {
        if (Maintenance.IsInMaintenance(f)) on = true; // keep the pipe open until maintenance ends
        orig(f, sys, on);
    }

    private static void InstallGraphMapping()
    {
        try
        {
            var t = typeof(ConveyorElementTranslator);
            Keep.Add(new Hook(t.GetMethod("SetFilter", BindingFlags.Public | BindingFlags.Static)!, typeof(FilterSync).GetMethod(nameof(HSetFilter), BindingFlags.Static | BindingFlags.NonPublic)!));
            Keep.Add(new Hook(t.GetMethod("SetOppositeFLow", BindingFlags.Public | BindingFlags.Static)!, typeof(FilterSync).GetMethod(nameof(HSetOpposite), BindingFlags.Static | BindingFlags.NonPublic)!));
            Log.Write("FILTER: empty list = no filtering; maintenance keeps the sorter open");
        }
        catch (Exception ex) { Log.Error("FILTER graph mapping", ex); }
    }

    // ---- whitelist toggle -> graph ----
    private delegate void OrigBool(FilterComponent self, bool value);
    private static void InstallWhitelistFix()
    {
        try
        {
            var m = typeof(FilterComponent).GetMethod("IsWhitelistChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Keep.Add(new Hook(m, typeof(FilterSync).GetMethod(nameof(HWhitelist), BindingFlags.Static | BindingFlags.NonPublic)!));
            Log.Write("FILTER: whitelist toggle now refreshes the conveyor filter");
        }
        catch (Exception ex) { Log.Error("FILTER whitelist fix", ex); }
    }

    private static void HWhitelist(OrigBool orig, FilterComponent self, bool value)
    {
        orig(self, value);
        try { _updateFilter?.Invoke(self, null); Pumps.RestartNetwork(self); } catch (Exception ex) { Log.Error("FILTER whitelist refresh", ex); }
    }

    // ---- EntityId registry ----
    private delegate void OrigCtor(object self, Entity entity, ControlPanelGridModel grid, EntityId id, ImmutableArray<SubclassOf<BlockDetailModel>> types, ControlPanelGroupModel? group);
    private static void InstallRegistry()
    {
        try
        {
            var t = typeof(ControlPanelEntityModel).Assembly.GetType("Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.ControlPanelEntityModelServer")!;
            var ctor = t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(c => c.GetParameters().Length == 5);
            Keep.Add(new Hook(ctor, typeof(FilterSync).GetMethod(nameof(HCtor), BindingFlags.Static | BindingFlags.NonPublic)!));
            Log.Write("FILTER: control panel entity registry installed");
        }
        catch (Exception ex) { Log.Error("FILTER registry", ex); }
    }

    private static void HCtor(OrigCtor orig, object self, Entity entity, ControlPanelGridModel grid, EntityId id, ImmutableArray<SubclassOf<BlockDetailModel>> types, ControlPanelGroupModel? group)
    {
        orig(self, entity, grid, id, types, group);
        try
        {
            if (entity != null && entity.TryGet<FilterComponent>() != null)
                lock (Gate) Registry[id] = new WeakReference<Entity>(entity);
        }
        catch { }
    }

    public static FilterComponent? Find(EntityId id)
    {
        lock (Gate)
        {
            if (Registry.TryGetValue(id, out var w) && w.TryGetTarget(out var e))
            {
                try { return e.TryGet<FilterComponent>(); } catch { return null; }
            }
        }
        return null;
    }

    /// <summary>Apply a new item set to a sorter, keeping its current whitelist mode.</summary>
    public static bool Apply(FilterComponent comp, HashSet<ItemDefinition> items)
    {
        try
        {
            var copy = new HashSet<ItemDefinition>(items);
            if (!Pumps.TickAlive)
            {
                // fallback (server tick not seen yet): apply directly like 0.4.x
                comp.SetNewFilter(copy, comp.IsWhitelist);
                return true;
            }
            Pumps.Post(() =>
            {
                comp.SetNewFilter(copy, comp.IsWhitelist);
                Pumps.RestartNetwork(comp); // Drain All wakes up by itself after a filter change
            });
            return true;
        }
        catch (Exception ex) { Log.Error("FILTER apply", ex); return false; }
    }
}
