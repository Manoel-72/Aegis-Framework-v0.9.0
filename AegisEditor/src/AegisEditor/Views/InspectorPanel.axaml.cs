using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AegisEditor.ViewModels;

namespace AegisEditor.Views;

#pragma warning disable CS0618
public partial class InspectorPanel : UserControl
{
    public InspectorPanel()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(InputElement.GotFocusEvent, OnEditorFieldGotFocus, RoutingStrategies.Tunnel);
        AddHandler(InputElement.LostFocusEvent, OnEditorFieldLostFocus, RoutingStrategies.Bubble);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnEditorFieldGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (e.Source is TextBox { IsReadOnly: false } && DataContext is InspectorViewModel inspector)
            inspector.BeginEdit();
    }

    private void OnEditorFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox { IsReadOnly: false } && DataContext is InspectorViewModel inspector)
            inspector.CommitEdit();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(AssetBrowserPanel.DragScriptPathFormat)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not InspectorViewModel inspector)
            return;

        var raw = e.Data.Contains(AssetBrowserPanel.DragScriptPathFormat)
            ? e.Data.Get(AssetBrowserPanel.DragScriptPathFormat)?.ToString()
            : null;

        if (!string.IsNullOrWhiteSpace(raw))
        {
            inspector.AssignScript(raw);
            e.Handled = true;
        }
    }
}
#pragma warning restore CS0618
