using AegisEditor.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace AegisEditor.Views;

#pragma warning disable CS0618 // Avalonia 11 keeps IDataObject working; DataTransfer migration is isolated here.
public sealed partial class AssetBrowserPanel : UserControl
{
    public const string DragTexturePathFormat = "Aegis.TexturePath";
    public const string DragScriptPathFormat = "Aegis.ScriptPath";
    private bool _suppressNextRenameLostFocus;
    private bool _renameHandledByKeyboard;

    public AssetBrowserPanel()
    {
        InitializeComponent();
    }

    private async void AssetList_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is AssetBrowserViewModel vm && vm.OpenSelectedCommand.CanExecute(null))
            await vm.OpenSelectedCommand.ExecuteAsync(null);
    }

    private async void AssetList_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not AssetBrowserViewModel vm) return;
        await TryStartAssetDragAsync(e, vm.SelectedAsset);
    }

    private void AssetList_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not AssetBrowserViewModel vm) return;

        if (e.Key == Key.F2)
        {
            vm.StartRenameSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelRename();
            e.Handled = true;
        }
    }

    private async void AssetItem_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not StyledElement { DataContext: AssetBrowserItemViewModel item }) return;
        if (DataContext is AssetBrowserViewModel vm)
            vm.SelectedAsset = item;

        await TryStartAssetDragAsync(e, item);
    }

    private async Task TryStartAssetDragAsync(PointerPressedEventArgs e, AssetBrowserItemViewModel? asset)
    {
        if (asset is not { IsBroken: false }) return;
        if (!asset.IsSprite && !asset.Kind.Equals("Script", StringComparison.OrdinalIgnoreCase)) return;
        if (asset.IsRenaming) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var data = new DataObject();
        data.Set(asset.IsSprite ? DragTexturePathFormat : DragScriptPathFormat, asset.RelativePath);
        data.Set(DataFormats.Text, asset.RelativePath);

        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
    }

    private async void AssetRenameBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _renameHandledByKeyboard = true;
            _suppressNextRenameLostFocus = true;
            await CommitInlineRenameAsync(sender);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (sender is StyledElement { DataContext: AssetBrowserItemViewModel item }
                && DataContext is AssetBrowserViewModel vm)
            {
                _suppressNextRenameLostFocus = true;
                _renameHandledByKeyboard = true;
                vm.CancelRename(item);
            }
            e.Handled = true;
        }
    }

    private async void AssetRenameBox_OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_renameHandledByKeyboard)
        {
            _renameHandledByKeyboard = true;
            _suppressNextRenameLostFocus = true;
            await CommitInlineRenameAsync(sender);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && !_renameHandledByKeyboard)
        {
            if (sender is StyledElement { DataContext: AssetBrowserItemViewModel item }
                && DataContext is AssetBrowserViewModel vm)
            {
                _renameHandledByKeyboard = true;
                _suppressNextRenameLostFocus = true;
                vm.CancelRename(item);
            }
            e.Handled = true;
        }
    }

    private async void AssetRenameBox_OnLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_suppressNextRenameLostFocus)
        {
            _suppressNextRenameLostFocus = false;
            _renameHandledByKeyboard = false;
            return;
        }

        await CommitInlineRenameAsync(sender);
        _renameHandledByKeyboard = false;
    }

    private async Task CommitInlineRenameAsync(object? sender)
    {
        if (sender is not StyledElement { DataContext: AssetBrowserItemViewModel item }) return;
        if (DataContext is not AssetBrowserViewModel vm) return;
        if (sender is TextBox textBox)
            item.EditableName = textBox.Text ?? string.Empty;

        await vm.CommitRenameAsync(item);
    }
}
#pragma warning restore CS0618
