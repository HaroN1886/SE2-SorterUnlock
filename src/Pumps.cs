using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Conveyors;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Conveyors;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.PersistentRequests;
using Keen.Game2.Simulation.WorldObjects.Items;
using MonoMod.RuntimeDetour;

namespace SorterUnlock;

/// <summary>
/// 0.6.0: server-side plumbing.
///  - registry of all sorters (FilterComponent);
///  - a server-thread tick: we piggyback on the in-process server transport job that applies client changes
///    (the same place Keen itself applies sorter settings), and run our queued work there;
///  - "pump" helpers: is a sorter actively draining, wake/restart drains;
///  - maintenance ("перекачка") mode with timer and auto-finish.
/// </summary>
internal static class Pumps
{
    private static readonly List<object> Keep = new();
    private static readonly object Gate = new();
    private static readonly List<WeakReference<FilterComponent>> All = new();
    private static readonly ConcurrentQueue<Action> Queue = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static FieldInfo? _pullRequest;
    private static MethodInfo? _getSystem, _dispose, _create;
    private static FieldInfo? _reqPullIdx, _reqSys, _reqFilter;
    private static bool _tickSeen;

    public static double Now => Clock.Elapsed.TotalSeconds;
    public static bool TickAlive => _tickSeen;

    public static void Install()
    {
        const BindingFlags I = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var t = typeof(FilterComponent);
        _pullRequest = t.GetField("_pullRequest", I);
        _getSystem = t.GetMethod("GetConveyorSystem", I);
        _dispose = t.GetMethod("DisposeRequest", I);
        _create = t.GetMethod("CreatePersistentRequest", I);
        var rt = typeof(PersistentRequestFilterPullThrough);
        _reqPullIdx = rt.GetField("_pullNodeIndex", I);
        _reqSys = rt.GetField("_system", I);
        _reqFilter = rt.GetField("_filter", I);

        try
        {
            var asm = typeof(Keen.VRage.Multiplayer.SecureIds.EntityId).Assembly;
            var baseT = asm.GetType("Keen.VRage.Multiplayer.Transport.InProcess.InProcessPeerTransportSessionComponent")!;
            var m = baseT.GetMethod("ProcessFromRemote", I)!;
            Keep.Add(new Hook(m, typeof(Pumps).GetMethod(nameof(HProcessFromRemote), BindingFlags.Static | BindingFlags.NonPublic)!));
            Log.Write("PUMPS: server tick hook installed");
            Keep.Add(new Hook(_create!, typeof(Pumps).GetMethod(nameof(HCreate), BindingFlags.Static | BindingFlags.NonPublic)!));
        }
        catch (Exception ex) { Log.Error("PUMPS tick install", ex); }
    }

    // ---------------- registry ----------------
    public static void Register(FilterComponent f)
    {
        lock (Gate)
        {
            All.RemoveAll(w => !w.TryGetTarget(out _));
            All.Add(new WeakReference<FilterComponent>(f));
        }
    }

    public static List<FilterComponent> Snapshot()
    {
        var res = new List<FilterComponent>();
        lock (Gate)
            foreach (var w in All)
                if (w.TryGetTarget(out var f)) res.Add(f);
        return res;
    }

    public static PersistentRequestFilterPullThrough? RequestOf(FilterComponent f)
        => _pullRequest?.GetValue(f) as PersistentRequestFilterPullThrough;

    public static FilterComponent? FilterOf(PersistentRequestFilterPullThrough r) => _reqFilter?.GetValue(r) as FilterComponent;

    public static ConveyorSystemComponent? SystemOf(FilterComponent f)
    {
        try { return _getSystem?.Invoke(f, null) as ConveyorSystemComponent; } catch { return null; }
    }

    /// <summary>Graph nodes of sorters that are pumping right now (Drain All on and running).</summary>
    public static HashSet<(int, ConveyorSystemComponent)> ActivePumpNodes()
    {
        var res = new HashSet<(int, ConveyorSystemComponent)>();
        foreach (var f in Snapshot())
        {
            var r = RequestOf(f);
            if (r == null) continue;
            var idx = _reqPullIdx?.GetValue(r) as int?;
            var sys = _reqSys?.GetValue(r) as ConveyorSystemComponent ?? SystemOf(f);
            if (sys == null) continue;
            idx ??= ConveyorElementTranslator.TryGetNodeIndex(f, sys);
            if (idx.HasValue) res.Add((idx.Value, sys));
        }
        return res;
    }

    // ---------------- server tick ----------------
    public static void Post(Action a) => Queue.Enqueue(a);

    private static void HProcessFromRemote(Action<object> orig, object self)
    {
        orig(self);
        if (self.GetType().Name != "InProcessServerPeerTransportSessionComponent") return;
        if (!_tickSeen) { _tickSeen = true; Log.Write("PUMPS: server tick alive"); }
        try
        {
            int n = 0;
            while (n++ < 256 && Queue.TryDequeue(out var a))
            {
                try { a(); } catch (Exception ex) { Log.Error("PUMPS queued action", ex); }
            }
            Maintenance.Tick();
        }
        catch (Exception ex) { Log.Error("PUMPS tick", ex); }
    }

    public static void Suspend(FilterComponent f) { if (RequestOf(f) != null) _dispose?.Invoke(f, null); }
    public static void Resume(FilterComponent f) { if (RequestOf(f) == null) _create?.Invoke(f, null); }

    private static void HCreate(Action<FilterComponent> orig, FilterComponent self)
    {
        if (Maintenance.IsInMaintenance(self)) return; // stays quiet until maintenance ends
        orig(self);
    }

    /// <summary>Restart a sorter's drain request (what the user does with the Drain All toggle). Server thread only.</summary>
    public static void Restart(FilterComponent f)
    {
        if (RequestOf(f) == null || Maintenance.IsInMaintenance(f)) return;
        _dispose?.Invoke(f, null);
        _create?.Invoke(f, null);
    }

    /// <summary>Restart every active drain on the same conveyor network. Server thread only.</summary>
    public static void RestartNetwork(FilterComponent around)
    {
        var sys = SystemOf(around);
        foreach (var f in Snapshot())
        {
            if (RequestOf(f) == null) continue;
            var s = SystemOf(f);
            if (sys != null && s != null && s != sys && !Connected(s, sys)) continue;
            try { Restart(f); } catch (Exception ex) { Log.Error("PUMPS restart", ex); }
        }
    }

    private static bool Connected(ConveyorSystemComponent a, ConveyorSystemComponent b)
    {
        try
        {
            var m = typeof(ConveyorSystemComponent).GetMethod("AreSystemsConnected", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return m != null && (bool)m.Invoke(null, new object[] { a, b })!;
        }
        catch { return true; }
    }

    // pump activity, for auto-finish
    private static double _lastPumpRun;
    public static void NotePumpRun() => _lastPumpRun = Now;
    public static double LastPumpRun => _lastPumpRun;
}

/// <summary>"Перекачка": the sorter stops pumping and becomes an open pipe both ways for a while. Saved settings are not touched.</summary>
internal static class Maintenance
{
    internal sealed class State
    {
        public FilterComponent F = null!;
        public bool HadRequest;
        public double Started, Until;
        public bool AutoFinish;
    }

    private static readonly object Gate = new();
    private static readonly List<State> Active = new();
    public const double IdleSeconds = 3.0;

    public static State? Get(FilterComponent f)
    {
        lock (Gate) return Active.FirstOrDefault(s => s.F == f);
    }

    public static bool IsInMaintenance(FilterComponent f)
    {
        lock (Gate) return Active.Count > 0 && Active.Any(s => s.F == f);
    }

    public static void Start(FilterComponent f, double seconds, bool autoFinish)
    {
        Pumps.Post(() =>
        {
            var existing = Get(f);
            if (existing != null) { existing.Until = Pumps.Now + seconds; existing.AutoFinish = autoFinish; return; }
            var st = new State { F = f, HadRequest = Pumps.RequestOf(f) != null, Started = Pumps.Now, Until = Pumps.Now + seconds, AutoFinish = autoFinish };
            lock (Gate) Active.Add(st);
            Pumps.Suspend(f);
            Reapply(f);
            Pumps.RestartNetwork(f); // wake the other pumps: a new path just opened
            Log.Write($"MAINT: start {seconds:0}s (auto-finish={autoFinish}), pump was {(st.HadRequest ? "on" : "off")}");
        });
    }

    public static void Extend(FilterComponent f, double seconds) => Pumps.Post(() => { var s = Get(f); if (s != null) s.Until += seconds; });

    public static void Stop(FilterComponent f) => Pumps.Post(() => { var s = Get(f); if (s != null) Finish(s, "manual"); });

    public static void Tick()
    {
        List<State> list;
        lock (Gate) { if (Active.Count == 0) return; list = Active.ToList(); }
        double now = Pumps.Now;
        foreach (var s in list)
        {
            if (now >= s.Until) { Finish(s, "timer"); continue; }
            if (s.AutoFinish && now - s.Started > IdleSeconds && now - Math.Max(s.Started, Pumps.LastPumpRun) > IdleSeconds)
                Finish(s, "pumps idle");
        }
    }

    /// <summary>Push the sorter's (maintenance-aware) rules into the conveyor graph.</summary>
    private static void Reapply(FilterComponent f)
    {
        var sys = Pumps.SystemOf(f);
        if (sys == null) return;
        ConveyorElementTranslator.SetFilter(f, sys);                       // hooked -> FilterSync.ApplyGraph
        ConveyorElementTranslator.SetOppositeFLow(f, sys, f.AllowOppositeFlow); // hooked -> forced open during maintenance
    }

    private static void Finish(State s, string why)
    {
        lock (Gate) Active.Remove(s);
        try
        {
            Reapply(s.F);
            if (s.HadRequest && s.F.DrainAll) Pumps.Resume(s.F);
            Log.Write($"MAINT: finished ({why}), sorter back to its own settings");
        }
        catch (Exception ex) { Log.Error("MAINT finish", ex); }
    }
}
