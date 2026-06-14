using System.Collections.ObjectModel;
using System.Text.Json;
using Aegis.Resource;
using AegisEditor.Services;
using AegisEditor.Shared.Models;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AegisEditor.ViewModels;

public sealed partial class AssetBrowserViewModel(IAssetBrowserService assets, IEditorLogSink log) : ObservableObject
{
    private string _projectRoot = string.Empty;
    private string _resRoot = string.Empty;

    public ObservableCollection<AssetBrowserItemViewModel> Entries { get; } = [];

    public ObservableCollection<AssetProblemViewModel> Problems { get; } = [];

    public event EventHandler<SceneEntityDto>? SpriteCreateRequested;

    [ObservableProperty]
    private string _currentFolder = string.Empty;

    [ObservableProperty]
    private AssetBrowserItemViewModel? _selectedAsset;

    [ObservableProperty]
    private string _status = "Abra um projeto para listar assets.";

    [ObservableProperty]
    private string _selectedCategory = "All";

    partial void OnSelectedAssetChanged(AssetBrowserItemViewModel? value)
    {
        if (value is not null && !value.IsRenaming)
            value.EditableName = value.IsDirectory ? value.Name : Path.GetFileNameWithoutExtension(value.Name);
    }

    public async Task OpenProjectAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _resRoot = Path.Combine(_projectRoot, "res");
        await OpenFolderAsync(_resRoot, cancellationToken).ConfigureAwait(true);
        ValidateProject();
    }

    [RelayCommand]
    private async Task OpenSelectedAsync(CancellationToken cancellationToken)
    {
        if (SelectedAsset is null) return;

        if (SelectedAsset.IsDirectory)
            await OpenFolderAsync(SelectedAsset.FullPath, cancellationToken).ConfigureAwait(true);
        else if (SelectedAsset.IsSprite)
            CreateSpriteFromSelected();
    }

    [RelayCommand]
    private async Task UpAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(CurrentFolder)) return;
        var parent = Directory.GetParent(CurrentFolder);
        if (parent is null) return;

        var parentFull = Path.GetFullPath(parent.FullName);
        if (!parentFull.StartsWith(Path.GetFullPath(_resRoot), StringComparison.OrdinalIgnoreCase))
            return;

        await OpenFolderAsync(parentFull, cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CreateSpriteFromSelected()
    {
        if (SelectedAsset is null || SelectedAsset.IsDirectory || !SelectedAsset.IsSprite)
        {
            Status = "Selecione uma imagem PNG/JPG para criar Sprite.";
            return;
        }

        SpriteCreateRequested?.Invoke(this, CreateSpriteEntity(SelectedAsset.RelativePath, 160, 160));
        Status = $"Sprite criado: {SelectedAsset.RelativePath}";
    }

    [RelayCommand]
    private async Task CreateScriptAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_projectRoot))
        {
            Status = "Abra um projeto antes de criar scripts.";
            return;
        }

        var scriptsRoot = Path.Combine(_projectRoot, "scripts");
        Directory.CreateDirectory(scriptsRoot);

        var name = "new_script.lua";
        var fullPath = Path.Combine(scriptsRoot, name);
        var index = 1;
        while (File.Exists(fullPath))
        {
            name = $"new_script_{index++}.lua";
            fullPath = Path.Combine(scriptsRoot, name);
        }

        await File.WriteAllTextAsync(fullPath, """
local M = {}

function M.init(self)
end

function M.update(self, dt)
end

return M
""", cancellationToken).ConfigureAwait(true);

        await OpenScriptsAsync(cancellationToken).ConfigureAwait(true);
        SelectedAsset = Entries.FirstOrDefault(e => e.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
        if (SelectedAsset is not null)
        {
            SelectedAsset.EditableName = Path.GetFileNameWithoutExtension(name);
            SelectedAsset.IsRenaming = true;
        }
        Status = $"Script criado: scripts/{name}";
        log.Post(EditorLogLevel.Info, $"[ASSET] Script criado: scripts/{name}");
    }

    [RelayCommand]
    private void RenameSelected()
        => StartRenameSelected();

    public void StartRenameSelected()
    {
        if (SelectedAsset is null || SelectedAsset.IsDirectory)
        {
            Status = "Selecione um arquivo para renomear.";
            return;
        }

        SelectedAsset.EditableName = Path.GetFileNameWithoutExtension(SelectedAsset.Name);
        SelectedAsset.IsRenaming = true;
        Status = "Digite o novo nome direto no item e pressione Enter.";
    }

    public void CancelRename(AssetBrowserItemViewModel? item = null)
    {
        var target = item ?? SelectedAsset;
        if (target is null || !target.IsRenaming)
            return;

        target.EditableName = target.IsDirectory ? target.Name : Path.GetFileNameWithoutExtension(target.Name);
        target.IsRenaming = false;
        Status = "Renomear cancelado.";
    }

    public async Task CommitRenameAsync(AssetBrowserItemViewModel? item, CancellationToken cancellationToken = default)
    {
        if (item is null || item.IsDirectory || !item.IsRenaming)
        {
            return;
        }

        var oldPath = item.FullPath;
        var dir = Path.GetDirectoryName(oldPath);
        if (dir is null) return;

        var ext = Path.GetExtension(oldPath);
        var requested = SanitizeFileName(item.EditableName, Path.GetFileNameWithoutExtension(oldPath));
        var newName = EnsureExtension(requested, ext);
        var newPath = Path.Combine(dir, newName);

        if (newPath.Equals(oldPath, StringComparison.OrdinalIgnoreCase))
        {
            item.IsRenaming = false;
            item.EditableName = Path.GetFileNameWithoutExtension(item.Name);
            Status = "Nome mantido.";
            return;
        }

        if (File.Exists(newPath))
        {
            Status = $"Ja existe um arquivo com esse nome: {newName}";
            return;
        }

        File.Move(oldPath, newPath);
        await OpenFolderAsync(dir, cancellationToken).ConfigureAwait(true);
        SelectedAsset = Entries.FirstOrDefault(e => e.FullPath.Equals(newPath, StringComparison.OrdinalIgnoreCase));
        Status = $"Renomeado: {newName}";
        log.Post(EditorLogLevel.Info, $"[ASSET] Arquivo renomeado: {newName}");
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync(CancellationToken cancellationToken)
    {
        if (SelectedAsset is null || SelectedAsset.IsDirectory)
        {
            Status = "Selecione um arquivo para deletar.";
            return;
        }

        var path = SelectedAsset.FullPath;
        var dir = Path.GetDirectoryName(path);
        if (dir is null) return;

        File.Delete(path);
        await OpenFolderAsync(dir, cancellationToken).ConfigureAwait(true);
        Status = $"Arquivo deletado: {Path.GetFileName(path)}";
        log.Post(EditorLogLevel.Warning, $"[ASSET] Arquivo deletado: {path}");
    }

    public SceneEntityDto CreateSpriteEntity(string texturePath, float x, float y)
    {
        var safeName = Path.GetFileNameWithoutExtension(texturePath.Replace('\\', '/'));
        return new SceneEntityDto
        {
            Id = "sprite-" + Guid.NewGuid().ToString("N")[..8],
            Name = string.IsNullOrWhiteSpace(safeName) ? "Sprite" : safeName,
            Type = "Sprite",
            X = x,
            Y = y,
            ScaleX = 1,
            ScaleY = 1,
            TexturePath = texturePath.Replace('\\', '/'),
            Components =
            [
                new ComponentDto
                {
                    Type = "Transform",
                    Properties =
                    {
                        ["position"] = JsonSerializer.SerializeToElement(new[] { x, y }),
                        ["rotation"] = JsonSerializer.SerializeToElement(0f),
                        ["scale"] = JsonSerializer.SerializeToElement(new[] { 1f, 1f }),
                    }
                },
                new ComponentDto
                {
                    Type = "SpriteRenderer",
                    Properties =
                    {
                        ["sprite"] = JsonSerializer.SerializeToElement(texturePath.Replace('\\', '/')),
                        ["color"] = JsonSerializer.SerializeToElement(new[] { 1f, 1f, 1f, 1f }),
                        ["flip_x"] = JsonSerializer.SerializeToElement(false),
                    }
                }
            ]
        };
    }

    [RelayCommand]
    private void ValidateProject()
    {
        Problems.Clear();
        if (string.IsNullOrWhiteSpace(_projectRoot) || !Directory.Exists(_projectRoot))
        {
            Status = "Abra um projeto antes de validar assets.";
            return;
        }

        var report = AssetValidator.ValidateProject(_projectRoot);
        foreach (var issue in report.Issues.Where(i => i.Severity != AssetIssueSeverity.Info))
        {
            var level = issue.Severity == AssetIssueSeverity.Error ? EditorLogLevel.Error : EditorLogLevel.Warning;
            log.Post(level, $"[ASSET] {issue.Message}" + (string.IsNullOrWhiteSpace(issue.Path) ? string.Empty : $" | {issue.Path}"));
            Problems.Add(new AssetProblemViewModel
            {
                Severity = issue.Severity.ToString().ToUpperInvariant(),
                Message = issue.Message,
                Path = issue.Path ?? string.Empty
            });
        }

        if (Problems.Count == 0)
            log.Post(EditorLogLevel.Info, "[ASSET] Assets validados: nenhum problema encontrado.");
        else
            log.Post(EditorLogLevel.Warning, $"[ASSET] Assets validados: {report.ErrorCount} erro(s), {report.WarningCount} aviso(s).");
    }

    private async Task OpenFolderAsync(string folder, CancellationToken cancellationToken)
    {
        CurrentFolder = Path.GetFullPath(folder);
        Entries.Clear();

        var list = await assets.ListAsync(CurrentFolder, cancellationToken).ConfigureAwait(true);
        foreach (var entry in list)
            Entries.Add(CreateItem(entry));

        var broken = Entries.Count(e => e.IsBroken);
        Status = Entries.Count == 0
            ? "Pasta vazia."
            : broken == 0
                ? $"{Entries.Count} item(ns)"
                : $"{Entries.Count} item(ns), {broken} com problema";
    }

    [RelayCommand]
    private async Task OpenAllAsync(CancellationToken cancellationToken)
        => await OpenCategoryAsync("All", _resRoot, cancellationToken).ConfigureAwait(true);

    [RelayCommand]
    private async Task OpenSpritesAsync(CancellationToken cancellationToken)
        => await OpenCategoryAsync("Sprites", Path.Combine(_resRoot, "sprites"), cancellationToken).ConfigureAwait(true);

    [RelayCommand]
    private async Task OpenAudioAsync(CancellationToken cancellationToken)
        => await OpenCategoryAsync("Audio", Path.Combine(_resRoot, "audio"), cancellationToken).ConfigureAwait(true);

    [RelayCommand]
    private async Task OpenTilemapsAsync(CancellationToken cancellationToken)
        => await OpenCategoryAsync("Tilemaps", Path.Combine(_resRoot, "tilemaps"), cancellationToken).ConfigureAwait(true);

    [RelayCommand]
    private async Task OpenScriptsAsync(CancellationToken cancellationToken)
        => await OpenCategoryAsync("Scripts", Path.Combine(_projectRoot, "scripts"), cancellationToken).ConfigureAwait(true);

    [RelayCommand]
    private async Task OpenFontsAsync(CancellationToken cancellationToken)
        => await OpenCategoryAsync("Fonts", Path.Combine(_resRoot, "fonts"), cancellationToken).ConfigureAwait(true);

    private async Task OpenCategoryAsync(string category, string folder, CancellationToken cancellationToken)
    {
        SelectedCategory = category;
        Directory.CreateDirectory(folder);
        await OpenFolderAsync(folder, cancellationToken).ConfigureAwait(true);
    }

    private AssetBrowserItemViewModel CreateItem(AssetEntry entry)
    {
        var rel = MakeRelativePath(entry.FullPath);
        var kind = Classify(entry);
        var validation = Validate(entry, kind);

        return new AssetBrowserItemViewModel
        {
            Name = entry.Name,
            EditableName = entry.IsDirectory ? entry.Name : Path.GetFileNameWithoutExtension(entry.Name),
            FullPath = entry.FullPath,
            RelativePath = rel,
            Kind = kind,
            IsDirectory = entry.IsDirectory,
            IsBroken = validation.Length > 0,
            ValidationMessage = validation,
            Thumbnail = kind == "Sprite" && validation.Length == 0 ? TryCreateThumbnail(entry.FullPath) : null
        };
    }

    private string MakeRelativePath(string fullPath)
    {
        if (!string.IsNullOrWhiteSpace(_resRoot) && fullPath.StartsWith(Path.GetFullPath(_resRoot), StringComparison.OrdinalIgnoreCase))
            return Path.GetRelativePath(_resRoot, fullPath).Replace('\\', '/');

        if (!string.IsNullOrWhiteSpace(_projectRoot) && fullPath.StartsWith(Path.GetFullPath(_projectRoot), StringComparison.OrdinalIgnoreCase))
            return Path.GetRelativePath(_projectRoot, fullPath).Replace('\\', '/');

        return Path.GetFileName(fullPath);
    }

    private static string Classify(AssetEntry entry)
    {
        if (entry.IsDirectory) return "Folder";
        var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" => "Sprite",
            ".wav" or ".ogg" or ".mp3" => "Audio",
            ".json" or ".tmx" => "Tilemap",
            ".lua" => "Script",
            ".ttf" or ".otf" => "Font",
            _ => "File",
        };
    }

    private static string Validate(AssetEntry entry, string kind)
    {
        if (entry.IsDirectory) return string.Empty;
        if (!File.Exists(entry.FullPath)) return "Arquivo ausente";

        try
        {
            return kind switch
            {
                "Sprite" => ValidateSprite(entry.FullPath),
                "Audio" => ValidateAudio(entry.FullPath),
                "Tilemap" => ValidateTilemap(entry.FullPath),
                "Script" => ValidateScript(entry.FullPath),
                "Font" => ValidateFont(entry.FullPath),
                _ => string.Empty,
            };
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string ValidateSprite(string fullPath)
    {
        using var stream = File.OpenRead(fullPath);
        using var bitmap = new Bitmap(stream);
        return bitmap.Size.Width <= 0 || bitmap.Size.Height <= 0 ? "Imagem vazia" : string.Empty;
    }

    private static string ValidateAudio(string fullPath)
    {
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (ext != ".wav") return string.Empty;

        Span<byte> header = stackalloc byte[12];
        using var stream = File.OpenRead(fullPath);
        if (stream.Read(header) < header.Length) return "WAV incompleto";
        var riff = System.Text.Encoding.ASCII.GetString(header[..4]);
        var wave = System.Text.Encoding.ASCII.GetString(header[8..12]);
        return riff == "RIFF" && wave == "WAVE" ? string.Empty : "WAV invalido";
    }

    private static string ValidateTilemap(string fullPath)
    {
        if (!Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        using var stream = File.OpenRead(fullPath);
        using var _ = System.Text.Json.JsonDocument.Parse(stream);
        return string.Empty;
    }

    private static string ValidateScript(string fullPath)
        => new FileInfo(fullPath).Length == 0 ? "Script vazio" : string.Empty;

    private static string ValidateFont(string fullPath)
        => new FileInfo(fullPath).Length < 4 ? "Fonte invalida" : string.Empty;

    private static Bitmap? TryCreateThumbnail(string fullPath)
    {
        try
        {
            using var stream = File.OpenRead(fullPath);
            return Bitmap.DecodeToWidth(stream, 48);
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeFileName(string? value, string fallback)
    {
        var name = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        name = name.Replace('\\', '_').Replace('/', '_').Trim();
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private static string EnsureExtension(string name, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return name;

        return Path.HasExtension(name)
            ? name
            : name + extension;
    }
}
