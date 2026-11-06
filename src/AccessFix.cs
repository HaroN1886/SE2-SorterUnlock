using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Keen.Game2.Simulation.StreamedUI.Terminal.Inventories;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Conveyors;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.PersistentRequests;
using Keen.Game2.Simulation.WorldObjects.Items;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using KTask = Keen.VRage.Library.Threading.Task<bool>;

namespace SorterUnlock;

/// <summary>
/// 0.2.8:
///  (1) Player terminal: inventory connectivity ignores sorter direction/filter (hand access works everywhere),
///      automation is untouched.
///  (2) Drain All: never pull from inventories on the sorter's output side (no ping-pong with opposite flow).
/// </summary>
internal static class AccessFix
{
    private static readonly List<ILHook> IlHooks = new();
    private static Hook? _runHook;

    private static FieldInfo? _pullIdx, _pushIdx, _sys;

    [ThreadStatic] private static PersistentRequestFilterPullThrough? _curReq;

    private static int _playerOverrides;

    public static void Install()
    {
        InstallPlayerAccess();
        InstallPingPong();
    }

    // ---------------- graph helpers ----------------

    /// <summary>BFS over the conveyor graph (incl. links to other grids), ignoring filters. blocked node is never entered.</summary>
    private static IEnumerable<(int node, ConveyorSystemComponent sys)> Walk(int start, ConveyorSystemComponent startSys, int? blocked, ConveyorSystemComponent? blockedSys, bool includeStart)
    {
        var seen = new HashSet<(int, ConveyorSystemComponent)>();
        var stack = new Stack<(int, ConveyorSystemComponent)>();
        stack.Push((start, startSys));
        seen.Add((start, startSys));
        if (blocked.HasValue && blockedSys != null) seen.Add((blocked.Value, blockedSys));
        while (stack.Count > 0)
        {
            var (idx, sys) = stack.Pop();
            var g = sys.ConveyorGraph;
            var node = g.GetNode(idx);
            if (node == null || !node.IsEnabled) continue;
            if (includeStart || idx != start || sys != startSys) yield return (idx, sys);
            foreach (var kv in node.Edges)
            {
                if (kv.Value.IsEnabledMultiplicity <= 0) continue;
                if (seen.Add((kv.Key, sys))) stack.Push((kv.Key, sys));
            }
            if (node.LinkDecoration.HasValue)
            {
                var link = g.GetLink(node.LinkDecoration.Value);
                if (link != null && link.OtherSystem != null && seen.Add((link.OtherNodeId, link.OtherSystem)))
                    stack.Push((link.OtherNodeId, link.OtherSystem));
            }
        }
    }

    // ---------------- (1) player access ----------------

    private static void InstallPlayerAccess()
    {
        try
        {
            var target = typeof(ConveyorSystemComponent).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == "AreConnectedAsync" && m.GetParameters().Length == 5);
            var repl = typeof(AccessFix).GetMethod(nameof(AreConnectedForPlayer), BindingFlags.Public | BindingFlags.Static)!;

            var root = typeof(StreamedInventorySessionComponent);
            var types = new List<Type> { root };
            types.AddRange(root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            int patched = 0;
            foreach (var t in types)
            foreach (var m in t.GetMethods(all))
            {
                if (m.IsAbstract || m.ContainsGenericParameters || !CallsMethod(m, target)) continue;
                try
                {
                    var name = t.Name + "." + m.Name;
                    IlHooks.Add(new ILHook(m, il =>
                    {
                        var c = new ILCursor(il);
                        int n = 0;
                        while (c.TryGotoNext(i => i.MatchCall(target)))
                        {
                            c.Next!.Operand = il.Import(repl);
                            n++;
                        }
                        Log.Write($"ACCESS: patched {n} connectivity check(s) in {name}");
                    }));
                    patched++;
                }
                catch (Exception ex) { Log.Error("ACCESS ilhook " + t.Name + "." + m.Name, ex); }
            }
            Log.Write($"ACCESS: player terminal connectivity override installed in {patched} method(s)");
        }
        catch (Exception ex) { Log.Error("ACCESS install", ex); }
    }

    private static bool CallsMethod(MethodInfo m, MethodInfo target)
    {
        try
        {
            var body = m.GetMethodBody()?.GetILAsByteArray();
            if (body == null) return false;
            int token = target.MetadataToken;
            // cheap scan for the call/callvirt token; ILHook manipulator does the precise match
            for (int i = 0; i + 4 < body.Length; i++)
            {
                if ((body[i] == 0x28 || body[i] == 0x6F) && BitConverter.ToInt32(body, i + 1) == token)
                {
                    try { if (m.Module.ResolveMethod(token) == target) return true; } catch { }
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>Same signature as ConveyorSystemComponent.AreConnectedAsync, but sorters do not block the player.</summary>
    public static KTask AreConnectedForPlayer(int nodeIdxFrom, ConveyorSystemComponent systemFrom, int nodeIdxTo, ConveyorSystemComponent systemTo, ItemDefinition? filterItem)
    {
        try
        {
            if (systemFrom == null || systemTo == null) return new KTask(false);
            if (nodeIdxFrom == nodeIdxTo && systemFrom == systemTo) return new KTask(systemFrom.ConveyorGraph.GetNode(nodeIdxFrom).IsEnabled);
            if (!systemTo.ConveyorGraph.GetNode(nodeIdxTo).IsEnabled) return new KTask(false);
            foreach (var (n, s) in Walk(nodeIdxFrom, systemFrom, null, null, includeStart: false))
            {
                if (n == nodeIdxTo && s == systemTo)
                {
                    if (_playerOverrides++ < 20) Log.Write($"ACCESS: player connectivity {nodeIdxFrom}->{nodeIdxTo} = true (direction ignored)");
                    return new KTask(true);
                }
            }
            return new KTask(false);
        }
        catch (Exception ex)
        {
            Log.Error("ACCESS check, falling back to stock", ex);
            return ConveyorSystemComponent.AreConnectedAsync(nodeIdxFrom, systemFrom, nodeIdxTo, systemTo, filterItem);
        }
    }

    // ---------------- (2) ping-pong ----------------

    private static void InstallPingPong()
    {
        try
        {
            var t = typeof(PersistentRequestFilterPullThrough);
            const BindingFlags I = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            _pullIdx = t.GetField("_pullNodeIndex", I);
            _pushIdx = t.GetField("_pushNodeIndex", I);
            _sys = t.GetField("_system", I);

            var run = t.GetMethod("Run", I, null, new[] { typeof(Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Inventories.InventorySystemComponent) }, null)!;
            _runHook = new Hook(run, typeof(AccessFix).GetMethod(nameof(HRun), BindingFlags.Static | BindingFlags.NonPublic)!);

            var sm = t.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public).Single(x => x.Name.Contains("RunPull"));
            var moveNext = sm.GetMethod("MoveNext", I)!;
            var hasAny = typeof(InventoryComponent).GetMethod("HasAnyItem", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)!;
            var repl = typeof(AccessFix).GetMethod(nameof(PullCandidate), BindingFlags.Public | BindingFlags.Static)!;
            IlHooks.Add(new ILHook(moveNext, il =>
            {
                var c = new ILCursor(il);
                // first HasAnyItem() = "collect pull inventories" loop
                if (c.TryGotoNext(i => i.MatchCallOrCallvirt(hasAny)))
                {
                    c.Next!.OpCode = OpCodes.Call;
                    c.Next.Operand = il.Import(repl);
                    Log.Write("PINGPONG: pull-side filter patched into RunPull");
                }
                else Log.Write("PINGPONG: HasAnyItem call not found, ping-pong fix inactive");

                // every per-item connectivity check in RunPull -> also respect production reservations
                var areConn = typeof(Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Inventories.InventorySystemComponent)
                    .GetMethod("AreConnectedAsync", BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(InventoryComponent), typeof(InventoryComponent), typeof(ItemDefinition) }, null)!;
                var areRepl = typeof(AccessFix).GetMethod(nameof(DrainCanMove), BindingFlags.Public | BindingFlags.Static)!;
                var c2 = new ILCursor(il);
                int n = 0;
                var thisFld = sm.GetField("<>4__this", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
                while (c2.TryGotoNext(i => i.MatchCall(areConn)))
                {
                    c2.Emit(OpCodes.Ldarg_0);          // state machine
                    c2.Emit(OpCodes.Ldfld, thisFld);   // the request
                    c2.Next!.Operand = il.Import(areRepl);
                    c2.Index++;
                    n++;
                }
                Log.Write($"RESERVE+PUSHSIDE: patched {n} transfer check(s) in RunPull (reservations respected, targets only on the output side)");
            }));
        }
        catch (Exception ex) { Log.Error("PINGPONG install", ex); }
    }

    private static int _reserveLogs;
    private static Func<InventoryComponent, ItemDefinition, bool>? _canPull;

    /// <summary>Replaces InventorySystemComponent.AreConnectedAsync in RunPull: items a production block reserved (IItemRequestModifier.CanPullFrom) are left alone.</summary>
    private static int _pushLogs, _backLogs, _homeLogs;
    public static KTask DrainCanMove(InventoryComponent invFrom, InventoryComponent invTo, ItemDefinition? itemDef, PersistentRequestFilterPullThrough req)
    {
        try
        {
            var info = InfoOf(req);
            var front = info?.Set;
            if (front != null && front.Count > 0)
            {
                // drain = move ACROSS the sorter: from the back side to the output side only
                if (!front.Contains(invTo) || front.Contains(invFrom))
                {
                    if (_pushLogs++ < 20) Log.Write($"PUSHSIDE: skip {itemDef} {invFrom?.Entity} -> {invTo?.Entity} (not across the sorter)");
                    return new KTask(false);
                }
            }
            // rule 1: a pump never sucks backwards through another sorter
            if (info?.Back != null && invFrom != null && !info.Back.Contains(invFrom))
            {
                if (_backLogs++ < 20) Log.Write($"BACKFLOW: skip {itemDef} from {invFrom.Entity} (only reachable backwards through a working pump)");
                return new KTask(false);
            }
            // rule 2: specific pump first on a shared source
            if (itemDef != null && invFrom != null && YieldToSpecific(req, invFrom, itemDef))
            {
                if (_homeLogs++ < 20) Log.Write($"PRIORITY: {itemDef} in {invFrom.Entity} left for a sorter that whitelists it");
                return new KTask(false);
            }
            if (itemDef != null && invFrom?.Entity != null)
            {
                _canPull ??= (Func<InventoryComponent, ItemDefinition, bool>)Delegate.CreateDelegate(typeof(Func<InventoryComponent, ItemDefinition, bool>),
                    typeof(Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Inventories.InventorySystemComponent)
                        .GetMethod("CanPullFrom", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(InventoryComponent), typeof(ItemDefinition) }, null)!);
                if (!_canPull(invFrom, itemDef))
                {
                    if (_reserveLogs++ < 30) Log.Write($"RESERVE: skip {itemDef} in {invFrom.Entity} (reserved by production block)");
                    return new KTask(false);
                }
            }
        }
        catch (Exception ex) { Log.Error("RESERVE check", ex); }
        return Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Inventories.InventorySystemComponent.AreConnectedAsync(invFrom, invTo, itemDef);
    }

    private static void HRun(Action<PersistentRequestFilterPullThrough, Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Inventories.InventorySystemComponent> orig,
                             PersistentRequestFilterPullThrough self, Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Inventories.InventorySystemComponent system)
    {
        var prevReq = _curReq;
        _curReq = self;
        Pumps.NotePumpRun();
        FrontCache.Remove(self); // recompute once per run, the grid may have changed
        try { orig(self, system); }
        finally { _curReq = prevReq; }
    }

    /// <summary>Replaces inv.HasAnyItem() in the pull-collection loop: also rejects inventories on the sorter's output side.</summary>
    public static bool PullCandidate(InventoryComponent inv)
    {
        if (!inv.HasAnyItem()) return false;
        var req = _curReq;
        if (req == null) return true;
        var info = InfoOf(req);
        if (info == null) return true;
        if (info.Set != null && info.Set.Contains(inv)) return false;
        if (info.Back != null && !info.Back.Contains(inv)) return false;
        return true;
    }

    private sealed class FrontBox
    {
        public HashSet<InventoryComponent>? Set;   // output side
        public HashSet<InventoryComponent>? Back;  // input side, never crossing another sorter backwards
        public double Stamp;
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PersistentRequestFilterPullThrough, FrontBox> FrontCache = new();

    /// <summary>Inventories on the sorter's output side: reachable from the push node without passing the sorter node.</summary>
    private static HashSet<InventoryComponent>? FrontOf(PersistentRequestFilterPullThrough? req) => InfoOf(req)?.Set;

    private static FrontBox? InfoOf(PersistentRequestFilterPullThrough? req)
    {
        if (req == null) return null;
        if (FrontCache.TryGetValue(req, out var box) && Pumps.Now - box.Stamp < 2.0) return box;
        box = new FrontBox { Stamp = Pumps.Now };
        try
        {
            var pull = (int?)_pullIdx!.GetValue(req);
            var push = (int?)_pushIdx!.GetValue(req);
            var sys = (ConveyorSystemComponent?)_sys!.GetValue(req);
            if (pull.HasValue && push.HasValue && sys != null)
            {
                var set = new HashSet<InventoryComponent>();
                foreach (var (n, s) in Walk(push.Value, sys, pull.Value, sys, includeStart: true))
                {
                    var node = s.ConveyorGraph.GetNode(n);
                    if (node.InventoryDecoration.HasValue)
                        foreach (var i in s.ConveyorGraph.GetInventories(node.InventoryDecoration.Value)) set.Add(i);
                }
                box.Set = set;
                try { box.Back = BackSide(pull.Value, sys, Pumps.ActivePumpNodes()); } catch (Exception ex) { Log.Error("BACK compute", ex); }
            }
        }
        catch (Exception ex) { Log.Error("FRONT compute", ex); }
        FrontCache.AddOrUpdate(req, box);
        return box;
    }

    private static void AddInvs(ConveyorSystemComponent s, int n, HashSet<InventoryComponent> into)
    {
        var node = s.ConveyorGraph.GetNode(n);
        if (node.InventoryDecoration.HasValue)
            foreach (var i in s.ConveyorGraph.GetInventories(node.InventoryDecoration.Value)) into.Add(i);
    }

    private static ConveyorFilter? FilterAt(ConveyorSystemComponent s, int n)
    {
        var node = s.ConveyorGraph.GetNode(n);
        return node.FilterDecoration.HasValue ? s.ConveyorGraph.GetFilter(node.FilterDecoration.Value) : null;
    }

    /// <summary>Inputs of a sorter: walk away from its back side; other sorters may be passed only front->back (= items flowing forward through them).</summary>
    private static HashSet<InventoryComponent> BackSide(int sorter, ConveyorSystemComponent sys, HashSet<(int, ConveyorSystemComponent)> activePumps)
    {
        var res = new HashSet<InventoryComponent>();
        var own = FilterAt(sys, sorter);
        int? ownFront = own?.Direction;
        var seen = new HashSet<(int, ConveyorSystemComponent, bool)>();
        var stack = new Stack<(int n, ConveyorSystemComponent s, int prev)>();
        foreach (var kv in sys.ConveyorGraph.GetNode(sorter).Edges)
            if (kv.Value.IsEnabledMultiplicity > 0 && kv.Key != ownFront) stack.Push((kv.Key, sys, sorter));
        while (stack.Count > 0)
        {
            var (n, s, prev) = stack.Pop();
            if (n == sorter && s == sys) continue;
            var node = s.ConveyorGraph.GetNode(n);
            if (node == null || !node.IsEnabled) continue;
            var f = node.FilterDecoration.HasValue ? s.ConveyorGraph.GetFilter(node.FilterDecoration.Value) : null;
            bool directed = f != null && f.IsDirectionActive && f.Direction.HasValue && activePumps.Contains((n, s));
            bool fromFront = directed && prev == f!.Direction;
            if (!seen.Add((n, s, fromFront))) continue;
            AddInvs(s, n, res);
            foreach (var kv in node.Edges)
            {
                if (kv.Value.IsEnabledMultiplicity <= 0) continue;
                if (directed && !fromFront && kv.Key == f!.Direction) continue; // would mean pulling items backwards through a working pump
                stack.Push((kv.Key, s, n));
            }
            if (node.LinkDecoration.HasValue)
            {
                var link = s.ConveyorGraph.GetLink(node.LinkDecoration.Value);
                if (link?.OtherSystem != null) stack.Push((link.OtherNodeId, link.OtherSystem, -1));
            }
        }
        return res;
    }

    /// <summary>A pump that is not specific about this item leaves it to a pump that whitelists it, while that pump has room.</summary>
    private static bool YieldToSpecific(PersistentRequestFilterPullThrough req, InventoryComponent invFrom, ItemDefinition item)
    {
        var me = Pumps.FilterOf(req);
        if (me != null && me.IsWhitelist && me.FilteredItems != null && me.FilteredItems.Contains(item)) return false; // I'm specific too
        foreach (var p in Pumps.Snapshot())
        {
            if (p == me || !p.IsWhitelist) continue;
            var items = p.FilteredItems;
            if (items == null || !items.Contains(item)) continue;
            var pr = Pumps.RequestOf(p);
            if (pr == null || pr == req) continue;
            var pi = InfoOf(pr);
            if (pi?.Back == null || !pi.Back.Contains(invFrom)) continue;
            if (pi.Set != null && pi.Set.Any(i => (double)i.GetFillRatio() < 0.999)) return true;
        }
        return false;
    }
}
