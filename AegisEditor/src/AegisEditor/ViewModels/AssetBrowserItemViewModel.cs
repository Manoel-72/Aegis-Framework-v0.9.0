using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AegisEditor.ViewModels;

public sealed partial class AssetBrowserItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _editableName = string.Empty;

    [ObservableProperty]
    private bool _isRenaming;

    public required string FullPath { get; init; }

    public required string RelativePath { get; init; }

    public required string Kind { get; init; }

    public bool IsDirectory { get; init; }

    public bool IsSprite => Kind == "Sprite";

    public bool IsBroken { get; init; }

    public string ValidationMessage { get; init; } = string.Empty;

    public Bitmap? Thumbnail { get; init; }

    public string Badge => Kind switch
    {
        "Folder" => "DIR",
        "Sprite" => "IMG",
        "Audio" => "SND",
        "Tilemap" => "MAP",
        "Script" => "LUA",
        "Font" => "TTF",
        _ => "FILE",
    };

    partial void OnNameChanged(string value)
    {
        if (!IsRenaming)
            EditableName = Path.GetFileNameWithoutExtension(value);
    }
}
