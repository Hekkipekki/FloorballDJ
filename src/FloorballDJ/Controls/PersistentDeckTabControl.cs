using System.Windows.Controls;
using System.Windows.Data;

namespace FloorballDJ.Controls;

// Random settings uses one explicit content template for every deck. Keep that
// template on the native host when a source/group reset briefly clears selection.
// SelectedContent still changes normally, so WPF rebinds the existing view.
// Headers, selection, keyboard navigation and the theme template stay WPF-owned.
public sealed class PersistentDeckTabControl : TabControl
{
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        RetainSelectedTemplate();
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        RetainSelectedTemplate();
    }

    private void RetainSelectedTemplate()
    {
        // Leave the initial preparation shell empty, as with a native TabControl.
        // Start retaining only once a real deck has reached the content host.
        if (SelectedContent is not null && GetTemplateChild("PART_SelectedContentHost") is ContentPresenter host &&
            !ReferenceEquals(BindingOperations.GetBinding(host, ContentPresenter.ContentTemplateProperty)?.Source, this))
            host.SetBinding(ContentPresenter.ContentTemplateProperty,
                new Binding(nameof(ContentTemplate)) { Source = this });
    }
}
