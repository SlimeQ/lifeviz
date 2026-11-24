using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace lifeviz;

public partial class MainWindow
{
    private readonly record struct LiveVjFrame(bool Held, long ReleaseSerial, long SlamSerial);
    private sealed record LiveVjSnapshot(Guid Target, LiveVjFrame Frame);

    private static readonly LiveVjSnapshot EmptyLiveVjSnapshot = new(Guid.Empty, default);
    private LiveVjSnapshot _liveVjSnapshot = EmptyLiveVjSnapshot;
    private bool _liveVjEnabled, _liveVjGamepadHold;
    private Guid _liveVjTarget;
    private long _liveVjSerial;
    private readonly LiveVjButtonEdges _liveVjEdges = new();
    private readonly LiveVjController _liveVjController = new();
    private DispatcherTimer? _liveVjTimer;
    private MenuItem? _liveVjMenu;

    // Capture a single immutable value: input events are dispatched on the UI thread.
    private LiveVjFrame GetLiveVjFrame(Guid layerId)
    {
        LiveVjSnapshot snapshot = Volatile.Read(ref _liveVjSnapshot);
        return layerId != Guid.Empty && snapshot.Target == layerId ? snapshot.Frame : default;
    }

    private void InitializeLiveVjControls()
    {
        if (_liveVjMenu != null) return;
        _liveVjMenu = new MenuItem { Header = "Live VJ" };
        _liveVjMenu.Items.Add(new MenuItem { Header = "Live VJ", IsEnabled = false });
        _liveVjMenu.SubmenuOpened += (sender, e) =>
        {
            if (ReferenceEquals(sender, e.Source)) RebuildLiveVjMenu();
        };
        RootContextMenu.Items.Insert(Math.Min(4, RootContextMenu.Items.Count), _liveVjMenu);

        PreviewKeyDown += LiveVj_PreviewKeyDown;
        Deactivated += LiveVj_Deactivated;
        _liveVjTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        _liveVjTimer.Tick += (_, _) => PollLiveVjController();
    }

    private void StopLiveVjControls()
    {
        _liveVjTimer?.Stop();
        PreviewKeyDown -= LiveVj_PreviewKeyDown;
        Deactivated -= LiveVj_Deactivated;
        _liveVjEnabled = false;
        ResetLiveVjInput();
    }

    private void ResetLiveVjInput()
    {
        _liveVjGamepadHold = false;
        _liveVjEdges.Reset();
        Volatile.Write(ref _liveVjSnapshot, EmptyLiveVjSnapshot);
    }

    private void LiveVj_Deactivated(object? sender, EventArgs e)
    {
        ResetLiveVjInput();
    }

    private bool LiveVjSessionIsFocused() =>
        _liveVjEnabled && _liveVjTarget != Guid.Empty && IsActive && !_isOfflineRendering;

    private void PollLiveVjController()
    {
        if (!LiveVjSessionIsFocused())
        {
            _liveVjEdges.Reset();
            return;
        }
        if (!_liveVjController.TryRead(out ushort buttons))
        {
            _liveVjEdges.Reset();
            if (_liveVjGamepadHold)
            {
                _liveVjGamepadHold = false;
                // Connection loss is not a deliberate Release performance gesture.
                LiveVjFrame frame = GetLiveVjFrame(_liveVjTarget);
                Volatile.Write(ref _liveVjSnapshot, new LiveVjSnapshot(_liveVjTarget, frame with { Held = false }));
            }
            return;
        }
        if (RootContextMenu.IsOpen)
        {
            // Still detect unplug while the menu is open, but don't accept gestures.
            _liveVjEdges.Reset();
            return;
        }
        ApplyLiveVjAction(_liveVjEdges.Observe(buttons), fromGamepad: true);
    }

    private void LiveVj_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!LiveVjSessionIsFocused() || RootContextMenu.IsOpen || e.Handled ||
            Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift) ||
            e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or ComboBox)
        {
            return;
        }
        LiveVjAction action = (e.Key == Key.System ? e.SystemKey : e.Key) switch
        {
            Key.F9 => LiveVjAction.Hold,
            Key.F10 => LiveVjAction.Release,
            Key.F11 => LiveVjAction.Slam,
            _ => LiveVjAction.None
        };
        if (action == LiveVjAction.None) return;
        e.Handled = true;
        if (!e.IsRepeat) ApplyLiveVjAction(action);
    }

    private void ApplyLiveVjAction(LiveVjAction action, bool fromGamepad = false)
    {
        if (action == LiveVjAction.None || !_liveVjEnabled ||
            _liveVjTarget == Guid.Empty || _isOfflineRendering)
        {
            return;
        }

        LiveVjFrame old = GetLiveVjFrame(_liveVjTarget);
        LiveVjFrame next;
        switch (action)
        {
            case LiveVjAction.Hold:
                if (old.Held) return;
                _liveVjGamepadHold = fromGamepad;
                next = old with { Held = true };
                break;
            case LiveVjAction.Release:
                _liveVjGamepadHold = false;
                next = old with { Held = false, ReleaseSerial = ++_liveVjSerial };
                break;
            case LiveVjAction.Slam:
                _liveVjGamepadHold = false;
                next = old with { Held = false, SlamSerial = ++_liveVjSerial };
                break;
            default:
                return;
        }
        Volatile.Write(ref _liveVjSnapshot, new LiveVjSnapshot(_liveVjTarget, next));
    }

    private void RebuildLiveVjMenu()
    {
        if (_liveVjMenu == null) return;
        var targets = new List<(Guid Id, string Label)>();
        AddLiveVjTargets(_sources, "", targets);
        if (!targets.Exists(item => item.Id == _liveVjTarget))
        {
            _liveVjTarget = Guid.Empty;
            ResetLiveVjInput();
        }

        _liveVjMenu.Items.Clear();
        var enabled = new MenuItem
        {
            Header = "Enable Live VJ for this session",
            IsCheckable = true,
            IsChecked = _liveVjEnabled
        };
        enabled.Click += (_, _) =>
        {
            _liveVjEnabled = enabled.IsChecked;
            ResetLiveVjInput();
            if (_liveVjEnabled) _liveVjTimer?.Start();
            else _liveVjTimer?.Stop();
        };
        _liveVjMenu.Items.Add(enabled);

        var targetMenu = new MenuItem { Header = "Target simulation layer" };
        var clear = new MenuItem { Header = "None", IsCheckable = true, IsChecked = _liveVjTarget == Guid.Empty };
        clear.Click += (_, _) => SelectLiveVjTarget(Guid.Empty);
        targetMenu.Items.Add(clear);
        foreach ((Guid id, string label) in targets)
        {
            var choice = new MenuItem
            {
                Header = label.Replace("_", "__", StringComparison.Ordinal),
                IsCheckable = true,
                IsChecked = id == _liveVjTarget
            };
            choice.Click += (_, _) => SelectLiveVjTarget(id);
            targetMenu.Items.Add(choice);
        }
        if (targets.Count == 0)
            targetMenu.Items.Add(new MenuItem { Header = "Add a compatible layer in the Scene Editor", IsEnabled = false });
        _liveVjMenu.Items.Add(targetMenu);
        _liveVjMenu.Items.Add(new Separator());
        AddLiveVjActionItem("Hold", "Xbox A / Ctrl+Shift+F9", LiveVjAction.Hold);
        AddLiveVjActionItem("Release", "Xbox B / Ctrl+Shift+F10", LiveVjAction.Release);
        AddLiveVjActionItem("Slam", "Xbox X / Ctrl+Shift+F11", LiveVjAction.Slam);
    }

    private void SelectLiveVjTarget(Guid id)
    {
        if (_liveVjTarget == id) return;
        _liveVjTarget = id;
        ResetLiveVjInput();
    }

    private void AddLiveVjActionItem(string label, string shortcut, LiveVjAction action)
    {
        var option = new MenuItem
        {
            Header = label,
            InputGestureText = shortcut,
            IsEnabled = _liveVjEnabled && _liveVjTarget != Guid.Empty && !_isOfflineRendering
        };
        option.Click += (_, _) => ApplyLiveVjAction(action);
        _liveVjMenu!.Items.Add(option);
    }

    private static void AddLiveVjTargets(IEnumerable<CaptureSource> sources, string path, List<(Guid, string)> result)
    {
        foreach (CaptureSource source in sources)
        {
            if (!source.Enabled) continue;
            string label = path + source.DisplayName + " / ";
            if (source.Type == CaptureSource.SourceType.SimGroup)
            {
                AddLiveVjSimulationTargets(source.SimulationLayers, label, result);
            }
            AddLiveVjTargets(source.Children, label, result);
        }
    }

    private static void AddLiveVjSimulationTargets(IReadOnlyList<SimulationLayerSpec> layers, string path, List<(Guid, string)> result)
    {
        for (int i = 0; i < layers.Count; i++)
        {
            SimulationLayerSpec layer = layers[i];
            if (!layer.Enabled) continue;
            string label = $"{path}{i + 1}. {layer.Name}";
            if (IsLiveVjCompatible(layer.LayerType))
                result.Add((layer.Id, label));
            AddLiveVjSimulationTargets(layer.Children, label + " / ", result);
        }
    }

    private static bool IsLiveVjCompatible(SimulationLayerType type) =>
        type is SimulationLayerType.Faultline or SimulationLayerType.MagnetChoir or
                SimulationLayerType.Stampede or SimulationLayerType.InkTrap;
}
