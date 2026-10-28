using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Strata.Controls;
using Strata.Core;
using Strata.Native;

namespace Strata.Views;

/// <summary>A fixed-geometry, keyboard-first surface over a typed command catalog.</summary>
public sealed class CommandPaletteWindow : Window
{
    public const double PaletteWidth = 680;
    public const double PaletteHeight = 400;
    public const int VisibleResultLimit = 6;

    private readonly IReadOnlyList<PaletteCommand> _catalog;
    private readonly TextBox _query = new();
    private readonly ListBox _results = new();
    private readonly TextBlock _status = new();
    private IReadOnlyList<PaletteMatch> _matches = [];

    public int ResultCount => _matches.Count;

    public CommandPaletteWindow(IReadOnlyList<PaletteCommand> catalog)
    {
        _catalog = catalog;
        Title = "Strata command palette";
        Width = MinWidth = MaxWidth = PaletteWidth;
        Height = MinHeight = MaxHeight = PaletteHeight;
        SizeToContent = SizeToContent.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Theme.UiFont;
        Background = Brushes.Transparent;
        AutomationProperties.SetName(this, Title);

        var card = new Border
        {
            CornerRadius = new CornerRadius(WindowComposition.SystemCornerRadiusDip),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18),
        };
        card.SetResourceReference(Border.BackgroundProperty, "FlyoutBg");
        card.SetResourceReference(Border.BorderBrushProperty, "FlyoutBorder");

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(276) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _query.Height = 44;
        _query.FontSize = 17;
        _query.Padding = new Thickness(13, 8, 13, 8);
        _query.BorderThickness = new Thickness(1);
        _query.SetResourceReference(Control.BackgroundProperty, "InputBg");
        _query.SetResourceReference(Control.ForegroundProperty, "FlyoutFg");
        _query.SetResourceReference(Control.BorderBrushProperty, "FlyoutBorder");
        _query.SetResourceReference(TextBox.CaretBrushProperty, "FlyoutFg");
        _query.Named("Search Strata commands", "Type to filter applications, windows, settings, widgets, actions, and automations.");
        _query.TextChanged += (_, _) => RefreshResults();
        _query.PreviewKeyDown += OnQueryKeyDown;
        layout.Children.Add(_query);

        _results.Height = 276;
        _results.BorderThickness = new Thickness(0);
        _results.Background = Brushes.Transparent;
        _results.SetResourceReference(Control.ForegroundProperty, "FlyoutFg");
        _results.ItemContainerStyle = CreateResultItemStyle();
        ScrollViewer.SetVerticalScrollBarVisibility(_results, ScrollBarVisibility.Hidden);
        _results.MouseDoubleClick += (_, _) => ExecuteSelected();
        _results.SelectionChanged += (_, _) => AnnounceSelection();
        AutomationProperties.SetName(_results, "Command results");
        Grid.SetRow(_results, 2);
        layout.Children.Add(_results);

        _status.FontSize = 11.5;
        _status.Margin = new Thickness(4, 7, 4, 0);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "FlyoutSubtle");
        AutomationProperties.SetName(_status, "Command palette status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Grid.SetRow(_status, 3);
        layout.Children.Add(_status);

        card.Child = layout;
        Content = card;

        SourceInitialized += (_, _) => WindowComposition.Apply(new WindowInteropHelper(this).Handle,
            rounded: true, darkMode: !Theme.Current.IsLight,
            systemBackdrop: MaterialPolicy.AllowsSystemBackdrop(MaterialEnvironment.ReadCurrent()));
        Loaded += (_, _) => { RefreshResults(); _query.Focus(); Keyboard.Focus(_query); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Deactivated += (_, _) => { if (IsLoaded) Close(); };
    }

    public void SetQuery(string query)
    {
        _query.Text = query;
        _query.CaretIndex = _query.Text.Length;
        RefreshResults();
    }

    private void RefreshResults()
    {
        _matches = PaletteMatcher.Search(_catalog, _query.Text, VisibleResultLimit);
        _results.Items.Clear();
        foreach (var match in _matches)
        {
            var title = new TextBlock
            {
                Text = match.Command.Title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            title.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground))
                { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1) });
            var subtitle = new TextBlock
            {
                Text = match.Command.Subtitle,
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            subtitle.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground))
                { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1) });
            var row = new StackPanel { Margin = new Thickness(10, 5, 10, 5) };
            row.Children.Add(title);
            row.Children.Add(subtitle);
            var item = new ListBoxItem
            {
                Content = row,
                Height = 46,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = match.Command,
                ToolTip = string.IsNullOrWhiteSpace(match.Command.Subtitle) ? match.Command.Title : $"{match.Command.Title} — {match.Command.Subtitle}",
            };
            AutomationProperties.SetName(item, $"{match.Command.Title}. {match.Command.Subtitle}. {match.Command.Kind}");
            _results.Items.Add(item);
        }
        if (_results.Items.Count > 0) _results.SelectedIndex = 0;
        string text = _matches.Count == 0 ? "No matching commands" : $"{_matches.Count} command{(_matches.Count == 1 ? "" : "s")}. Use arrow keys and Enter.";
        SetStatus(text);
    }

    /// <summary>The query retains typing focus; this explicit selection state is therefore the keyboard focus cue.</summary>
    public static Style CreateResultItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("FlyoutFg")));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("InputBg")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, SystemColors.HighlightBrush));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.HighlightTextBrush));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, SystemColors.HighlightTextBrush));
        selected.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2)));
        style.Triggers.Add(selected);
        return style;
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up)
        {
            if (_results.Items.Count == 0) return;
            int delta = e.Key == Key.Down ? 1 : -1;
            _results.SelectedIndex = Math.Clamp(_results.SelectedIndex + delta, 0, _results.Items.Count - 1);
            _results.ScrollIntoView(_results.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ExecuteSelected();
            e.Handled = true;
        }
    }

    private void AnnounceSelection()
    {
        if (_results.SelectedItem is ListBoxItem { Tag: PaletteCommand command })
            SetStatus($"Selected {command.Title}. {command.Subtitle}");
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        AutomationProperties.SetName(_status, text);
    }

    private void ExecuteSelected()
    {
        if (_results.SelectedItem is not ListBoxItem { Tag: PaletteCommand command }) return;
        if (command.Confirmation is { Length: > 0 } confirmation &&
            MessageBox.Show(this, confirmation, "Strata", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        try
        {
            command.Execute();
            Close();
        }
        catch (Exception ex)
        {
            Log.Write($"Command palette {command.Kind} action failed: {ex.GetType().Name}");
            SetStatus("That command could not be completed.");
        }
    }
}
