using System.Media;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Strata.Controls;
using Strata.Core;
using Strata.Smart;

namespace Strata.Widgets;

public sealed class TodoItem
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

public sealed class TodoWidget : Widget
{
    private const string FileName = "todos.json";
    private static List<TodoItem>? _items;
    private static List<TodoItem> Items => _items ??= Storage.Load<List<TodoItem>>(FileName);
    private readonly TextBlock _count;

    /// <summary>Replace the list (preview fixtures).</summary>
    public static void UseFixture(List<TodoItem> items) => _items = items;

    public TodoWidget(IDockHost host) : base(host)
    {
        _count = UI.Text("", Settings.FontSize, weight: FontWeights.SemiBold);
        Child = UI.Row(6, UI.Icon("", Settings.FontSize + 2), _count);
        Announce("Todos");
    }

    public override void Tick()
    {
        int open = Items.Count(i => !i.Done);
        _count.Text = open.ToString();
        ToolTip = open == 0 ? "No open todos" : $"{open} open todo{(open == 1 ? "" : "s")}";
        Announce("Todos", ToolTip as string);
    }

    protected override FrameworkElement BuildFlyout()
    {
        var list = new StackPanel();
        var input = UI.Input(placeholder: "Add a todo and press Enter");
        var clear = UI.TextButton("Clear completed", () => { Items.RemoveAll(i => i.Done); Save(); Render(); });
        var scroller = new ScrollViewer { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list, Focusable = false };
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || input.Text.Trim().Length == 0) return;
            Items.Insert(0, new TodoItem { Text = input.Text.Trim() });
            input.Text = "";
            Save();
            Render();
        };

        void Save() { Storage.Save(FileName, Items); Tick(); }

        void Render()
        {
            list.Children.Clear();
            if (Items.Count == 0) list.Children.Add(UI.Text("Nothing to do.", 12.5, subtle: true, flyout: true));
            foreach (var item in Items.OrderBy(i => i.Done))
            {
                // A real check box (Toggle pattern), drawn as a round tick.
                var mark = UI.Icon(item.Done ? "" : "", 10);
                mark.Foreground = Brushes.White;
                mark.HorizontalAlignment = HorizontalAlignment.Center;
                var check = new DockButton
                {
                    Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Padding = new Thickness(0),
                    BorderThickness = new Thickness(1.5), Content = mark, VerticalAlignment = VerticalAlignment.Center,
                };
                check.SetResourceReference(BorderBrushProperty, item.Done ? "Accent" : "FlyoutSubtle");
                if (item.Done) check.SetResourceReference(BackgroundProperty, "Accent");
                else check.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // hit-testable
                check.Named($"{item.Text}, {(item.Done ? "done" : "not done")}", "Press to toggle");
                check.Click += (_, _) => { item.Done = !item.Done; Save(); Render(); };

                var text = UI.Text(item.Text, 13, subtle: item.Done, flyout: true);
                text.TextWrapping = TextWrapping.Wrap;
                if (item.Done) text.TextDecorations = TextDecorations.Strikethrough;
                text.Margin = new Thickness(10, 0, 6, 0);

                var del = UI.IconButton("", () => { Items.Remove(item); Save(); Render(); }, 10, $"Delete {item.Text}");
                del.Opacity = 0;
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), Background = Brushes.Transparent };
                void ShowDelete(bool show) => del.Opacity = show ? 1 : 0;
                row.MouseEnter += (_, _) => ShowDelete(true);
                row.MouseLeave += (_, _) => ShowDelete(row.IsKeyboardFocusWithin);
                row.IsKeyboardFocusWithinChanged += (_, _) => ShowDelete(row.IsKeyboardFocusWithin || row.IsMouseOver);
                DockPanel.SetDock(check, Dock.Left);
                DockPanel.SetDock(del, Dock.Right);
                row.Children.Add(check);
                row.Children.Add(del);
                row.Children.Add(text);
                list.Children.Add(row);
            }
        }

        Render();
        var panel = UI.Column(10, UI.Header("Todos"), input, scroller, clear);
        panel.Loaded += (_, _) => input.Focus();
        return panel;
    }
}

public sealed class NotesWidget : Widget
{
    private const string FileName = "notes.txt";

    public NotesWidget(IDockHost host) : base(host)
    {
        Child = UI.Icon("", Settings.FontSize + 3);
        ToolTip = "Notes";
        Announce("Notes");
    }

    protected override double FlyoutWidth => 360;

    protected override FrameworkElement BuildFlyout()
    {
        string path = Storage.PathFor(FileName);
        string text = File.Exists(path) ? File.ReadAllText(path) : "";
        var box = UI.Input(text, "Write anything. It saves as you type.", multiline: true);
        box.Height = 280;
        var status = UI.Text("Saved on this PC", 11.5, subtle: true, flyout: true);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var save = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        save.Tick += (_, _) =>
        {
            save.Stop();
            try { File.WriteAllText(path, box.Text); status.Text = "Saved on this PC"; }
            catch (Exception ex) { status.Text = "Could not save: " + ex.Message; }
        };
        box.TextChanged += (_, _) => { status.Text = "Saving..."; save.Stop(); save.Start(); };
        box.Unloaded += (_, _) => { if (save.IsEnabled) { save.Stop(); File.WriteAllText(path, box.Text); } };
        var panel = UI.Column(8, UI.Header("Notes"), box, status);
        panel.Loaded += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; };
        return panel;
    }
}

/// <summary>Shared pomodoro state, so it keeps running when the dock rebuilds after a settings change.</summary>
public static class PomodoroTimer
{
    public enum Phase { Work, Break }
    public static Phase Current { get; private set; } = Phase.Work;
    public static bool Running { get; private set; }
    public static int CompletedToday { get; private set; }
    private static DateTime _endsAt;
    private static TimeSpan _remaining = TimeSpan.Zero;
    private static DateTime _countDate = AppClock.Today;

    public static TimeSpan Remaining(AppSettings s) =>
        Running ? Max(_endsAt - AppClock.Now) : _remaining > TimeSpan.Zero ? _remaining : Length(s);

    private static TimeSpan Max(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
    private static TimeSpan Length(AppSettings s) =>
        TimeSpan.FromMinutes(Current == Phase.Work ? Math.Max(1, s.PomodoroWorkMinutes) : Math.Max(1, s.PomodoroBreakMinutes));

    /// <summary>The focus signal, rounded to whole minutes so it changes once a minute, not every second.</summary>
    public static PomodoroState State(AppSettings s) => new(Running, Current == Phase.Work ? FocusPhase.Work : FocusPhase.Break,
        TimeSpan.FromMinutes(Math.Ceiling(Remaining(s).TotalMinutes)), CompletedToday);

    public static void Toggle(AppSettings s)
    {
        if (Running) { _remaining = Max(_endsAt - AppClock.Now); Running = false; }
        else { _endsAt = AppClock.Now + Remaining(s); Running = true; }
    }

    /// <summary>Start a focus session (used by the "start focus" automation action).</summary>
    public static void StartFocus(AppSettings s)
    {
        Current = Phase.Work;
        _remaining = TimeSpan.Zero;
        _endsAt = AppClock.Now + Length(s);
        Running = true;
    }

    public static void Reset() { Running = false; _remaining = TimeSpan.Zero; }

    public static void Skip(AppSettings s)
    {
        Current = Current == Phase.Work ? Phase.Break : Phase.Work;
        _remaining = TimeSpan.Zero;
        if (Running) _endsAt = AppClock.Now + Length(s);
    }

    public static void Tick(IDockHost host)
    {
        if (_countDate != AppClock.Today) { _countDate = AppClock.Today; CompletedToday = 0; }
        if (!Running || AppClock.Now < _endsAt) return;
        // The user started this timer, so its alerts are urgent (they ignore quiet hours).
        if (Current == Phase.Work)
        {
            CompletedToday++;
            host.Notify("pomodoro:" + AppClock.Now.Ticks, "Focus session done", $"Take a {host.Settings.PomodoroBreakMinutes}-minute break.", urgent: true);
        }
        else host.Notify("pomodoro:" + AppClock.Now.Ticks, "Break over", "Ready for the next focus session?", urgent: true);
        try { SystemSounds.Asterisk.Play(); } catch { /* no sound device */ }
        Current = Current == Phase.Work ? Phase.Break : Phase.Work;
        _remaining = TimeSpan.Zero;
        // Breaks start automatically; a new focus session waits for you.
        Running = Current == Phase.Break;
        if (Running) _endsAt = AppClock.Now + Length(host.Settings);
    }

    public static string Format(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
}

public sealed class PomodoroWidget : Widget
{
    private readonly TextBlock _icon, _time;

    public PomodoroWidget(IDockHost host) : base(host)
    {
        _icon = UI.Icon("", Settings.FontSize + 2);
        _time = UI.Text("", Settings.FontSize, weight: FontWeights.SemiBold);
        _time.MinWidth = 38;
        Child = UI.Row(6, _icon, _time);
        Announce("Focus timer");
    }

    public override void Tick()
    {
        var left = PomodoroTimer.Remaining(Settings);
        _time.Text = PomodoroTimer.Format(left);
        bool active = PomodoroTimer.Running;
        _icon.SetResourceReference(TextBlock.ForegroundProperty, active ? "Accent" : "Fg");
        ToolTip = active ? $"{PomodoroTimer.Current} - {PomodoroTimer.Format(left)} left" : "Pomodoro timer - click to start";
        Announce("Focus timer", active ? $"{PomodoroTimer.Current}, {(int)left.TotalMinutes} minutes left" : "stopped");
    }

    protected override double FlyoutWidth => 300;

    protected override FrameworkElement BuildFlyout()
    {
        var phase = UI.Text("", 13, subtle: true, flyout: true);
        var time = UI.Text("", 44, flyout: true, weight: FontWeights.SemiBold);
        time.FontFamily = Theme.DisplayFont;
        time.HorizontalAlignment = phase.HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetLiveSetting(phase, AutomationLiveSetting.Polite);
        var startLabel = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        var start = UI.Button(startLabel, () => { PomodoroTimer.Toggle(Settings); Tick(); }, accent: true, padding: new Thickness(18, 8, 18, 8));
        var reset = UI.TextButton("Reset", () => { PomodoroTimer.Reset(); Tick(); });
        var skip = UI.TextButton("Skip", () => { PomodoroTimer.Skip(Settings); Tick(); });
        var buttons = UI.Row(8, start, reset, skip);
        buttons.HorizontalAlignment = HorizontalAlignment.Center;
        var done = UI.Text("", 12, subtle: true, flyout: true);
        done.HorizontalAlignment = HorizontalAlignment.Center;
        var panel = UI.Column(8, UI.Header("Focus timer"), phase, time, buttons, done);
        return panel.Updating(() =>
        {
            phase.Text = PomodoroTimer.Current == PomodoroTimer.Phase.Work ? "Focus" : "Break";
            time.Text = PomodoroTimer.Format(PomodoroTimer.Remaining(Settings));
            startLabel.Text = PomodoroTimer.Running ? "Pause" : "Start";
            start.Named(startLabel.Text);
            done.Text = $"{PomodoroTimer.CompletedToday} session{(PomodoroTimer.CompletedToday == 1 ? "" : "s")} today · {Settings.PomodoroWorkMinutes}/{Settings.PomodoroBreakMinutes} min";
        });
    }
}
