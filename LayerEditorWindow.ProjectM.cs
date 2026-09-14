using System;
using System.Windows;
using System.Windows.Controls;

namespace lifeviz;

public partial class LayerEditorWindow
{
    private void AddRootProjectM_Click(object sender, RoutedEventArgs e) => AddSource(null, LayerEditorSourceKind.ProjectM);
    private void AddChildProjectM_Click(object sender, RoutedEventArgs e)
    {
        var parent = ResolveSelectedGroup(sender);
        if (parent != null) AddSource(parent, LayerEditorSourceKind.ProjectM);
    }

    private void ProjectMSettings_Click(object sender, RoutedEventArgs e)
    {
        var source = ResolveSourceContext(sender);
        if (source?.IsProjectM != true) return;
        bool live = ShouldApplyLive();
        var dialog = new ProjectMSettingsWindow(source.ProjectM,
            live ? () => _owner.GetProjectMStatus(source.Id) : null,
            live ? action => _owner.ControlProjectM(source.Id, action) : null,
            _owner.CopyProjectMPreviewAudio) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        source.ProjectM = dialog.Result.Clone();
        if (live) _owner.UpdateProjectMFromEditor(source.Id, source.ProjectM);
    }

    private void ProjectMControl_Click(object sender, RoutedEventArgs e)
    {
        var source = ResolveSourceContext(sender);
        if (source?.IsProjectM == true && ShouldApplyLive() && sender is Button { Tag: string text } && int.TryParse(text, out int direction))
            _owner.ControlProjectM(source.Id, direction);
    }
}
