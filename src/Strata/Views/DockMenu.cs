using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Strata.Controls;
using Strata.Core;
using Strata.Widgets;

namespace Strata.Views;

/// <summary>A right-click menu drawn like the dock's flyouts (the stock WPF menu is always light).</summary>
public sealed class DockMenu
{
    private abstract record Entry;
    private sealed record HeaderEntry(string Text) : Entry;
    private sealed record SeparatorEntry : Entry;
    private sealed record ItemEntry(string Text, Action Click, string? Glyph, bool Enabled, bool Danger) : Entry;

    private readonly List<Entry> _entries = [];

    public void Header(string text) => _entries.Add(new HeaderEntry(text));
    public void Separator() => _entries.Add(new SeparatorEntry());

    public void Add(string text, Action click, string? glyph = null, bool enabled = true, bool danger = false) =>
        _entries.Add(new ItemEntry(text, click, glyph, enabled, danger));

    /// <summary>Item labels in order, for previews and tests.</summary>
    public IReadOnlyList<string> Labels => _entries.OfType<ItemEntry>().Select(e => e.Text).ToList();

    public FrameworkElement Build(Action close)
    {
        var panel = new StackPanel { Margin = new Thickness(-8, -6, -8, -8) };
        foreach (var entry in _entries)
        {
            switch (entry)
            {
                case HeaderEntry h:
                    var t = UI.Text(h.Text, 12, subtle: true, flyout: true, weight: FontWeights.SemiBold);
                    t.Margin = new Thickness(10, 2, 10, 6);
                    panel.Children.Add(t);
                    break;
                case SeparatorEntry:
                    panel.Children.Add(UI.Separator().Also(s => s.Margin = new Thickness(6, 4, 6, 4)));
                    break;
                case ItemEntry item:
                    panel.Children.Add(Row(item, close));
                    break;
            }
        }
        return panel;
    }

    private static FlatButton Row(ItemEntry item, Action close)
    {
        var icon = UI.Icon(item.Glyph ?? "", 13, subtle: !item.Danger, flyout: true);
        icon.Width = 18;
        var text = UI.Text(item.Text, 13, flyout: true);
        if (item.Danger)
        {
            var red = new SolidColorBrush(Color.FromRgb(0xF2, 0x6D, 0x6D));
            red.Freeze();
            icon.Foreground = text.Foreground = red;
        }
        // DockPanel (not a StackPanel) so long window titles get a trailing ellipsis.
        var line = new DockPanel();
        icon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        line.Children.Add(icon);
        line.Children.Add(text);
        var row = new FlatButton
        {
            Content = line,
            Padding = new Thickness(10, 7, 10, 7),
            Background = Brushes.Transparent,
            Cursor = item.Enabled ? Cursors.Hand : Cursors.Arrow,
            IsEnabled = item.Enabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        row.Named(item.Text);
        if (!item.Enabled) return row;
        row.Click += (_, e) =>
        {
            e.Handled = true;
            close();
            try { item.Click(); }
            catch (Exception ex) { Log.Write($"Menu action '{item.Text}' failed: {ex.Message}"); }
        };
        return row;
    }
}
