using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace lifeviz;

internal sealed class ProjectMSettingsWindow : Window
{
    private readonly ObservableCollection<string> _playlist;
    private readonly ListBox _library = new() { SelectionMode = SelectionMode.Extended };
    private readonly ListBox _selected = new() { SelectionMode = SelectionMode.Extended };
    private readonly TextBox _search = new();
    private readonly TextBlock _count = new();
    private string[] _all = Array.Empty<string>();
    private readonly ComboBox _order, _advance;
    private readonly TextBox _duration, _transition, _beats, _minimum;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private static readonly Brush PanelBrush = new SolidColorBrush(Color.FromRgb(42, 42, 42));
    public ProjectMSettings Result { get; private set; }

    public ProjectMSettingsWindow(ProjectMSettings settings, Func<string>? status = null, Action<int>? control = null)
    {
        Result = settings.Clone();
        _playlist = new(Result.Presets);
        Title = "MilkDrop / projectM — Presets & Playback";
        Width = 1080; Height = 760; MinWidth = 840; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(25, 25, 25)); Foreground = Brushes.WhiteSmoke;
        foreach (Type type in new[] { typeof(TextBox), typeof(ListBox), typeof(ComboBox), typeof(Button) })
        {
            var style = new Style(type);
            style.Setters.Add(new Setter(Control.BackgroundProperty, PanelBrush));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.WhiteSmoke));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6)));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 3, 7, 3)));
            Resources.Add(type, style);
        }
        // ComboBox popup entries use their own container colors.
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, PanelBrush));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.WhiteSmoke));
        Resources.Add(typeof(ComboBoxItem), itemStyle);
        var root = new DockPanel { Margin = new Thickness(18), LastChildFill = true, Background = Background };
        Content = root;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = Button("Save Playlist Settings", Save); save.IsDefault = true;
        var cancel = Button("Cancel", (_, _) => DialogResult = false); cancel.IsCancel = true;
        footer.Children.Add(save); footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var options = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
        DockPanel.SetDock(options, Dock.Bottom); root.Children.Add(options);
        var modes = new WrapPanel();
        _order = Choice(new[] { ("Shuffle without repeats", "Shuffle"), ("Ordered loop", "Ordered") }, Result.Order);
        _advance = Choice(new[] { ("After duration", "Timed"), ("After duration, on next beat", "TimedOnBeat"), ("Every N detected beats", "Beats"), ("Hold / manual only", "Hold") }, Result.Advance);
        modes.Children.Add(Field("Playlist order", _order)); modes.Children.Add(Field("Change preset", _advance)); options.Children.Add(modes);
        var numbers = new WrapPanel();
        _duration = Number(Result.DurationSeconds); _transition = Number(Result.TransitionSeconds);
        _beats = Number(Result.BeatsPerPreset); _minimum = Number(Result.MinimumSeconds);
        numbers.Children.Add(Field("Duration (seconds)", _duration));
        numbers.Children.Add(Field("Transition (0 = cut)", _transition));
        numbers.Children.Add(Field("Beats per preset", _beats));
        numbers.Children.Add(Field("Minimum seconds for beat changes", _minimum));
        options.Children.Add(numbers);
        options.Children.Add(new TextBlock { Text = "Audio follows LifeViz's Audio Source. Automatic changes wait for the current transition to finish.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) });
        var transport = new WrapPanel();
        foreach (var pair in new[] { ("Previous", -1), ("Next", 1), ("Retry / Restart", 0) })
        {
            var button = Button(pair.Item1, (_, _) => control?.Invoke(pair.Item2));
            button.IsEnabled = control != null; transport.Children.Add(button);
        }
        options.Children.Add(transport);
        var current = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxHeight = 52, Text = status?.Invoke() ?? "Draft playlist: apply the scene to hear and see playback." };
        options.Children.Add(current);
        if (status != null)
        {
            _statusTimer.Tick += (_, _) => current.Text = status();
            _statusTimer.Start();
        }
        Closed += (_, _) => _statusTimer.Stop();

        var columns = new Grid(); columns.ColumnDefinitions.Add(new()); columns.ColumnDefinitions.Add(new()); root.Children.Add(columns);
        var libraryPanel = new DockPanel(); var selectedPanel = new DockPanel { Margin = new Thickness(12, 0, 0, 0) };
        columns.Children.Add(libraryPanel); Grid.SetColumn(selectedPanel, 1); columns.Children.Add(selectedPanel);
        AddTop(libraryPanel, new TextBlock { Text = "Bundled preset library", FontSize = 17, FontWeight = FontWeights.SemiBold });
        _search.ToolTip = "Filter by category, author, or preset name";
        AddTop(libraryPanel, new TextBlock { Text = "Search category, author, or name", Margin = new Thickness(0, 8, 0, 0) });
        AddTop(libraryPanel, _search); AddTop(libraryPanel, _count);
        _search.TextChanged += (_, _) => Filter();
        var libraryButtons = new WrapPanel();
        libraryButtons.Children.Add(Button("Add Selected →", (_, _) => Add(_library.SelectedItems.Cast<string>())));
        libraryButtons.Children.Add(Button("Import .milk Files...", Import));
        DockPanel.SetDock(libraryButtons, Dock.Bottom); libraryPanel.Children.Add(libraryButtons);
        libraryPanel.Children.Add(_library);
        AddTop(selectedPanel, new TextBlock { Text = "This layer's playlist", FontSize = 17, FontWeight = FontWeights.SemiBold });
        AddTop(selectedPanel, new TextBlock { Text = "Ctrl/Shift selects multiple presets. Changes take effect when saved.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 5) });
        var playlistButtons = new WrapPanel();
        playlistButtons.Children.Add(Button("Remove", (_, _) => { foreach (string p in _selected.SelectedItems.Cast<string>().ToArray()) _playlist.Remove(p); }));
        playlistButtons.Children.Add(Button("Move Up", (_, _) => Move(-1)));
        playlistButtons.Children.Add(Button("Move Down", (_, _) => Move(1)));
        DockPanel.SetDock(playlistButtons, Dock.Bottom); selectedPanel.Children.Add(playlistButtons);
        _selected.ItemsSource = _playlist; selectedPanel.Children.Add(_selected);
        foreach (ListBox list in new[] { _library, _selected })
        {
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        }
        Loaded += async (_, _) =>
        {
            _count.Text = "Loading preset library...";
            try { _all = await Task.Run(() => ProjectMLibrary.Presets.ToArray()); Filter(); }
            catch (Exception ex) { _count.Text = $"Preset library unavailable: {ex.Message}"; }
        };
    }

    internal void PopulateLibraryForSmoke() { _all = ProjectMLibrary.Presets.ToArray(); Filter(); }

    private void Filter()
    {
        var words = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = _all.Where(p => words.All(w => p.Contains(w, StringComparison.OrdinalIgnoreCase))).ToArray();
        _library.ItemsSource = matches;
        _count.Text = $"{matches.Length:N0} matching presets / {_all.Length:N0} bundled";
    }
    private void Add(System.Collections.Generic.IEnumerable<string> paths)
    {
        foreach (string path in paths.ToArray()) if (!_playlist.Contains(path, StringComparer.OrdinalIgnoreCase)) _playlist.Add(path);
    }
    private void Import(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import MilkDrop presets", Filter = "MilkDrop presets (*.milk)|*.milk", Multiselect = true };
        if (dialog.ShowDialog(this) == true) Add(dialog.FileNames);
    }
    private void Move(int direction)
    {
        var selected = _selected.SelectedItems.Cast<string>().ToHashSet();
        var indices = Enumerable.Range(0, _playlist.Count).Where(i => selected.Contains(_playlist[i])).ToArray();
        if (direction > 0) Array.Reverse(indices);
        foreach (int i in indices)
        {
            int next = i + direction;
            if (next >= 0 && next < _playlist.Count && !selected.Contains(_playlist[next])) _playlist.Move(i, next);
        }
        foreach (string p in selected) if (!_selected.SelectedItems.Contains(p)) _selected.SelectedItems.Add(p);
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        if (!TryNumber(_duration, 0.1, 86400, out double duration) || !TryNumber(_transition, 0, 30, out double transition)
            || !TryNumber(_beats, 1, 4096, out double beats) || beats != Math.Truncate(beats)
            || !TryNumber(_minimum, 0, 86400, out double minimum))
        {
            MessageBox.Show(this, "Use a duration from 0.1–86400 seconds, transition from 0–30 seconds, a whole beat count from 1–4096, and a minimum from 0–86400 seconds.", "Preset timing", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Result.Presets = _playlist.ToList(); Result.Order = (string)((ComboBoxItem)_order.SelectedItem).Tag;
        Result.Advance = (string)((ComboBoxItem)_advance.SelectedItem).Tag;
        Result.DurationSeconds = duration; Result.TransitionSeconds = transition; Result.BeatsPerPreset = (int)beats; Result.MinimumSeconds = minimum;
        DialogResult = true;
    }
    private static bool TryNumber(TextBox box, double min, double max, out double value) => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value) && value >= min && value <= max;
    private static TextBox Number(double value) => new() { Text = value.ToString(CultureInfo.CurrentCulture), Width = 105, HorizontalAlignment = HorizontalAlignment.Left };
    private static ComboBox Choice((string label, string value)[] values, string selected)
    {
        var box = new ComboBox { MinWidth = 200, Foreground = Brushes.Black };
        foreach (var pair in values)
        {
            var item = new ComboBoxItem { Content = pair.label, Tag = pair.value };
            box.Items.Add(item); if (pair.value == selected) box.SelectedItem = item;
        }
        return box;
    }
    private static StackPanel Field(string label, UIElement control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 12, 2) };
        panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(control); return panel;
    }
    private static Button Button(string title, RoutedEventHandler action)
    {
        var button = new Button { Content = title, MinHeight = 30 }; button.Click += action; return button;
    }
    private static void AddTop(DockPanel panel, UIElement element) { DockPanel.SetDock(element, Dock.Top); panel.Children.Add(element); }
}
