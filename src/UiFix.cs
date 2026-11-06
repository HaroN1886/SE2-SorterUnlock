using System;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Reflection;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using MonoMod.RuntimeDetour;

namespace SorterUnlock;

/// <summary>
/// Vanilla design bug: FilterBlockDetailModel.ControlPanelFilterItems is an ImmutableArray (non-observable container)
/// of ItemElement (ObservableObject). The replication validator rejects that ("Observable chains cannot pass through
/// non-observable links") and the whole sorter panel stops syncing. We empty the list on the server model so the
/// rest of the panel (whitelist / drain all / directed / opposite flow) replicates normally.
/// </summary>
internal static class UiFix
{
    private static Hook? _ctorHook, _propHook;
    private static PropertyInfo? _itemsProp;
    private static readonly Type ServerModel = typeof(FilterBlockDetailModel).Assembly
        .GetType("Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails.FilterBlockDetailModelServer", true)!;

    public static void Install()
    {
        try
        {
            _itemsProp = typeof(FilterBlockDetailModel).GetProperty("ControlPanelFilterItems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMemberException("ControlPanelFilterItems");
            ConstructorInfo ctor = ServerModel.GetConstructor(BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)
                ?? throw new MissingMethodException("FilterBlockDetailModelServer()");
            _ctorHook = new Hook(ctor, typeof(UiFix).GetMethod(nameof(HookedCtor), BindingFlags.Static | BindingFlags.NonPublic)!);
            Log.Write("Hook installed on FilterBlockDetailModelServer ctor (item list disabled to keep panel replicable)");
        }
        catch (Exception ex) { Log.Error("hook FilterBlockDetailModelServer ctor", ex); }

        try
        {
            MethodInfo m = ServerModel.GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(object), typeof(PropertyChangedEventArgs) }, null)
                ?? throw new MissingMethodException("OnPropertyChanged");
            _propHook = new Hook(m, typeof(UiFix).GetMethod(nameof(HookedPropChanged), BindingFlags.Static | BindingFlags.NonPublic)!);
            Log.Write("Hook installed on FilterBlockDetailModelServer.OnPropertyChanged (diagnostics)");
        }
        catch (Exception ex) { Log.Error("hook OnPropertyChanged", ex); }
    }

    private static void HookedCtor(Action<FilterBlockDetailModel> orig, FilterBlockDetailModel self)
    {
        orig(self);
        try
        {
            _itemsProp!.SetValue(self, ImmutableArray<ItemElement>.Empty);
            Log.Write("UI: sorter panel opened (item list hidden, toggles active)");
        }
        catch (Exception ex) { Log.Error("clearing item list", ex); }
    }

    private static void HookedPropChanged(Action<FilterBlockDetailModel, object?, PropertyChangedEventArgs> orig,
                                          FilterBlockDetailModel self, object? sender, PropertyChangedEventArgs e)
    {
        orig(self, sender, e);
        try
        {
            Log.Write($"UI: {e.PropertyName} changed -> whitelist={self.ControlPanelFilterIsWhitelist} drainAll={self.ControlPanelFilterDrainAll} directed={self.ControlPanelFilterIsDirected} oppositeFlow={self.AllowOppositeFlow}");
        }
        catch { }
    }
}
