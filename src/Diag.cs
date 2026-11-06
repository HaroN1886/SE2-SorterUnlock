using System;
using System.Linq;
using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Conveyors;
using MonoMod.RuntimeDetour;

namespace SorterUnlock;

/// <summary>Read-only diagnostics of FilterComponent: does it find its conveyor node, does Drain All create a pull request.</summary>
internal static class Diag
{
    private static Hook? _init, _drain, _create, _setFilter;
    private static FieldInfo? _node, _pull, _drainAll, _isDirected;

    public static void Install()
    {
        var t = typeof(FilterComponent);
        const BindingFlags I = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        _node = t.GetField("_relatedSubgraphNodeId", I);
        _pull = t.GetField("_pullRequest", I);
        _drainAll = t.GetField("_drainAll", I);
        _isDirected = t.GetField("_isDirected", I);
        Try("FilterComponent.Init", () =>
        {
            var m = t.GetMethods(I).Single(x => x.Name == "Init" && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == typeof(FilterComponentObjectBuilder));
            _init = new Hook(m, typeof(Diag).GetMethod(nameof(HInit), BindingFlags.Static | BindingFlags.NonPublic)!);
        });
        Try("FilterComponent.UpdateDrainAll", () =>
        {
            var m = t.GetMethod("UpdateDrainAll", I, null, new[] { typeof(bool) }, null)!;
            _drain = new Hook(m, typeof(Diag).GetMethod(nameof(HDrain), BindingFlags.Static | BindingFlags.NonPublic)!);
        });
        Try("FilterComponent.CreatePersistentRequest", () =>
        {
            var m = t.GetMethod("CreatePersistentRequest", I, null, Type.EmptyTypes, null)!;
            _create = new Hook(m, typeof(Diag).GetMethod(nameof(HCreate), BindingFlags.Static | BindingFlags.NonPublic)!);
        });
        Try("FilterComponent.SetNewFilter", () =>
        {
            var m = t.GetMethod("SetNewFilter", I)!;
            _setFilter = new Hook(m, typeof(Diag).GetMethod(nameof(HSetFilter), BindingFlags.Static | BindingFlags.NonPublic)!);
        });
    }

    private static void Try(string what, Action a)
    {
        try { a(); Log.Write("Diag hook installed on " + what); }
        catch (Exception ex) { Log.Error("diag hook " + what, ex); }
    }

    private static string State(FilterComponent f)
    {
        try
        {
            return $"node={_node?.GetValue(f)} drainAll={_drainAll?.GetValue(f)} directed={_isDirected?.GetValue(f)} pullRequest={(_pull?.GetValue(f) == null ? "NONE" : "ACTIVE")} items={f.FilteredItems?.Count}";
        }
        catch (Exception ex) { return "state error: " + ex.Message; }
    }

    private static void HInit(Action<FilterComponent, FilterComponentObjectBuilder> orig, FilterComponent self, FilterComponentObjectBuilder ob)
    {
        try { Pumps.Register(self); } catch { }
        orig(self, ob);
        Log.Write("DIAG Init: " + State(self));
    }

    private static void HDrain(Action<FilterComponent, bool> orig, FilterComponent self, bool enabled)
    {
        orig(self, enabled);
        Log.Write($"DIAG UpdateDrainAll(enabled/powered={enabled}): " + State(self));
    }

    private static void HCreate(Action<FilterComponent> orig, FilterComponent self)
    {
        orig(self);
        Log.Write("DIAG CreatePersistentRequest: " + State(self) + (_pull?.GetValue(self) == null ? "  <-- grid has no PersistentRequestProcessorComponent or request refused" : ""));
    }

    private static void HSetFilter(Action<FilterComponent, System.Collections.Generic.HashSet<Keen.Game2.Simulation.WorldObjects.Items.ItemDefinition>, bool> orig,
                                   FilterComponent self, System.Collections.Generic.HashSet<Keen.Game2.Simulation.WorldObjects.Items.ItemDefinition> set, bool wl)
    {
        orig(self, set, wl);
        Log.Write($"DIAG SetNewFilter(whitelist={wl}, {set?.Count} items): " + State(self));
    }
}
