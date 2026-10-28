using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Strata.Controls;
using Strata.Core;
using Strata.Quota;
using Strata.Smart;
using Strata.Widgets;

namespace Strata.Views;

public sealed class SettingsWindow : Window
{
    private readonly App _app;
    private readonly ContentControl _page = new();
    private readonly StackPanel _nav = new() { Margin = new Thickness(12, 16, 12, 16) };
    private string _current = "look";

    private AppSettings S => _app.Settings;

    private static readonly (string Id, string Glyph, string Title)[] Pages =
    [
        ("look", "\uE790", "Appearance"),
        ("taskbar", "\uE75B", "Taskbar & startup"),
        ("widgets", "\uE74C", "Widgets"),
        ("automations", "\uE8F1", "Automations"),
        ("activity", "\uE81C", "Activity"),
        ("apps", "\uE71D", "Pinned apps"),
        ("time", "\uE823", "Clock, timers & weather"),
        ("aiusage", "\uE9D2", "AI usage"),
        ("privacy", "\uE72E", "Privacy & data"),
        ("about", "\uE946", "About"),
    ];

    public SettingsWindow(App app, string? page = null)
    {
        _app = app;
        Title = "Strata Settings";
        Width = 860;
        Height = 640;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Theme.UiFont;
        using (var trayIcon = Tray.CreateIcon())
            Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(trayIcon.Handle, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        SetResourceReference(BackgroundProperty, "FlyoutBg");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var navHost = new Border { Child = _nav, BorderThickness = new Thickness(0, 0, 1, 0) };
        navHost.SetResourceReference(Border.BorderBrushProperty, "FlyoutBorder");
        navHost.SetResourceReference(Border.BackgroundProperty, "InputBg");
        grid.Children.Add(navHost);
        var scroller = new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(28, 22, 28, 28) };
        Grid.SetColumn(scroller, 1);
        grid.Children.Add(scroller);
        Content = grid;

        ShowPage(page ?? "look");
    }

    public void ShowPage(string page)
    {
        _current = page;
        _nav.Children.Clear();
        var brand = UI.Row(8, UI.Text("Strata", 20, flyout: true, weight: FontWeights.SemiBold));
        brand.Margin = new Thickness(8, 0, 0, 18);
        _nav.Children.Add(brand);
        foreach (var (id, glyph, title) in Pages)
        {
            var row = UI.Row(12, UI.Icon(glyph, 15, flyout: true), UI.Text(title, 13.5, flyout: true));
            var item = new FlatButton { Content = row, Padding = new Thickness(10, 9, 10, 9),
                HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 1) }.Named(title);
            item.SetResourceReference(Control.BackgroundProperty, id == page ? "Pressed" : "InputBg");
            string target = id;
            item.Click += (_, _) => ShowPage(target);
            _nav.Children.Add(item);
        }

        _page.Content = page switch
        {
            "taskbar" => TaskbarPage(),
            "widgets" => WidgetsPage(),
            "automations" => AutomationsPage(),
            "activity" => AutomationActivityPage(),
            "apps" => AppsPage(),
            "time" => TimePage(),
            "aiusage" => AiUsagePage(),
            "privacy" => PrivacyPage(),
            "about" => AboutPage(),
            _ => LookPage(),
        };
    }

    private void Changed(bool rebuild = true)
    {
        S.Save();
        if (rebuild) _app.ApplySettings();
        ShowPage(_current);
    }

    // ---------------- Building blocks ----------------

    private static StackPanel PageRoot(string title, string subtitle)
    {
        var p = new StackPanel();
        p.Children.Add(UI.Text(title, 24, flyout: true, weight: FontWeights.SemiBold));
        var sub = UI.Text(subtitle, 13, subtle: true, flyout: true);
        sub.TextWrapping = TextWrapping.Wrap;
        sub.Margin = new Thickness(0, 4, 0, 18);
        p.Children.Add(sub);
        return p;
    }

    private static Border SettingRow(string title, string? description, UIElement control)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(UI.Text(title, 14, flyout: true));
        if (description is not null)
        {
            var d = UI.Text(description, 12, subtle: true, flyout: true);
            d.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(d);
        }
        control.Named(title, description);
        var dock = new DockPanel();
        if (control is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(16, 0, 0, 0); }
        DockPanel.SetDock(control, Dock.Right);
        dock.Children.Add(control);
        dock.Children.Add(text);
        var card = UI.Card(dock, new Thickness(16, 12, 16, 12));
        card.Margin = new Thickness(0, 0, 0, 6);
        return card;
    }

    private static StackPanel Choice<T>(T current, IEnumerable<(T Value, string Label)> options, Action<T> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (value, label) in options)
        {
            bool on = EqualityComparer<T>.Default.Equals(value, current);
            var t = UI.Text(label, 12.5, flyout: true, weight: on ? FontWeights.SemiBold : FontWeights.Normal);
            if (on) t.Foreground = Brushes.White;
            var b = new ChoiceButton { Content = t, IsChecked = on, Padding = new Thickness(12, 6, 12, 6) }.Named(label);
            b.Click += (_, _) => pick(value);
            b.Margin = new Thickness(4, 0, 0, 0);
            row.Children.Add(b);
        }
        return row;
    }

    private static ToggleSwitch Toggle(bool on, Action<bool> set)
    {
        var toggle = new ToggleSwitch { IsChecked = on };
        toggle.Click += (_, _) => set(toggle.IsChecked == true);
        return toggle;
    }

    private static TextBox NumberBox(int value, int min, int max, Action<int> set)
    {
        var box = UI.Input(value.ToString());
        box.Width = 70;
        box.TextAlignment = TextAlignment.Center;
        box.LostFocus += (_, _) =>
        {
            if (int.TryParse(box.Text, out int v)) set(Math.Clamp(v, min, max));
            else box.Text = value.ToString();
        };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Keyboard.ClearFocus(); };
        return box;
    }

    private static TextBlock SectionTitle(string text)
    {
        var t = UI.Text(text, 13, subtle: true, flyout: true, weight: FontWeights.SemiBold);
        t.Margin = new Thickness(0, 16, 0, 8);
        return t;
    }

    // ---------------- Pages ----------------

    private FrameworkElement LookPage()
    {
        var p = PageRoot("Appearance", "How the dock looks. Changes apply right away.");
        p.Children.Add(SettingRow("Style", "Glass blurs your wallpaper behind the dock. Liquid glass is glass you set the opacity of. Dark is a solid dark dock.",
            Choice(DockStyles.Normalize(S.Style), DockStyles.Selectable.Select(v => (v, DockStyles.DisplayName(v))), v => { S.Style = v; Changed(); })));
        if (DockStyles.Normalize(S.Style) == DockStyle.Clear)
        {
            // 0: fully transparent; the middle: blurred, translucent glass; 100: a solid fill.
            var value = UI.Text($"{S.GlassOpacity}%", 13, flyout: true, weight: FontWeights.SemiBold);
            value.MinWidth = 44;
            value.TextAlignment = TextAlignment.Right;
            var slider = new FlatSlider { Value = S.GlassOpacity, Width = 220 }.Named("Glass opacity");
            var apply = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            apply.Tick += (_, _) => { apply.Stop(); S.Save(); _app.ApplySettings(); };
            slider.UserChanged += v =>
            {
                S.GlassOpacity = (int)Math.Round(v);
                value.Text = $"{S.GlassOpacity}%";
                apply.Stop(); apply.Start();   // apply when the slider rests, not on every step
            };
            p.Children.Add(SettingRow("Glass opacity", "0% is fully transparent, the middle is blurred, translucent glass, and 100% is a solid fill.",
                UI.Row(8, slider, value)));
        }
        p.Children.Add(SettingRow("Shape", "A floating pill, or a bar across the whole screen edge.",
            Choice(S.Shape, [(DockShape.Pill, "Pill"), (DockShape.FullWidth, "Full width")], v => { S.Shape = v; Changed(); })));
        p.Children.Add(SettingRow("Size", null,
            Choice(S.Size, [(DockSize.Small, "Small"), (DockSize.Medium, "Medium"), (DockSize.Large, "Large")], v => { S.Size = v; Changed(); })));
        p.Children.Add(SettingRow("Position", null,
            Choice(S.Edge, [(DockEdge.Bottom, "Bottom"), (DockEdge.Top, "Top")], v => { S.Edge = v; Changed(); })));
        p.Children.Add(SettingRow("Start button", null, Toggle(S.ShowStartButton, v => { S.ShowStartButton = v; Changed(); })));
        p.Children.Add(SettingRow("Search button", null, Toggle(S.ShowSearchButton, v => { S.ShowSearchButton = v; Changed(); })));
        return p;
    }

    private FrameworkElement TaskbarPage()
    {
        var p = PageRoot("Taskbar & startup", "Strata never changes Windows permanently: exit Strata and the taskbar comes back exactly as it was.");
        p.Children.Add(SettingRow("Windows taskbar while Strata runs",
            "Hide removes it completely. Auto-hide lets it slide in at the screen edge (handy for tray icons).",
            Choice(S.TaskbarMode, [(TaskbarMode.Hide, "Hide"), (TaskbarMode.AutoHide, "Auto-hide"), (TaskbarMode.Leave, "Leave it")],
                v => { S.TaskbarMode = v; Changed(); })));
        p.Children.Add(SettingRow("Reserve screen space",
            "Maximized windows stop at the dock instead of going underneath it.",
            Toggle(S.ReserveSpace, v => { S.ReserveSpace = v; Changed(); })));
        int screens = PrivacyGuard.PreviewActive ? 1 : System.Windows.Forms.Screen.AllScreens.Length;
        p.Children.Add(SettingRow("Dock on every screen", "Each other screen gets its own dock with Start, the clock and the windows on that screen; the main dock keeps the widgets. "
            + (screens == 1 ? "This PC has one screen." : $"This PC has {screens} screens."),
            Toggle(S.DockOnEveryScreen, v => { S.DockOnEveryScreen = v; Changed(); })));
        int free = _app.AppShortcutsRegistered;
        string availability = !S.AppShortcuts ? "Off." : PrivacyGuard.PreviewActive ? "Preview: not registered."
            : free == AppHotkeys.Count ? "All nine are available." : $"{free} of nine are available; other apps hold the rest.";
        p.Children.Add(SettingRow("App shortcuts", $"{AppHotkeys.Gesture}+1 to 9 open or switch to the dock's first nine apps, as a click does. {availability}",
            Toggle(S.AppShortcuts, v => { S.AppShortcuts = v; Changed(); })));
        p.Children.Add(SettingRow("Unread badges", "A red count on an app when its window title shows unread messages, like \"(3) WhatsApp\" or \"Inbox (3)\". Read from the titles on the dock, never stored.",
            Toggle(S.UnreadBadges, v => { S.UnreadBadges = v; Changed(); })));
        p.Children.Add(SettingRow("Start with Windows", "Launch Strata when you sign in.",
            Toggle(S.StartWithWindows, v => { S.StartWithWindows = v; StartupRegistration.Set(v); Changed(rebuild: false); })));
        var shortcut = UI.Text(_app.KeyboardShortcutRegistered == false
                ? $"Keyboard: {DockHotkey.Gesture} is unavailable (Windows or another app uses it). Open Strata again from Start to reach these settings."
                : $"Keyboard: press {DockHotkey.Gesture} to focus the dock, then use the arrow keys, Enter, Shift+F10 and Escape.",
            12, subtle: _app.KeyboardShortcutRegistered != false, flyout: true);
        shortcut.TextWrapping = TextWrapping.Wrap;
        shortcut.Margin = new Thickness(0, 12, 0, 0);
        p.Children.Add(shortcut);
        var palette = UI.Text(_app.CommandPaletteShortcutRegistered == false
                ? $"Command palette: {CommandPaletteHotkey.Gesture} is unavailable (Windows or another app uses it). Open it from the dock's right-click menu."
                : $"Command palette: press {CommandPaletteHotkey.Gesture} to search apps, windows, settings, widgets, actions and automations.",
            12, subtle: _app.CommandPaletteShortcutRegistered != false, flyout: true);
        palette.TextWrapping = TextWrapping.Wrap;
        palette.Margin = new Thickness(0, 6, 0, 0);
        p.Children.Add(palette);
        var restore = UI.Text("If Strata is ever closed unexpectedly and the taskbar stays hidden, run Strata again, or run:  Strata.exe --restore-taskbar", 12, subtle: true, flyout: true);
        restore.TextWrapping = TextWrapping.Wrap;
        restore.Margin = new Thickness(0, 12, 0, 0);
        p.Children.Add(restore);
        return p;
    }

    private FrameworkElement WidgetsPage()
    {
        var p = PageRoot("Widgets", "Choose what sits on each side of your apps. Use the arrows to reorder.");

        UIElement SideList(string title, List<string> side, List<string> other)
        {
            var col = new StackPanel();
            col.Children.Add(SectionTitle(title));
            if (side.Count == 0) col.Children.Add(UI.Text("Empty", 12.5, subtle: true, flyout: true));
            for (int i = 0; i < side.Count; i++)
            {
                int index = i;
                var info = WidgetCatalog.Find(side[i]);
                if (info is null) continue;
                var buttons = UI.Row(2,
                    UI.IconButton("\uE70E", () => { if (index > 0) { (side[index - 1], side[index]) = (side[index], side[index - 1]); Changed(); } }, 10, "Move up"),
                    UI.IconButton("\uE70D", () => { if (index < side.Count - 1) { (side[index + 1], side[index]) = (side[index], side[index + 1]); Changed(); } }, 10, "Move down"),
                    UI.IconButton("\uE8AB", () => { side.RemoveAt(index); other.Add(info.Id); Changed(); }, 10, "Move to other side"),
                    UI.IconButton("\uE711", () => { side.RemoveAt(index); Changed(); }, 10, "Remove"));
                var row = new DockPanel();
                DockPanel.SetDock(buttons, Dock.Right);
                row.Children.Add(buttons);
                row.Children.Add(UI.Text(info.Name, 13.5, flyout: true));
                var card = UI.Card(row, new Thickness(12, 6, 6, 6));
                card.Margin = new Thickness(0, 0, 0, 4);
                col.Children.Add(card);
            }
            return col;
        }

        var sides = new Grid();
        sides.ColumnDefinitions.Add(new ColumnDefinition());
        sides.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        sides.ColumnDefinitions.Add(new ColumnDefinition());
        var left = SideList("Left of apps", S.LeftWidgets, S.RightWidgets);
        var right = SideList("Right of apps", S.RightWidgets, S.LeftWidgets);
        Grid.SetColumn(right, 2);
        sides.Children.Add(left);
        sides.Children.Add(right);
        p.Children.Add(sides);

        p.Children.Add(SectionTitle("Add a widget"));
        foreach (var group in WidgetCatalog.All.GroupBy(w => w.Group))
        {
            foreach (var info in group)
            {
                bool used = S.LeftWidgets.Contains(info.Id) || S.RightWidgets.Contains(info.Id);
                var add = UI.Row(6,
                    UI.TextButton("+ Left", () => { S.LeftWidgets.Add(info.Id); Changed(); }),
                    UI.TextButton("+ Right", () => { S.RightWidgets.Add(info.Id); Changed(); }));
                var text = UI.Column(1, UI.Text(info.Name + (used ? "  ·  on dock" : ""), 13.5, flyout: true),
                    UI.Text($"{info.Group} · {info.Description}", 12, subtle: true, flyout: true));
                var row = new DockPanel();
                DockPanel.SetDock(add, Dock.Right);
                row.Children.Add(add);
                row.Children.Add(text);
                var card = UI.Card(row, new Thickness(14, 8, 8, 8));
                card.Margin = new Thickness(0, 0, 0, 4);
                p.Children.Add(card);
            }
        }
        return p;
    }

    private FrameworkElement AppsPage()
    {
        var p = PageRoot("Pinned apps", "Right-click any running app on the dock to pin it, or drag a shortcut, program or folder onto the dock.");
        if (S.Pinned.Count == 0) p.Children.Add(UI.Text("No pinned apps yet.", 13, subtle: true, flyout: true));
        for (int i = 0; i < S.Pinned.Count; i++)
        {
            int index = i;
            var pin = S.Pinned[i];
            var icon = new Image { Width = 24, Height = 24, Source = IconCache.For(pin), Margin = new Thickness(0, 0, 12, 0) };
            var buttons = UI.Row(2,
                UI.IconButton("\uE70E", () => { if (index > 0) { (S.Pinned[index - 1], S.Pinned[index]) = (S.Pinned[index], S.Pinned[index - 1]); Changed(); } }, 10, "Move up"),
                UI.IconButton("\uE70D", () => { if (index < S.Pinned.Count - 1) { (S.Pinned[index + 1], S.Pinned[index]) = (S.Pinned[index], S.Pinned[index + 1]); Changed(); } }, 10, "Move down"),
                UI.IconButton("\uE711", () => { S.Pinned.RemoveAt(index); Changed(); }, 10, "Unpin"));
            var row = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(icon);
            row.Children.Add(buttons);
            row.Children.Add(UI.Column(1, UI.Text(pin.Name, 13.5, flyout: true),
                UI.Text(pin.Aumid ?? pin.Path, 11.5, subtle: true, flyout: true)));
            var card = UI.Card(row, new Thickness(12, 8, 8, 8));
            card.Margin = new Thickness(0, 0, 0, 4);
            p.Children.Add(card);
        }
        var import = UI.TextButton("Import pins from the Windows taskbar", () =>
        {
            foreach (var pin in Launcher.ImportWindowsTaskbarPins())
                if (!S.Pinned.Any(x => string.Equals(x.Path, pin.Path, StringComparison.OrdinalIgnoreCase))) S.Pinned.Add(pin);
            Changed();
        });
        import.Margin = new Thickness(0, 12, 0, 0);
        import.HorizontalAlignment = HorizontalAlignment.Left;
        p.Children.Add(import);
        return p;
    }

    private FrameworkElement AutomationsPage()
    {
        var rules = _app.Automations?.Rules ?? Smart.Automations.Create();
        var p = PageRoot("Automations", $"{rules.Count} smart actions respond to shared signals. Temporary layout and theme changes restore automatically.");
        var quietStart = UI.Input(S.QuietHoursStart.ToString(@"hh\:mm")); quietStart.Width = 74;
        var quietEnd = UI.Input(S.QuietHoursEnd.ToString(@"hh\:mm")); quietEnd.Width = 74;
        void SaveQuiet(TextBox box, Action<TimeSpan> save)
        {
            if (TimeSpan.TryParseExact(box.Text.Trim(), @"h\:mm", System.Globalization.CultureInfo.InvariantCulture, out var value) ||
                TimeSpan.TryParseExact(box.Text.Trim(), @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out value))
            { save(value); Changed(rebuild: false); }
        }
        quietStart.LostFocus += (_, _) => SaveQuiet(quietStart, v => S.QuietHoursStart = v);
        quietEnd.LostFocus += (_, _) => SaveQuiet(quietEnd, v => S.QuietHoursEnd = v);
        p.Children.Add(SettingRow("Quiet hours", "Non-urgent automation notifications wait or stay silent during this daily window.",
            Toggle(S.QuietHoursEnabled, v => { S.QuietHoursEnabled = v; Changed(rebuild: false); })));
        // The row names the pair; each box needs its own name too, or a screen reader announces just "edit".
        quietStart.Named("Quiet hours start", "24-hour time");
        quietEnd.Named("Quiet hours end", "24-hour time");
        p.Children.Add(SettingRow("Quiet-hours window", "24-hour time, start to end", UI.Row(6, quietStart, UI.Text("to", 12, flyout: true), quietEnd)));
        foreach (var rule in rules)
        {
            var status = _app.Automations?.Status(rule.Id) ?? new RuleStatus(rule, rule.EnabledByDefault, false, false, new RuleState(), null);
            var title = UI.Text(rule.Name, 15, flyout: true, weight: FontWeights.SemiBold);
            var enabled = Toggle(status.Enabled, value => { _app.Automations?.SetEnabled(rule.Id, value); ShowPage("automations"); }).Named($"Enable {rule.Name}");
            var heading = new DockPanel();
            DockPanel.SetDock(enabled, Dock.Right);
            heading.Children.Add(enabled);
            heading.Children.Add(title);

            var body = new StackPanel();
            body.Children.Add(heading);
            var desc = UI.Text(rule.Description, 12, subtle: true, flyout: true);
            desc.TextWrapping = TextWrapping.Wrap;
            desc.Margin = new Thickness(0, 4, 0, 8);
            body.Children.Add(desc);

            foreach (var parameter in rule.Parameters)
            {
                string current = status.State.Parameters.GetValueOrDefault(parameter.Id, parameter.Default);
                UIElement editor;
                if (parameter.Kind == ParameterKind.Toggle)
                {
                    editor = Toggle(current != "0", value => { _app.Automations?.SetParameter(rule.Id, parameter.Id, value ? "1" : "0"); ShowPage("automations"); });
                }
                else if (parameter.Kind == ParameterKind.Style)
                {
                    var value = DockStyles.Normalize(int.TryParse(current, out int raw) && Enum.IsDefined(typeof(DockStyle), raw) ? (DockStyle)raw : DockStyle.Dark);
                    editor = Choice(value, DockStyles.Selectable.Select(v => (v, DockStyles.DisplayName(v))), v =>
                    {
                        _app.Automations?.SetParameter(rule.Id, parameter.Id, ((int)v).ToString(System.Globalization.CultureInfo.InvariantCulture));
                        ShowPage("automations");
                    });
                }
                else
                {
                    var input = UI.Input(current);
                    input.Width = parameter.Kind == ParameterKind.Text ? 180 : 90;
                    void Save()
                    {
                        string value = input.Text.Trim();
                        if (parameter.Kind == ParameterKind.Number && (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double number) || number < parameter.Min || number > parameter.Max))
                        { input.Text = current; return; }
                        _app.Automations?.SetParameter(rule.Id, parameter.Id, value);
                    }
                    input.LostFocus += (_, _) => Save();
                    input.KeyDown += (_, e) => { if (e.Key == Key.Enter) Save(); };
                    editor = input;
                }
                body.Children.Add(SettingRow(parameter.Label + (parameter.Unit.Length == 0 ? "" : $" ({parameter.Unit})"), null, editor));
            }

            string state = status.State.ErrorDisabled ? $"Paused after errors: {status.State.LastError}"
                : status.Active ? "Active now"
                : status.Suppressed ? "Waiting for the condition to clear"
                : status.WaitingUntil is { } wake ? $"Waiting until {wake:g}" : "Ready";
            body.Children.Add(UI.Text(state, 11.5, status.State.ErrorDisabled, flyout: true));
            if (status.State.LastFired is { } fired)
            {
                body.Children.Add(UI.Text($"Last fired {fired:g}: {status.State.LastExplanation}", 11.5, subtle: true, flyout: true));
                if (!string.IsNullOrWhiteSpace(status.State.LastOutcome)) body.Children.Add(UI.Text(status.State.LastOutcome, 11.5, subtle: true, flyout: true));
            }
            var restore = UI.TextButton("Restore defaults", () =>
            {
                if (_app.Automations is { } engine)
                {
                    foreach (var parameter in rule.Parameters) engine.SetParameter(rule.Id, parameter.Id, parameter.Default);
                    engine.SetEnabled(rule.Id, rule.EnabledByDefault);
                }
                ShowPage("automations");
            });
            restore.HorizontalAlignment = HorizontalAlignment.Left;
            restore.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(restore);
            var card = UI.Card(body, new Thickness(16, 12, 16, 12));
            card.Margin = new Thickness(0, 0, 0, 8);
            p.Children.Add(card);
        }
        return p;
    }

    private FrameworkElement AutomationActivityPage()
    {
        var rules = _app.Automations?.Rules ?? Smart.Automations.Create();
        IReadOnlyList<HistoryEntry> history = _app.Automations?.History ?? (PrivacyGuard.PreviewActive
            ?
            [
                new HistoryEntry(AppClock.Now.AddMinutes(-2), Smart.Automations.LowBattery, "Fired", "fixture", "fixture"),
                new HistoryEntry(AppClock.Now.AddMinutes(-1), Smart.Automations.LowBattery, "Ended", "fixture", "fixture"),
            ]
            : []);
        var activity = AutomationActivity.Project(history, rules);
        var p = PageRoot("Automation activity", "What acted, why it acted, what changed, and whether temporary changes were undone. " +
            $"The newest {AutomationActivity.ViewLimit} entries are shown using privacy-safe summaries.");
        if (activity.Count == 0)
        {
            p.Children.Add(UI.Text("No automation activity yet.", 13, subtle: true, flyout: true));
            return p;
        }
        foreach (var entry in activity)
        {
            var title = UI.Text($"{entry.RuleName} · {entry.Status}", 14, flyout: true, weight: FontWeights.SemiBold);
            var when = UI.Text(entry.At.ToString("g"), 11.5, subtle: true, flyout: true);
            var why = UI.Text("Why: " + entry.Why, 12, flyout: true); why.TextWrapping = TextWrapping.Wrap;
            var changed = UI.Text("Changed: " + entry.Changed, 12, subtle: true, flyout: true); changed.TextWrapping = TextWrapping.Wrap;
            var undone = UI.Text(entry.Undone ? "Undo: completed" : "Undo: not applicable yet", 11.5, subtle: true, flyout: true);
            var content = UI.Column(4, UI.Row(8, title, when), why, changed, undone);
            var card = UI.Card(content, new Thickness(14, 10, 14, 10));
            card.Margin = new Thickness(0, 0, 0, 7);
            AutomationProperties.SetName(card, $"{entry.RuleName}. {entry.Status}. {entry.Why}. {entry.Changed}. " +
                (entry.Undone ? "Changes undone." : "No undo recorded."));
            p.Children.Add(card);
        }
        return p;
    }

    private FrameworkElement TimePage()
    {
        var p = PageRoot("Clock, timers & weather", "Details for the time-based widgets.");
        p.Children.Add(SettingRow("24-hour clock", null, Toggle(S.Use24HourClock, v => { S.Use24HourClock = v; Changed(); })));
        p.Children.Add(SettingRow("Focus session length", "Minutes", NumberBox(S.PomodoroWorkMinutes, 1, 180, v => { S.PomodoroWorkMinutes = v; Changed(rebuild: false); })));
        p.Children.Add(SettingRow("Break length", "Minutes", NumberBox(S.PomodoroBreakMinutes, 1, 60, v => { S.PomodoroBreakMinutes = v; Changed(rebuild: false); })));
        p.Children.Add(SettingRow("Event reminders", "Minutes before an event starts (also shown on Automations)", NumberBox(S.EventReminderMinutes, 0, 120, v =>
        {
            S.EventReminderMinutes = v;
            _app.Automations?.SetParameter(Smart.Automations.EventReminder, "minutes", v.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Changed(rebuild: false);
        })));

        p.Children.Add(SectionTitle("Weather"));
        p.Children.Add(SettingRow("Units", null,
            Choice(S.Fahrenheit, [(false, "°C, km/h"), (true, "°F, mph")], v => { S.Fahrenheit = v; Changed(); })));
        var input = UI.Input(placeholder: "Search for your city and press Enter");
        input.Width = 300;
        var results = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        input.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || input.Text.Trim().Length < 2) return;
            results.Children.Clear();
            results.Children.Add(UI.Text("Searching...", 12, subtle: true, flyout: true));
            var places = await WeatherService.Search(input.Text.Trim());
            results.Children.Clear();
            if (places.Count == 0) results.Children.Add(UI.Text("No matches", 12, subtle: true, flyout: true));
            foreach (var place in places)
            {
                var b = UI.TextButton($"{place.Name}  ·  {place.Detail}", () =>
                {
                    S.WeatherCity = place.Name;
                    S.WeatherLatitude = place.Latitude;
                    S.WeatherLongitude = place.Longitude;
                    _ = WeatherService.RefreshIfStale(S, force: true);
                    Changed();
                });
                b.HorizontalAlignment = HorizontalAlignment.Left;
                b.Margin = new Thickness(0, 4, 0, 0);
                results.Children.Add(b);
            }
        };
        p.Children.Add(SettingRow("City", S.WeatherCity.Length > 0 ? $"Currently {S.WeatherCity}" : "Not set", input));
        p.Children.Add(results);
        return p;
    }

    private FrameworkElement AiUsagePage()
    {
        var q = S.Quota;
        var p = PageRoot("AI usage", "Claude and Codex plan limits on the dock, read from each app's own interface. The widget is on the dock by default; move or remove it on the Widgets page.");

        p.Children.Add(SectionTitle("Claude (Claude Code)"));
        string claudeFound = PrivacyGuard.PreviewActive ? "Preview: not looked up."
            : ClaudeUsageSource.Locate(q.ClaudePath) is not null ? "claude.exe found."
            : "Claude Code not found. Install it, or set quota.claudePath in Strata's settings.json.";
        p.Children.Add(SettingRow("Show Claude", $"Strata reads your limits in the background with Claude Code's own /usage, hidden: it uses no tokens and saves nothing to your sessions. If a sign-in is needed, the AI usage flyout offers one. {claudeFound}",
            Toggle(q.ClaudeEnabled, v => { q.ClaudeEnabled = v; QuotaService.Reset(); Changed(); })));
        string claudeText;
        FlatButton claudeButton;
        if (PrivacyGuard.PreviewActive)
        {
            claudeText = "Preview: sample data only. Claude Code's settings are not read.";
            claudeButton = UI.TextButton("Connect", () => { });
            claudeButton.IsEnabled = false;
        }
        else
        {
            var connector = ClaudeCodeConnector.ForCurrentUser();
            var state = connector.Status();
            claudeText = state switch
            {
                ClaudeConnection.Connected => "Optional, connected: Claude Code in a terminal also passes Strata its figures through its status line after each reply. Not needed for the background reading. Disconnect puts your settings file back as it was.",
                ClaudeConnection.OtherStatusLine => "Claude Code already has a status line, so Strata won't replace it. Remove that status line first if you want Strata to show Claude usage.",
                ClaudeConnection.SettingsUnreadable => "Claude Code's settings file couldn't be read, so Strata leaves it alone.",
                _ => "Optional, not needed for the background reading. Connect adds a status line to Claude Code's user settings (a backup is kept), so Claude Code in a terminal also passes Strata its figures after each reply.",
            };
            claudeButton = state == ClaudeConnection.Connected
                ? UI.TextButton("Disconnect", () => { connector.Disconnect(); QuotaService.Reset(); Changed(); })
                : UI.TextButton("Connect", () =>
                {
                    if (!connector.Connect(ClaudeCodeConnector.BridgeCommand(Environment.ProcessPath ?? "Strata.exe", Storage.UsingDefaultRoot ? null : Storage.Root)))
                        MessageBox.Show(this, "Strata didn't change Claude Code's settings (another status line is set, or the file couldn't be read).", "Strata");
                    QuotaService.Reset();
                    Changed();
                });
            claudeButton.IsEnabled = state is ClaudeConnection.Connected or ClaudeConnection.NotConnected;
        }
        p.Children.Add(SettingRow("Status line", claudeText, claudeButton));

        p.Children.Add(SectionTitle("Codex (Codex app-server)"));
        p.Children.Add(SettingRow("Show Codex", "Strata asks Codex's own app-server for your limits (account/rateLimits/read, read-only). Codex keeps the sign-in.",
            Toggle(q.CodexEnabled, v => { q.CodexEnabled = v; QuotaService.Reset(); Changed(); })));
        string found = PrivacyGuard.PreviewActive ? "Preview: not looked up."
            : CodexAppServerSource.Locate(q.CodexPath) is not null ? "codex.exe found."
            : "Not found. Install the Codex app or CLI, or set quota.codexPath in Strata's settings.json.";
        p.Children.Add(SettingRow("Check every", $"Minutes, while the widget is on the dock (longer after errors). {found}",
            NumberBox(q.RefreshMinutes, 1, 60, v => { q.RefreshMinutes = v; Changed(rebuild: false); })));

        p.Children.Add(SectionTitle("Dock tile"));
        p.Children.Add(SettingRow("Show", "Most constrained picks the single window with the least remaining among up-to-date providers. Both rotates every 8 seconds.",
            Choice(q.TileMode, [(QuotaTileMode.Claude, "Claude"), (QuotaTileMode.Codex, "Codex"), (QuotaTileMode.MostConstrained, "Most constrained"), (QuotaTileMode.Rotate, "Both")],
                v => { q.TileMode = v; Changed(); })));
        p.Children.Add(SettingRow("Claude window", null,
            Choice(q.ClaudeWindow, [(QuotaWindowKind.Session, "Session (5h)"), (QuotaWindowKind.Weekly, "Weekly"), (QuotaWindowKind.Spend, "Spend limit")], v => { q.ClaudeWindow = v; Changed(); })));
        p.Children.Add(SettingRow("Codex window", null,
            Choice(q.CodexWindow, [(QuotaWindowKind.Session, "Session (5h)"), (QuotaWindowKind.Weekly, "Weekly"), (QuotaWindowKind.Model, "Model-specific")], v => { q.CodexWindow = v; Changed(); })));
        p.Children.Add(SettingRow("Figure", "The tile always says which one it shows.",
            Choice(q.TileShowsRemaining, [(true, "Remaining"), (false, "Used")], v => { q.TileShowsRemaining = v; Changed(); })));
        p.Children.Add(SettingRow("Notifications", "Once when a limit reaches the warning level, once when it is reached, and once when it resets. Quiet hours apply.",
            Toggle(q.Alerts, v => { q.Alerts = v; Changed(rebuild: false); })));
        p.Children.Add(SettingRow("Warning colour", "At or below this percent remaining (also when the warning notification is sent)",
            NumberBox(q.WarnRemainingPercent, 1, 99, v => { q.WarnRemainingPercent = v; Changed(); })));
        p.Children.Add(SettingRow("Critical colour", "At or below this percent remaining (or when a limit is reached)",
            NumberBox(q.CriticalRemainingPercent, 0, 99, v => { q.CriticalRemainingPercent = v; Changed(); })));
        p.Children.Add(SettingRow("Claude data is stale after", "Minutes. Claude Code only reports while a session is active.",
            NumberBox(q.StaleMinutes, 5, 1440, v => { q.StaleMinutes = v; Changed(rebuild: false); })));

        var privacy = UI.Text("Strata keeps only the figures it shows (percentages, reset times, plan, credits) in quota.json and quota-claude.json, and which notifications it sent in quota-alerts.json. It never reads or stores sign-in tokens, raw responses, prompts, conversation text or folder paths, and it makes no network request for this: Codex contacts its own service, and Claude Code passes on figures it already has. These are plan limits; token counts and cost estimates are not shown as quota.", 12, subtle: true, flyout: true);
        privacy.TextWrapping = TextWrapping.Wrap;
        privacy.Margin = new Thickness(0, 12, 0, 0);
        p.Children.Add(privacy);
        return p;
    }

    private FrameworkElement PrivacyPage()
    {
        var p = PageRoot("Privacy & data", "Strata has no account and no telemetry. Everything stays in your user folder on this PC.");
        p.Children.Add(SettingRow("Track screen time", "Counts the app in front while you're active. Pauses after 2 minutes idle.",
            Toggle(S.TrackScreenTime, v => { S.TrackScreenTime = v; _app.ScreenTime.Enabled = v; Changed(rebuild: false); })));
        var open = UI.TextButton("Open data folder", () => Process.Start(new ProcessStartInfo(Storage.Root) { UseShellExecute = true }));
        open.IsEnabled = !PrivacyGuard.PreviewActive;
        p.Children.Add(SettingRow("Your data", PrivacyGuard.PreviewActive ? "Fixture storage (isolated)" : Storage.Root, open));
        var clear = UI.TextButton("Delete history", () =>
        {
            if (MessageBox.Show(this, "Delete all recorded screen time? This can't be undone.", "Strata",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            string dir = Path.Combine(Storage.Root, "screentime");
            if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, "*.json")) File.Delete(f);
            _app.ResetScreenTime();
            Changed(rebuild: false);
        });
        p.Children.Add(SettingRow("Screen time history", "Remove every recorded day.", clear));
        p.Children.Add(SettingRow("Back up settings", "Save your dock, widgets, pins, styles and automation choices to one file, to keep a copy or move them to another PC. "
            + "Notes, todos, events, screen time and AI usage are not in it.", UI.Row(6,
            UI.TextButton("Back up…", BackUpSettings), UI.TextButton("Restore…", RestoreSettings))));
        p.Children.Add(SettingRow("Diagnostic bundle", "Version, executable hash and size, configuration schema, privacy-safe error types, process counters and anonymous monitor rectangles. " +
            "It excludes usernames, paths, window and media titles, notification content, file names, account IDs and tokens.",
            UI.TextButton("Save diagnostics...", SaveDiagnostics)));
        var net = UI.Text("Network use: Strata itself only goes online for weather, and for updates if you give it a web release address (it then reads that release's update.json once a day, and downloads a new version only when you click Install). (AI usage asks the Codex app, which contacts its own service with its own sign-in.) Searching for a city sends the name you type to Open-Meteo's geocoding service, and forecasts send that city's coordinates to Open-Meteo (open-meteo.com), about every 30 minutes while the Weather widget is on the dock. Nothing else leaves your PC.", 12, subtle: true, flyout: true);
        net.TextWrapping = TextWrapping.Wrap;
        net.Margin = new Thickness(0, 12, 0, 0);
        p.Children.Add(net);
        return p;
    }

    private void SaveDiagnostics()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save privacy-safe Strata diagnostics",
            FileName = $"strata-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.json",
            Filter = "JSON files (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, _app.CreateDiagnosticBundle(), new UTF8Encoding(false));
            MessageBox.Show(this, "The privacy-safe diagnostic bundle was saved.", "Strata");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, $"Strata couldn't create the diagnostic bundle: {ex.Message}", "Strata",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Save a settings backup where the user chooses.</summary>
    private void BackUpSettings()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Back up Strata settings",
            FileName = SettingsBackup.SuggestedName(DateTime.Today),
            Filter = "Strata settings (*.strata-settings.json)|*.strata-settings.json|JSON files (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, _app.ExportSettings());
            MessageBox.Show(this, $"Settings saved to {dialog.FileName}.", "Strata");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Strata couldn't save the backup there: {ex.Message}", "Strata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Restore a settings backup, after showing what it replaces.</summary>
    private void RestoreSettings()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Restore Strata settings",
            Filter = "Strata settings (*.strata-settings.json)|*.strata-settings.json|JSON files (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != true) return;
        string json;
        try { json = File.ReadAllText(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Strata couldn't read that file: {ex.Message}", "Strata", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var (file, error) = SettingsBackup.Parse(json);
        if (file is null) { MessageBox.Show(this, error, "Strata", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show(this, $"Replace your current settings with the backup from {file.ExportedAt:d}? Your pins, widgets, styles and automation choices change; notes, todos and history stay.",
                "Strata", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _app.RestoreSettings(file);
        ShowPage("privacy");
    }

    /// <summary>Updates from a release folder or web address: choose it, check it, install a checked newer version.</summary>
    private FrameworkElement UpdatesRow()
    {
        string source = S.UpdateFolder;
        var check = PrivacyGuard.PreviewActive ? null : _app.LastUpdateCheck;
        string text = source.Length == 0
            ? "Off. Choose a release folder, or enter Strata's GitHub address. Checked once a day."
            : $"{source}. {check?.Message ?? "Not checked yet."}";

        async Task UseSource(string value)
        {
            S.UpdateFolder = value;
            Changed(rebuild: false);
            if (value.Length > 0) await _app.CheckForUpdatesAsync(notify: false);
            ShowPage("about");
        }

        var buttons = UI.Row(6,
            UI.TextButton("Choose folder…", () =>
            {
                using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Folder with Strata releases (update.json)", UseDescriptionForTitle = true };
                if (source.Length > 0 && !Updater.IsWeb(source)) dialog.SelectedPath = source;
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                _ = UseSource(dialog.SelectedPath);
            }));
        // Shown only once there is a source to check: a disabled button is skipped by keyboard focus (found by the sweep).
        if (source.Length > 0)
        {
            buttons.Children.Add(UI.TextButton("Check now", async () => { await _app.CheckForUpdatesAsync(notify: false); ShowPage("about"); }));
            buttons.Children.Add(UI.TextButton("Turn off", () => _ = UseSource("")));
        }
        if (check is { State: UpdateState.Available, Manifest: { } m })
        {
            FlatButton? install = null;
            install = UI.TextButton($"Install {Updater.Short(m.Version)}", async () =>
            {
                install!.IsEnabled = false;
                if (install.Content is TextBlock label) label.Text = Updater.IsWeb(source) ? "Downloading…" : "Checking…";
                var (staged, message) = await Updater.StageAsync(source, m, Environment.ProcessPath, Path.GetTempPath(), App.UpdateHttp);
                ShowPage("about");
                if (staged is null) { MessageBox.Show(this, message, "Strata"); return; }
                string unsigned = Updater.RunningUnsigned(Environment.ProcessPath) ? "\n\n" + Updater.UnsignedWarning : "";
                if (MessageBox.Show(this, $"Install Strata {Updater.Short(m.Version)} now? The dock restarts; your settings and data stay.{unsigned}", "Strata",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes && !Updater.Install(staged))
                    MessageBox.Show(this, "The update couldn't be started. Strata's log has the details.", "Strata");
            }, accent: true);
            buttons.Children.Add(install);
        }

        // A web address: a GitHub repository ("https://github.com/owner/repo") or an update.json.
        var address = UI.Input(Updater.IsWeb(source) ? source : "", "Release address, like https://github.com/owner/repo");
        address.Width = 300;
        var use = UI.TextButton("Use address", () =>
        {
            string value = address.Text.Trim();
            if (Updater.ManifestUri(value) is null) { MessageBox.Show(this, "Enter an https:// address: Strata's GitHub repository, or an update.json.", "Strata"); return; }
            _ = UseSource(value);
        });
        var column = UI.Column(8, buttons, UI.Row(6, address, use));
        if (source.Length > 0 && Updater.RunningUnsigned(PrivacyGuard.PreviewActive ? null : Environment.ProcessPath))
        {
            var warning = UI.Text("\uE7BA  " + Updater.UnsignedWarning, 12, flyout: true);
            warning.TextWrapping = TextWrapping.Wrap;
            warning.MaxWidth = 420;
            warning.SetResourceReference(TextBlock.ForegroundProperty, "FlyoutFg");
            column.Children.Add(UI.Card(warning, new Thickness(10, 8, 10, 8)));
        }
        return SettingRow("Updates", text, column);
    }

    private FrameworkElement AboutPage()
    {
        var p = PageRoot("About Strata", "A smart dock for Windows: your apps, your day and your limits in one bar, which rearranges itself when something needs you.");
        string version = Updater.Short(App.Version);
        p.Children.Add(SettingRow("Version", null, UI.Text(version, 13.5, flyout: true)));
        p.Children.Add(UpdatesRow());
        p.Children.Add(SettingRow("Catalog", null, UI.Text($"{WidgetManifest.All.Count} widgets · {Smart.Automations.Create().Count} automations", 13.5, flyout: true)));
        p.Children.Add(SettingRow("Exit Strata", "Closes the dock and brings the Windows taskbar back.", UI.TextButton("Exit", _app.Quit)));
        var installer = Installer.Default;
        if (installer.IsInstalledCopy)
            p.Children.Add(SettingRow("Installed", Path.GetDirectoryName(installer.InstalledExe), UI.TextButton("Uninstall…", () =>
            {
                if (MessageBox.Show(this, "Remove Strata from this PC? Your notes, todos and history stay in your user folder.", "Strata",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo(installer.InstalledExe, "--uninstall") { UseShellExecute = true });
            })));
        else
            p.Children.Add(SettingRow("Install on this PC",
                "Copies Strata to your Programs folder and adds it to the Start menu. No admin rights needed.",
                UI.TextButton("Install", () =>
                {
                    if (Environment.ProcessPath is { } exe)
                        Process.Start(new ProcessStartInfo(exe, "--install") { UseShellExecute = true });
                }, accent: true)));
        var tips = UI.Text(
            "Tips\n" +
            "• Click an app to switch to it; click again to minimize. Middle-click opens a new window. Scroll over it to step through its windows.\n" +
            "• Rest the pointer on an app to see its windows; drop files on it to open them with it.\n" +
            "• Drag an app along the dock to reorder your pins, or drag a running app in among them to pin it.\n" +
            "• The volume flyout has quick settings: Wi-Fi, Bluetooth, focus and night light.\n" +
            $"• {CommandPaletteHotkey.Gesture} opens the command palette; {AppHotkeys.Gesture}+1 to 9 open the dock's first nine apps; Win+Alt+S puts the dock under the keyboard.\n" +
            "• Scroll over the volume widget or the media player to change the volume; Shift+scroll the player to skip.\n" +
            "• Right-click empty dock space for settings, or the tray icon if the dock is hidden.",
            12.5, subtle: true, flyout: true);
        tips.TextWrapping = TextWrapping.Wrap;
        tips.Margin = new Thickness(0, 12, 0, 0);
        p.Children.Add(tips);
        return p;
    }
}
