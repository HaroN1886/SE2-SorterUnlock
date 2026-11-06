using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Keen.Game2.Client.UI.TerminalScreen.ControlPanel;
using Keen.Game2.Client.UI.TerminalScreen.ControlPanel.PropertyElements;
using Keen.Game2.Simulation.GameSystems.Definitions;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Conveyors;
using Keen.Game2.Simulation.WorldObjects.Items;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Localization;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer.SecureIds;
using Keen.VRage.UI.Shared.ObjectInspector.PropertyElements;
using MonoMod.RuntimeDetour;

namespace SorterUnlock;

/// <summary>Marker element appended to the terminal inspector when a sorter is shown.</summary>
public sealed class SorterFilterElement : PropertyElement
{
    public FilterBlockDetailModel? Model;
    public SorterFilterElement(Keen.VRage.UI.Shared.ObjectInspector.Core.ObjectInspectorViewModel parent, FilterBlockDetailModel? model) : base(parent) { Model = model; }
}

/// <summary>0.3.0: own item filter panel inside the stock sorter control panel.</summary>
internal static class FilterPanel
{
    private static readonly List<object> Keep = new();
    private static FieldInfo? _inspectedBlocks;

    public static void Install()
    {
        try
        {
            _inspectedBlocks = typeof(ControlPanelViewModel).GetField("_inspectedBlocks", BindingFlags.Instance | BindingFlags.NonPublic);
            var show = typeof(TerminalInspectorViewModel).GetMethod("Show", new[] { typeof(string), typeof(List<Keen.VRage.Library.Serialization.IAccessible>) })!;
            Keep.Add(new Hook(show, typeof(FilterPanel).GetMethod(nameof(HShow), BindingFlags.Static | BindingFlags.NonPublic)!));
            var build = typeof(TerminalPropertyElementSelector).GetMethod("Build", new[] { typeof(object) })!;
            Keep.Add(new Hook(build, typeof(FilterPanel).GetMethod(nameof(HBuild), BindingFlags.Static | BindingFlags.NonPublic)!));
            Log.Write("PANEL: item filter panel hooks installed");
        }
        catch (Exception ex) { Log.Error("PANEL install", ex); }
    }

    private static void HShow(Action<TerminalInspectorViewModel, string, List<Keen.VRage.Library.Serialization.IAccessible>> orig,
                              TerminalInspectorViewModel self, string title, List<Keen.VRage.Library.Serialization.IAccessible> inspectables)
    {
        orig(self, title, inspectables);
        try
        {
            var model = inspectables?.OfType<FilterBlockDetailModel>().FirstOrDefault();
            if (model != null)
            {
                HideStockWhitelist(self);
                self.PropertyElements.Add(new SorterFilterElement(self, model));
                Log.Write("PANEL: filter panel added to sorter controls");
            }
        }
        catch (Exception ex) { Log.Error("PANEL show", ex); }
    }

    private static bool _loggedLabels;
    /// <summary>Remove the stock "whitelist" toggle: our mode switch drives the same property.</summary>
    private static void HideStockWhitelist(TerminalInspectorViewModel self)
    {
        var list = self.PropertyElements;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (MemberName(list[i]) == "ControlPanelFilterIsWhitelist")
            {
                list.RemoveAt(i);
                Log.Write("PANEL: stock whitelist toggle hidden");
                return;
            }
        }
        if (!_loggedLabels)
        {
            _loggedLabels = true;
            Log.Write("PANEL: stock whitelist toggle not found; elements: " + string.Join(" | ", list.Select(e => e.GetType().Name + ":" + (MemberName(e) ?? e.Label))));
        }
    }

    private static string? MemberName(object el)
    {
        try
        {
            foreach (var p in el.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                if (t.Name != "MemberReference") continue;
                var v = p.GetValue(el);
                if (v == null) continue;
                var mi = v.GetType().GetProperty("MemberInfo")?.GetValue(v) as MemberInfo;
                if (mi != null) return mi.Name;
            }
        }
        catch { }
        return null;
    }

    private static Control HBuild(Func<TerminalPropertyElementSelector, object?, Control> orig, TerminalPropertyElementSelector self, object? param)
    {
        if (param is SorterFilterElement el)
        {
            try { return new FilterView(el.Model); }
            catch (Exception ex) { Log.Error("PANEL build", ex); return new TextBlock { Text = Lang.T("build_fail") }; }
        }
        return orig(self, param);
    }

    /// <summary>EntityIds of the blocks currently shown in the control panel.</summary>
    internal static List<EntityId> SelectedIds(Control from)
    {
        var res = new List<EntityId>();
        foreach (var v in from.GetVisualAncestors())
        {
            if (v is StyledElement se && se.DataContext is ControlPanelViewModel vm && _inspectedBlocks?.GetValue(vm) is System.Collections.IEnumerable list)
            {
                foreach (var b in list)
                    if (b is ControlPanelEntityViewModel evm) res.Add(evm.Model.EntityId);
                break;
            }
        }
        return res;
    }
}

internal sealed class ItemInfo
{
    public ItemDefinition Def = null!;
    public string Name = "";
    public int Group;
    public CheckBox Box = null!;
}

/// <summary>The panel itself, built in code (no XAML). 0.6.2: all texts via Lang, rebuilt when the game language changes.</summary>
internal sealed class FilterView : StackPanel
{
    // group flag, game localization key (official translation), our fallback key
    private static readonly (ItemTypes flag, string gameKey, string ownKey)[] Groups =
    {
        (ItemTypes.Ore, "VoxelHandMaterialsOres", "grp_ore"),
        (ItemTypes.Material, "ItemConverterIngotsCat", "grp_ingots"),
        (ItemTypes.Component, "ItemConverterComponentsCat", "grp_components"),
        (ItemTypes.Item, "ItemConverterToolsCat", "grp_tools"),
        (ItemTypes.Consumable, "ItemConverterConsumablesCat", "grp_consumables"),
        (ItemTypes.BlockSchematic, "Blueprints", "grp_blueprints"),
        (ItemTypes.Datapad, "TerminalScreenHeaderDatapads", "grp_datapads"),
        (ItemTypes.Quest, "", "grp_quest"),
    };

    private static string GroupTitle(int g)
    {
        if (g >= Groups.Length) return Lang.T("grp_other");
        var x = Groups[g];
        return x.gameKey.Length > 0 ? Lang.Game(x.gameKey, x.ownKey) : Lang.T(x.ownKey);
    }

    private readonly FilterBlockDetailModel? _model;
    private readonly List<ItemInfo> _items = new();
    private readonly List<(StackPanel root, CheckBox all, TextBlock count, WrapPanel body, ToggleButton expand, int group)> _groups = new();
    private readonly List<FilterComponent> _targets = new();
    private ComboBox _mode = null!;
    private bool _syncingMode;
    private TextBlock _hint = null!, _warn = null!, _status = null!;
    private TextBox _search = null!;
    private bool _suppress;
    private string _builtFor = "";
    private bool _attached;

    public FilterView(FilterBlockDetailModel? model)
    {
        _model = model;
        Spacing = 4;
        Margin = new Thickness(0, 12, 0, 12);
        Build();

        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            Lang.EnsureHooked();
            Lang.Changed -= OnLangChanged;
            Lang.Changed += OnLangChanged;
            if (_builtFor != Lang.Culture) Build(); // language switched while the panel was hidden
            Bind();
            if (_model != null) _model.PropertyChanged += OnModelChanged;
            _maintTimer ??= new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _maintTimer.Tick -= OnMaintTimer;
            _maintTimer.Tick += OnMaintTimer;
            _maintTimer.Start();
            MaintTick();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            Lang.Changed -= OnLangChanged;
            if (_model != null) _model.PropertyChanged -= OnModelChanged;
            _maintTimer?.Stop();
        };
    }

    private void OnLangChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_attached) return;
            try
            {
                var q = _search?.Text;
                Build();
                Bind();
                if (!string.IsNullOrEmpty(q)) _search.Text = q;
                MaintTick();
            }
            catch (Exception ex) { Log.Error("PANEL relocalize", ex); }
        });
    }

    /// <summary>(Re)creates every control with texts in the current game language.</summary>
    private void Build()
    {
        _builtFor = Lang.Culture;
        Children.Clear();
        _items.Clear();
        _groups.Clear();

        Children.Add(new TextBlock { Text = Lang.T("title"), FontWeight = FontWeight.DemiBold, FontSize = 18 });

        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 4, 0, 4) };
        modes.Children.Add(new TextBlock { Text = Lang.T("mode_label"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 140 });
        _mode = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        _mode.ItemsSource = new[] { Lang.T("mode_pass"), Lang.T("mode_block") };
        modes.Children.Add(_mode);
        Children.Add(modes);
        _mode.SelectionChanged += (_, _) =>
        {
            if (_syncingMode || _mode.SelectedIndex < 0) return;
            SetWhitelist(_mode.SelectedIndex == 0);
        };
        SyncModeButtons();

        _hint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.DemiBold };
        _warn = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x40)) };
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
        Children.Add(_hint);
        Children.Add(_warn);
        Children.Add(_status);
        Children.Add(BuildMaintenance());

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var none = new Button { Content = Lang.T("clear_all") };
        none.Click += (_, _) => SetAll(false);
        var collapse = new Button { Content = Lang.T("collapse") };
        collapse.Click += (_, _) => { foreach (var g in _groups) g.expand.IsChecked = false; };
        tools.Children.Add(none);
        tools.Children.Add(collapse);
        Children.Add(tools);
        _search = new TextBox { Watermark = Lang.T("search"), Margin = new Thickness(0, 6, 0, 6) };
        Children.Add(_search);
        _search.TextChanged += (_, _) => ApplySearch();

        BuildCatalog();
    }

    private void OnMaintTimer(object? sender, EventArgs e) => MaintTick();

    // ---------- model (stock, replicated) ----------
    private bool IsWhitelist => _model?.ControlPanelFilterIsWhitelist ?? (_targets.Count > 0 && _targets[0].IsWhitelist);
    private bool DrainAll => _model?.ControlPanelFilterDrainAll ?? false;
    private bool Opposite => _model?.AllowOppositeFlow ?? false;

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (e.PropertyName == nameof(FilterBlockDetailModel.ControlPanelFilterIsWhitelist)) SyncModeButtons();
            UpdateHint();
        });
    }

    private void SetWhitelist(bool wl)
    {
        if (_model == null) { Log.Write("PANEL: no model, mode switch unavailable"); SyncModeButtons(); return; }
        if (_model.ControlPanelFilterIsWhitelist != wl)
        {
            _model.ControlPanelFilterIsWhitelist = wl; // replicated to the server like the stock toggle
            Log.Write($"PANEL: mode -> {(wl ? "pass selected (whitelist)" : "block selected (blacklist)")}");
        }
        SyncModeButtons(wl);
        UpdateHint(wl);
    }

    private void SyncModeButtons(bool? wl = null)
    {
        int idx = (wl ?? IsWhitelist) ? 0 : 1;
        if (_mode.SelectedIndex == idx) return;
        _syncingMode = true;
        try { _mode.SelectedIndex = idx; } finally { _syncingMode = false; }
    }

    // ---------- maintenance (перекачка) ----------
    private static double _lastSeconds = 30;
    private static bool _lastAuto = true;
    private Button _maintBtn = null!;
    private Border _maintPlate = null!;
    private TextBlock _maintText = null!;
    private Avalonia.Threading.DispatcherTimer? _maintTimer;

    private static string Fmt(double sec) { int t = (int)Math.Ceiling(Math.Max(0, sec)); return $"{t / 60}:{t % 60:00}"; }

    private Control BuildMaintenance()
    {
        _maintBtn = new Button { Content = Lang.T("maint_btn") };
        var slider = new Slider { Minimum = 10, Maximum = 300, TickFrequency = 10, IsSnapToTickEnabled = true, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var len = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 40 };
        var auto = new CheckBox { Content = Lang.T("maint_auto") };
        _maintPlate = new Border { Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0x40, 0x40, 0x90, 0xFF)) };
        _maintText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

        var box = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 4) };
        box.Children.Add(new TextBlock { Text = Lang.T("maint_title"), FontWeight = FontWeight.DemiBold });
        box.Children.Add(new TextBlock { Text = Lang.T("maint_desc"), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        slider.Value = _lastSeconds;
        len.Text = Fmt(_lastSeconds);
        slider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) { _lastSeconds = slider.Value; len.Text = Fmt(_lastSeconds); } };
        auto.IsChecked = _lastAuto;
        auto.IsCheckedChanged += (_, _) => _lastAuto = auto.IsChecked == true;
        row.Children.Add(_maintBtn);
        row.Children.Add(new TextBlock { Text = Lang.T("maint_for"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(slider);
        row.Children.Add(len);
        box.Children.Add(row);
        box.Children.Add(auto);

        var plateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var stop = new Button { Content = Lang.T("maint_stop") };
        var more = new Button { Content = Lang.T("maint_more") };
        plateRow.Children.Add(_maintText);
        plateRow.Children.Add(more);
        plateRow.Children.Add(stop);
        _maintPlate.Child = plateRow;
        _maintPlate.IsVisible = false;
        box.Children.Add(_maintPlate);

        _maintBtn.Click += (_, _) =>
        {
            if (_targets.Count == 0) return;
            foreach (var t in _targets) Maintenance.Start(t, _lastSeconds, _lastAuto);
            Log.Write($"PANEL: maintenance requested for {_targets.Count} sorter(s), {_lastSeconds:0}s");
        };
        stop.Click += (_, _) => { foreach (var t in _targets) Maintenance.Stop(t); };
        more.Click += (_, _) => { foreach (var t in _targets) Maintenance.Extend(t, 30); };
        return box;
    }

    private void MaintTick()
    {
        var st = _targets.Count > 0 ? Maintenance.Get(_targets[0]) : null;
        _maintPlate.IsVisible = st != null;
        _maintBtn.IsEnabled = st == null && _targets.Count > 0;
        if (st != null)
            _maintText.Text = Lang.F("maint_status", Fmt(st.Until - Pumps.Now)) + (st.AutoFinish ? Lang.T("maint_status_auto") : "");
        if (!Pumps.TickAlive && _targets.Count > 0) _maintBtn.IsEnabled = false;
    }

    // ---------- catalog ----------
    private static string Pretty(ItemDefinition d)
    {
        string? s = null;
        try { s = d.DisplayName.EvaluateText(); } catch { }
        if (string.IsNullOrWhiteSpace(s) || (!s.Contains(' ') && (s.Contains('_') || s.Contains('/') || s.StartsWith("Item", StringComparison.Ordinal))))
        {
            var raw = d.DebugNameShort ?? s ?? "?";
            raw = raw.Replace('_', ' ').Trim();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                if (i > 0 && char.IsUpper(raw[i]) && char.IsLower(raw[i - 1])) sb.Append(' ');
                sb.Append(raw[i]);
            }
            s = sb.ToString();
        }
        return s!;
    }

    private static int GroupOf(ItemDefinition d)
    {
        var t = d.Type;
        for (int i = 0; i < Groups.Length; i++)
            if ((t & Groups[i].flag) != 0) return i;
        return Groups.Length;
    }

    private void BuildCatalog()
    {
        var pp = Singleton<DefinitionManager>.Instance.GetPostProcessor<ItemDefinitionPostProcessor>();
        foreach (var d in pp.AllItems)
            _items.Add(new ItemInfo { Def = d, Name = Pretty(d), Group = GroupOf(d) });

        for (int g = 0; g <= Groups.Length; g++)
        {
            var members = _items.Where(i => i.Group == g).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (members.Count == 0) continue;
            string title = GroupTitle(g);

            var root = new StackPanel { Spacing = 2, Margin = new Thickness(0, 6, 0, 0) };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var expand = new ToggleButton { Content = "▸ " + title, MinWidth = 220, HorizontalContentAlignment = HorizontalAlignment.Left, IsChecked = g <= 2 };
            var all = new CheckBox { Content = Lang.T("all"), IsThreeState = true };
            var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
            header.Children.Add(expand);
            header.Children.Add(all);
            header.Children.Add(count);
            var body = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
            foreach (var it in members)
            {
                it.Box = new CheckBox { Content = it.Name, Width = 250, Margin = new Thickness(0, 0, 8, 0) };
                it.Box.IsCheckedChanged += (_, _) => { if (!_suppress) OnItemToggled(); };
                body.Children.Add(it.Box);
            }
            root.Children.Add(header);
            root.Children.Add(body);
            Children.Add(root);

            int gi = g;
            void SyncExpand() { body.IsVisible = expand.IsChecked == true || !string.IsNullOrWhiteSpace(_search.Text); expand.Content = (body.IsVisible ? "▾ " : "▸ ") + title; }
            expand.IsCheckedChanged += (_, _) => SyncExpand();
            all.IsCheckedChanged += (_, _) =>
            {
                if (_suppress || all.IsChecked == null) return;
                bool v = all.IsChecked == true;
                _suppress = true;
                foreach (var it in _items.Where(i => i.Group == gi && i.Box.IsVisible)) it.Box.IsChecked = v;
                _suppress = false;
                OnItemToggled();
            };
            _groups.Add((root, all, count, body, expand, gi));
            SyncExpand();
        }
    }

    // ---------- binding / applying ----------
    private void Bind()
    {
        _targets.Clear();
        var ids = FilterPanel.SelectedIds(this);
        foreach (var id in ids)
        {
            var f = FilterSync.Find(id);
            if (f != null) _targets.Add(f);
        }
        SyncModeButtons();
        if (_targets.Count == 0)
        {
            _status.Text = ids.Count == 0 ? Lang.T("no_sel") : Lang.T("no_server");
            IsEnabled = false;
            UpdateHint();
            Log.Write($"PANEL: bind failed, selected ids={ids.Count}");
            return;
        }
        IsEnabled = true;
        HashSet<ItemDefinition> current;
        try { current = new HashSet<ItemDefinition>(_targets[0].FilteredItems ?? new HashSet<ItemDefinition>()); }
        catch { current = new HashSet<ItemDefinition>(); }

        _suppress = true;
        foreach (var it in _items) it.Box.IsChecked = current.Contains(it.Def);
        _suppress = false;
        UpdateCounts();
        _status.Text = _targets.Count > 1 ? Lang.F("multi", _targets.Count) : "";
        _status.IsVisible = _status.Text.Length > 0;
        UpdateHint();
        Log.Write($"PANEL: bound to {_targets.Count} sorter(s), {current.Count} item(s) selected, whitelist={IsWhitelist}");
    }

    private void OnItemToggled()
    {
        UpdateCounts();
        UpdateHint();
        if (_targets.Count == 0) return;
        var set = new HashSet<ItemDefinition>(_items.Where(i => i.Box.IsChecked == true).Select(i => i.Def));
        int ok = _targets.Count(t => FilterSync.Apply(t, set));
        Log.Write($"PANEL: applied {set.Count} item(s) to {ok}/{_targets.Count} sorter(s)");
    }

    private void SetAll(bool v)
    {
        _suppress = true;
        foreach (var it in _items) it.Box.IsChecked = v;
        _suppress = false;
        OnItemToggled();
    }

    private void UpdateCounts()
    {
        _suppress = true;
        foreach (var g in _groups)
        {
            var members = _items.Where(i => i.Group == g.group).ToList();
            int on = members.Count(i => i.Box.IsChecked == true);
            g.count.Text = $"{on}/{members.Count}";
            g.all.IsChecked = on == 0 ? false : on == members.Count ? true : (bool?)null;
        }
        _suppress = false;
    }

    private void UpdateHint(bool? wlOverride = null)
    {
        bool wl = wlOverride ?? IsWhitelist;
        var sel = _items.Where(i => i.Box.IsChecked == true).Select(i => i.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        string list = sel.Count <= 4 ? string.Join(", ", sel) : Lang.F("more", string.Join(", ", sel.Take(4)), sel.Count - 4);

        if (sel.Count == 0) _hint.Text = Lang.T("hint_empty");
        else if (wl) _hint.Text = Lang.F("hint_pass", list);
        else _hint.Text = Lang.F("hint_block", list);

        var w = new List<string>();
        if (DrainAll && sel.Count == 0) w.Add(Lang.T("warn_drain_empty"));
        if (Opposite) w.Add(Lang.T("warn_opposite"));
        _warn.Text = string.Join("\n", w);
        _warn.IsVisible = w.Count > 0;
    }

    private void ApplySearch()
    {
        var q = _search.Text?.Trim() ?? "";
        foreach (var g in _groups)
        {
            int visible = 0;
            foreach (var it in _items.Where(i => i.Group == g.group))
            {
                bool show = q.Length == 0 || it.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase);
                it.Box.IsVisible = show;
                if (show) visible++;
            }
            g.root.IsVisible = visible > 0;
            g.body.IsVisible = g.expand.IsChecked == true || q.Length > 0;
        }
    }
}
