using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace lifeviz;

public partial class LayerEditorWindow : Window
{
    private readonly MainWindow _owner;
    private readonly LayerEditorViewModel _viewModel;
    private bool _suppressLiveUpdates;
    private static readonly JsonSerializerOptions LayerConfigJsonOptions = new() { WriteIndented = true };

    public LayerEditorWindow(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;
        Owner = owner;
        _viewModel = new LayerEditorViewModel();
        DataContext = _viewModel;
        RefreshFromSources();
        UpdateApplyState();
    }

    public void RefreshFromSourcesIfLive()
    {
        if (!_viewModel.LiveMode)
        {
            return;
        }

        RefreshFromSources();
    }

    private void RefreshFromSources()
    {
        _suppressLiveUpdates = true;
        try
        {
            var sources = _owner.BuildLayerEditorSources();
            _viewModel.Sources = new ObservableCollection<LayerEditorSource>(sources);
        }
        finally
        {
            _suppressLiveUpdates = false;
        }
    }

    private void UpdateApplyState()
    {
        if (ApplyButton != null)
        {
            ApplyButton.IsEnabled = !_viewModel.LiveMode;
        }
    }

    private bool ShouldApplyLive() => _viewModel.LiveMode && !_suppressLiveUpdates;

    private void LiveModeToggle(object sender, RoutedEventArgs e)
    {
        UpdateApplyState();
        if (_viewModel.LiveMode)
        {
            RefreshFromSources();
        }
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.LiveMode)
        {
            return;
        }

        _owner.ApplyLayerEditorSources(_viewModel.Sources.ToList());
        RefreshFromSources();
    }

    private void SaveLayerConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Layer Configuration",
            Filter = "LifeViz Layer Config (*.lifevizlayers.json)|*.lifevizlayers.json|JSON Files|*.json|All Files|*.*",
            DefaultExt = ".lifevizlayers.json",
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var config = LayerConfigFile.FromEditorSources(_viewModel.Sources);
            string json = JsonSerializer.Serialize(config, LayerConfigJsonOptions);
            File.WriteAllText(dialog.FileName, json);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to save layer configuration:\n{ex.Message}", "Save Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadLayerConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load Layer Configuration",
            Filter = "LifeViz Layer Config (*.lifevizlayers.json)|*.lifevizlayers.json|JSON Files|*.json|All Files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(dialog.FileName);
            var config = JsonSerializer.Deserialize<LayerConfigFile>(json);
            if (config == null)
            {
                MessageBox.Show(this, "That file did not contain a layer configuration.", "Load Failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var sources = config.ToEditorSources();
            if (_viewModel.LiveMode)
            {
                _owner.ApplyLayerEditorSources(sources);
                RefreshFromSources();
            }
            else
            {
                _viewModel.Sources = new ObservableCollection<LayerEditorSource>(sources);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to load layer configuration:\n{ex.Message}", "Load Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BlendMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: LayerEditorSource source })
        {
            _owner.UpdateSourceBlendMode(source.Id, source.BlendMode);
        }
    }

    private void FitMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: LayerEditorSource source })
        {
            _owner.UpdateSourceFitMode(source.Id, source.FitMode);
        }
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: LayerEditorSource source })
        {
            _owner.UpdateSourceOpacity(source.Id, source.Opacity);
        }
    }

    private void Mirror_Changed(object sender, RoutedEventArgs e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: LayerEditorSource source })
        {
            _owner.UpdateSourceMirror(source.Id, source.Mirror);
        }
    }

    private void KeyEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: LayerEditorSource source })
        {
            _owner.UpdateSourceKeyEnabled(source.Id, source.KeyEnabled);
        }
    }

    private void KeyTolerance_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: LayerEditorSource source })
        {
            _owner.UpdateSourceKeyTolerance(source.Id, source.KeyTolerance);
        }
    }

    private void KeyColorHex_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: LayerEditorSource source })
        {
            _owner.UpdateSourceKeyColor(source.Id, source.KeyColorHex);
        }
    }

    private void GroupName_Changed(object sender, TextChangedEventArgs e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: LayerEditorSource source })
        {
            _owner.UpdateGroupName(source.Id, source.DisplayName);
        }
    }

    private void MakePrimary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LayerEditorSource source })
        {
            if (_viewModel.LiveMode)
            {
                _owner.MakePrimaryFromEditor(source.Id);
                RefreshFromSources();
            }
            else
            {
                MoveSourceToIndex(source, 0);
            }
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSourceBy(sender, -1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSourceBy(sender, 1);

    private void MoveSourceBy(object sender, int delta)
    {
        if (sender is not FrameworkElement { DataContext: LayerEditorSource source })
        {
            return;
        }

        if (_viewModel.LiveMode)
        {
            _owner.MoveSourceFromEditor(source.Id, delta);
            RefreshFromSources();
            return;
        }

        var list = GetParentCollection(source);
        int index = list.IndexOf(source);
        if (index < 0)
        {
            return;
        }

        int next = Math.Clamp(index + delta, 0, list.Count - 1);
        if (next == index)
        {
            return;
        }

        list.Move(index, next);
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LayerEditorSource source })
        {
            return;
        }

        if (_viewModel.LiveMode)
        {
            _owner.RemoveSourceFromEditor(source.Id);
            RefreshFromSources();
            return;
        }

        GetParentCollection(source).Remove(source);
    }

    private void RestartVideo_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.LiveMode)
        {
            MessageBox.Show(this, "Enable Live Mode to restart video playback.", "Live Mode Required",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (sender is FrameworkElement { DataContext: LayerEditorSource source })
        {
            _owner.RestartVideoFromEditor(source.Id);
        }
    }

    private void AddZoom_Click(object sender, RoutedEventArgs e) => AddAnimation(sender, "ZoomIn", null, null);

    private void AddTranslate_Click(object sender, RoutedEventArgs e) => AddAnimation(sender, "Translate", "Translate", null);

    private void AddRotate_Click(object sender, RoutedEventArgs e) => AddAnimation(sender, "Rotate", null, "Rotate");

    private void AddDvd_Click(object sender, RoutedEventArgs e) => AddAnimation(sender, "DvdBounce", null, null);

    private void AddBeatShake_Click(object sender, RoutedEventArgs e) => AddAnimation(sender, "BeatShake", null, null);

    private void AddAudioGranular_Click(object sender, RoutedEventArgs e) => AddAnimation(sender, "AudioGranular", null, null);

    private void AddFade_Click(object sender, RoutedEventArgs e) => AddAnimation(sender, "Fade", null, null);

    private void AddAnimation(object sender, string type, string? translateKey, string? rotateKey)
    {
        if (sender is not FrameworkElement { DataContext: LayerEditorSource source })
        {
            return;
        }

        if (_viewModel.LiveMode)
        {
            string? translateDirection = translateKey != null ? source.PendingTranslateDirection : null;
            string? rotationDirection = rotateKey != null ? source.PendingRotationDirection : null;
            _owner.AddAnimationFromEditor(source.Id, type, translateDirection, rotationDirection);
            RefreshFromSources();
            return;
        }

        var animation = new LayerEditorAnimation
        {
            Id = Guid.NewGuid(),
            Type = type,
            Loop = type == "DvdBounce" ? "PingPong" : "Forward",
            Speed = "Normal",
            TranslateDirection = source.PendingTranslateDirection,
            RotationDirection = source.PendingRotationDirection,
            Parent = source
        };

        source.Animations.Add(animation);
    }

    private void RemoveAnimation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LayerEditorAnimation animation })
        {
            return;
        }

        var parent = animation.Parent;
        if (parent == null)
        {
            return;
        }

        if (_viewModel.LiveMode)
        {
            _owner.RemoveAnimationFromEditor(parent.Id, animation.Id);
            RefreshFromSources();
            return;
        }

        parent.Animations.Remove(animation);
    }

    private void AnimationLoop_Changed(object sender, SelectionChangedEventArgs e) => ApplyAnimationChange(sender);

    private void AnimationSpeed_Changed(object sender, SelectionChangedEventArgs e) => ApplyAnimationChange(sender);

    private void AnimationCycle_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyAnimationChange(sender);

    private void AnimationTranslateDirection_Changed(object sender, SelectionChangedEventArgs e) => ApplyAnimationChange(sender);

    private void AnimationRotateDirection_Changed(object sender, SelectionChangedEventArgs e) => ApplyAnimationChange(sender);

    private void AnimationRotateDegrees_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyAnimationChange(sender);

    private void AnimationSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyAnimationChange(sender);

    private void AnimationBeatShakeIntensity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyAnimationChange(sender);

    private void ApplyAnimationChange(object sender)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        if (sender is not FrameworkElement { DataContext: LayerEditorAnimation animation })
        {
            return;
        }

        var parent = animation.Parent;
        if (parent == null)
        {
            return;
        }

        _owner.UpdateAnimationFromEditor(parent.Id, animation);
    }

    private void AddRootGroup_Click(object sender, RoutedEventArgs e) => AddSource(null, LayerEditorSourceKind.Group);

    private void AddRootWindow_Click(object sender, RoutedEventArgs e) => AddSource(null, LayerEditorSourceKind.Window);

    private void AddRootWebcam_Click(object sender, RoutedEventArgs e) => AddSource(null, LayerEditorSourceKind.Webcam);

    private void AddRootFile_Click(object sender, RoutedEventArgs e) => AddSource(null, LayerEditorSourceKind.File);

    private void AddRootYoutube_Click(object sender, RoutedEventArgs e) => AddSource(null, LayerEditorSourceKind.Youtube);

    private void AddRootSequence_Click(object sender, RoutedEventArgs e) => AddSource(null, LayerEditorSourceKind.VideoSequence);

    private void AddChildGroup_Click(object sender, RoutedEventArgs e) => AddSource(GetSourceContext(sender), LayerEditorSourceKind.Group);

    private void AddChildWindow_Click(object sender, RoutedEventArgs e) => AddSource(GetSourceContext(sender), LayerEditorSourceKind.Window);

    private void AddChildWebcam_Click(object sender, RoutedEventArgs e) => AddSource(GetSourceContext(sender), LayerEditorSourceKind.Webcam);

    private void AddChildFile_Click(object sender, RoutedEventArgs e) => AddSource(GetSourceContext(sender), LayerEditorSourceKind.File);

    private void AddChildYoutube_Click(object sender, RoutedEventArgs e) => AddSource(GetSourceContext(sender), LayerEditorSourceKind.Youtube);

    private void AddChildSequence_Click(object sender, RoutedEventArgs e) => AddSource(GetSourceContext(sender), LayerEditorSourceKind.VideoSequence);

    private LayerEditorSource? GetSourceContext(object sender) =>
        sender is FrameworkElement { DataContext: LayerEditorSource source } ? source : null;

    private void AddSource(LayerEditorSource? parent, LayerEditorSourceKind kind)
    {
        if (_viewModel.LiveMode)
        {
            AddSourceLive(parent, kind);
            RefreshFromSources();
            return;
        }

        var draft = BuildDraftSource(kind);
        if (draft == null)
        {
            return;
        }

        draft.Parent = parent;
        GetChildCollection(parent).Add(draft);
    }

    private void AddSourceLive(LayerEditorSource? parent, LayerEditorSourceKind kind)
    {
        Guid? parentId = parent?.Id;
        switch (kind)
        {
            case LayerEditorSourceKind.Group:
                _owner.AddLayerGroupFromEditor(parentId);
                break;
            case LayerEditorSourceKind.Window:
            {
                var window = PromptForWindow();
                if (window != null)
                {
                    _owner.AddWindowSourceFromEditor(window, parentId);
                }
                break;
            }
            case LayerEditorSourceKind.Webcam:
            {
                var webcam = PromptForWebcam();
                if (webcam.HasValue)
                {
                    _owner.AddWebcamSourceFromEditor(webcam.Value, parentId);
                }
                break;
            }
            case LayerEditorSourceKind.File:
            {
                var path = PromptForFile();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    _owner.AddFileSourceFromEditor(path!, parentId);
                }
                break;
            }
            case LayerEditorSourceKind.Youtube:
            {
                var url = PromptForYoutube();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    _owner.AddYoutubeSourceFromEditor(url!, parentId);
                }
                break;
            }
            case LayerEditorSourceKind.VideoSequence:
            {
                var paths = PromptForSequence();
                if (paths != null && paths.Length > 0)
                {
                    _owner.AddVideoSequenceFromEditor(paths, parentId);
                }
                break;
            }
        }
    }

    private LayerEditorSource? BuildDraftSource(LayerEditorSourceKind kind)
    {
        switch (kind)
        {
            case LayerEditorSourceKind.Group:
                return new LayerEditorSource
                {
                    Id = Guid.NewGuid(),
                    Kind = LayerEditorSourceKind.Group,
                    DisplayName = "Layer Group",
                    BlendMode = "Additive",
                    FitMode = "Fill",
                    Opacity = 1.0,
                    KeyColorHex = "#000000",
                    KeyTolerance = 0.1
                };
            case LayerEditorSourceKind.Window:
            {
                var window = PromptForWindow();
                if (window == null)
                {
                    return null;
                }

                return new LayerEditorSource
                {
                    Id = Guid.NewGuid(),
                    Kind = LayerEditorSourceKind.Window,
                    DisplayName = window.Title,
                    WindowTitle = window.Title,
                    WindowHandle = window.Handle,
                    BlendMode = "Additive",
                    FitMode = "Fill",
                    Opacity = 1.0,
                    KeyColorHex = "#000000",
                    KeyTolerance = 0.1
                };
            }
            case LayerEditorSourceKind.Webcam:
            {
                var camera = PromptForWebcam();
                if (!camera.HasValue)
                {
                    return null;
                }

                return new LayerEditorSource
                {
                    Id = Guid.NewGuid(),
                    Kind = LayerEditorSourceKind.Webcam,
                    DisplayName = camera.Value.Name,
                    WebcamId = camera.Value.Id,
                    BlendMode = "Additive",
                    FitMode = "Fill",
                    Opacity = 1.0,
                    KeyColorHex = "#000000",
                    KeyTolerance = 0.1
                };
            }
            case LayerEditorSourceKind.File:
            {
                var path = PromptForFile();
                if (string.IsNullOrWhiteSpace(path))
                {
                    return null;
                }

                return new LayerEditorSource
                {
                    Id = Guid.NewGuid(),
                    Kind = LayerEditorSourceKind.File,
                    DisplayName = Path.GetFileName(path),
                    FilePath = path,
                    BlendMode = "Additive",
                    FitMode = "Fill",
                    Opacity = 1.0,
                    KeyColorHex = "#000000",
                    KeyTolerance = 0.1
                };
            }
            case LayerEditorSourceKind.Youtube:
            {
                var url = PromptForYoutube();
                if (string.IsNullOrWhiteSpace(url))
                {
                    return null;
                }

                return new LayerEditorSource
                {
                    Id = Guid.NewGuid(),
                    Kind = LayerEditorSourceKind.Youtube,
                    DisplayName = "YouTube Video",
                    FilePath = "youtube:" + url,
                    BlendMode = "Additive",
                    FitMode = "Fill",
                    Opacity = 1.0,
                    KeyColorHex = "#000000",
                    KeyTolerance = 0.1
                };
            }
            case LayerEditorSourceKind.VideoSequence:
            {
                var paths = PromptForSequence();
                if (paths == null || paths.Length == 0)
                {
                    return null;
                }

                var source = new LayerEditorSource
                {
                    Id = Guid.NewGuid(),
                    Kind = LayerEditorSourceKind.VideoSequence,
                    DisplayName = $"Sequence ({paths.Length})",
                    BlendMode = "Additive",
                    FitMode = "Fill",
                    Opacity = 1.0,
                    KeyColorHex = "#000000",
                    KeyTolerance = 0.1
                };
                source.FilePaths.AddRange(paths);
                return source;
            }
        }

        return null;
    }

    private ObservableCollection<LayerEditorSource> GetChildCollection(LayerEditorSource? parent) =>
        parent?.Children ?? _viewModel.Sources;

    private ObservableCollection<LayerEditorSource> GetParentCollection(LayerEditorSource source) =>
        source.Parent?.Children ?? _viewModel.Sources;

    private void MoveSourceToIndex(LayerEditorSource source, int index)
    {
        var list = GetParentCollection(source);
        int currentIndex = list.IndexOf(source);
        if (currentIndex < 0 || currentIndex == index)
        {
            return;
        }

        index = Math.Clamp(index, 0, list.Count - 1);
        list.Move(currentIndex, index);
    }

    private WindowHandleInfo? PromptForWindow()
    {
        var windows = _owner.GetAvailableWindows();
        if (windows.Count == 0)
        {
            MessageBox.Show(this, "No windows detected.", "Window Sources", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var items = windows.Select(window => new SelectionItem(window.Title, window)).ToList();
        var dialog = new SelectionDialog("Select Window Source", items) { Owner = this };
        return dialog.ShowDialog() == true && dialog.SelectedValue is WindowHandleInfo selected ? selected : null;
    }

    private WebcamCaptureService.CameraInfo? PromptForWebcam()
    {
        var webcams = _owner.GetAvailableWebcams();
        if (webcams.Count == 0)
        {
            MessageBox.Show(this, "No webcams detected.", "Webcam Sources", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var items = webcams.Select(camera => new SelectionItem(camera.Name, camera)).ToList();
        var dialog = new SelectionDialog("Select Webcam Source", items) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedValue is WebcamCaptureService.CameraInfo selected)
        {
            return selected;
        }

        return null;
    }

    private string? PromptForFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Image, GIF, or Video",
            Filter = "Media Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.mp4;*.mov;*.wmv;*.avi;*.mkv;*.webm;*.mpg;*.mpeg|Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Video Files|*.mp4;*.mov;*.wmv;*.avi;*.mkv;*.webm;*.mpg;*.mpeg|All Files|*.*",
            Multiselect = false,
            CheckFileExists = true
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private string[]? PromptForSequence()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Video Sequence",
            Filter = "Video Files|*.mp4;*.mov;*.wmv;*.avi;*.mkv;*.webm;*.mpg;*.mpeg|All Files|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        return dialog.ShowDialog(this) == true ? dialog.FileNames : null;
    }

    private string? PromptForYoutube()
    {
        var dialog = new TextInputDialog("Add YouTube Source", "Enter YouTube URL:") { Owner = this };
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText) ? dialog.InputText.Trim() : null;
    }
}
