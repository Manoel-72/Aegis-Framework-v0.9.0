using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aegis.Resource;

public enum AssetIssueSeverity
{
    Info,
    Warning,
    Error
}

public sealed record AssetIssue(AssetIssueSeverity Severity, string Code, string Message, string? Path = null);

public sealed class AssetValidationReport
{
    private readonly List<AssetIssue> _issues = new();

    public IReadOnlyList<AssetIssue> Issues => _issues;
    public int ErrorCount => _issues.Count(i => i.Severity == AssetIssueSeverity.Error);
    public int WarningCount => _issues.Count(i => i.Severity == AssetIssueSeverity.Warning);
    public bool HasErrors => ErrorCount > 0;

    public void Add(AssetIssueSeverity severity, string code, string message, string? path = null)
        => _issues.Add(new AssetIssue(severity, code, message, path));
}

public static class AssetValidator
{
    private static readonly Regex LuaStringArgRegex = new(
        @"aegis\.(?<fn>asset|playSound|playSoundEx|playSoundAt|playMusic|crossfadeTo|playMusicLooped|loadTilemap|loadAtlas)\s*\(\s*[""'](?<path>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LuaPathFieldRegex = new(
        @"(?:path|file|font)\s*=\s*[""'](?<path>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static AssetValidationReport ValidateProject(string projectDir)
    {
        var report = new AssetValidationReport();
        var root = Path.GetFullPath(projectDir);

        if (!Directory.Exists(root))
        {
            report.Add(AssetIssueSeverity.Error, "project.missing", "Project directory not found.", root);
            return report;
        }

        ValidateProjectShape(root, report);
        ValidateExistingAssets(root, report);
        ValidateLuaReferences(root, report);
        ValidateSceneReferences(root, report);
        ValidateTilemapReferences(root, report);

        return report;
    }

    public static bool IsValidWav(string path, out string message)
    {
        message = "OK";
        if (!File.Exists(path))
        {
            message = "File not found.";
            return false;
        }

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 44)
        {
            message = "WAV file is too small.";
            return false;
        }

        var riff = new string(reader.ReadChars(4));
        _ = reader.ReadUInt32();
        var wave = new string(reader.ReadChars(4));
        if (riff != "RIFF" || wave != "WAVE")
        {
            message = "WAV must start with RIFF/WAVE header.";
            return false;
        }

        var hasFmt = false;
        var hasData = false;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadUInt32();
            var next = stream.Position + chunkSize + (chunkSize % 2);
            if (chunkId == "fmt ") hasFmt = true;
            if (chunkId == "data") hasData = true;
            if (next < stream.Position || next > stream.Length) break;
            stream.Position = next;
        }

        if (!hasFmt || !hasData)
        {
            message = "WAV must contain fmt and data chunks.";
            return false;
        }

        return true;
    }

    private static void ValidateProjectShape(string root, AssetValidationReport report)
    {
        RequireFile(root, "main.lua", report);
        RequireFile(root, "aegis.toml", report);

        var cfg = Path.Combine(root, "aegis.cfg");
        report.Add(
            File.Exists(cfg) ? AssetIssueSeverity.Info : AssetIssueSeverity.Warning,
            File.Exists(cfg) ? "config.present" : "config.missing",
            File.Exists(cfg) ? "aegis.cfg found." : "aegis.cfg not found; it will be created when the game runs.",
            cfg);

        var res = Path.Combine(root, "res");
        report.Add(
            Directory.Exists(res) ? AssetIssueSeverity.Info : AssetIssueSeverity.Warning,
            Directory.Exists(res) ? "res.present" : "res.missing",
            Directory.Exists(res) ? "res/ folder found." : "res/ folder not found.",
            res);
    }

    private static void ValidateExistingAssets(string root, AssetValidationReport report)
    {
        var res = Path.Combine(root, "res");
        if (!Directory.Exists(res)) return;

        foreach (var file in Directory.EnumerateFiles(res, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            switch (ext)
            {
                case ".png":
                    ValidateSignature(file, [0x89, 0x50, 0x4E, 0x47], "asset.png.invalid", "PNG header is invalid.", report);
                    break;
                case ".wav":
                    if (!IsValidWav(file, out var wavMessage))
                        report.Add(AssetIssueSeverity.Error, "asset.wav.invalid", wavMessage, file);
                    break;
                case ".json":
                    ValidateJson(file, report);
                    break;
                case ".ttf":
                case ".otf":
                    ValidateFont(file, report);
                    break;
                case ".ogg":
                case ".mp3":
                case ".svg":
                case ".ico":
                    break;
            }
        }
    }

    private static void ValidateLuaReferences(string root, AssetValidationReport report)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.lua", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in LuaStringArgRegex.Matches(text))
            {
                var fn = match.Groups["fn"].Value;
                var rel = match.Groups["path"].Value;
                ValidateLuaReference(root, fn, rel, file, report);
            }

            foreach (Match match in LuaPathFieldRegex.Matches(text))
            {
                var rel = match.Groups["path"].Value;
                if (LooksLikeAssetPath(rel))
                    ValidateAssetPath(root, rel, file, report);
            }
        }
    }

    private static void ValidateLuaReference(string root, string fn, string rel, string sourceFile, AssetValidationReport report)
    {
        if (!LooksLikeAssetPath(rel))
            return;

        if (fn == "asset")
        {
            ValidateAssetPath(root, rel, sourceFile, report);
            return;
        }

        if (fn.StartsWith("play", StringComparison.OrdinalIgnoreCase) || fn == "crossfadeTo")
        {
            ValidateAssetPath(root, rel.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? rel : $"audio/{rel}", sourceFile, report);
            return;
        }

        if (fn == "loadTilemap")
        {
            ValidateAssetPath(root, rel.StartsWith("tilemaps/", StringComparison.OrdinalIgnoreCase) ? rel : $"tilemaps/{rel}", sourceFile, report);
            return;
        }

        if (fn == "loadAtlas")
        {
            ValidateAssetPath(root, rel, sourceFile, report);
        }
    }

    private static void ValidateTilemapReferences(string root, AssetValidationReport report)
    {
        var tilemapRoot = Path.Combine(root, "res", "tilemaps");
        if (!Directory.Exists(tilemapRoot)) return;

        foreach (var file in Directory.EnumerateFiles(tilemapRoot, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var rootEl = doc.RootElement;
                if (!rootEl.TryGetProperty("tilesets", out var tilesets) || tilesets.ValueKind != JsonValueKind.Array) continue;

                foreach (var tileset in tilesets.EnumerateArray())
                {
                    if (tileset.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.String)
                    {
                        var imagePath = image.GetString();
                        if (!string.IsNullOrWhiteSpace(imagePath))
                            ValidateAssetPath(root, imagePath, file, report, Path.GetDirectoryName(file));
                    }
                    else if (tileset.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String)
                    {
                        report.Add(AssetIssueSeverity.Warning, "tilemap.tsx.external", "External TSX tilesets are not fully supported yet.", file);
                    }
                }
            }
            catch
            {
                // JSON validity is reported by ValidateExistingAssets.
            }
        }
    }

    private static void ValidateSceneReferences(string root, AssetValidationReport report)
    {
        var scenesRoot = Path.Combine(root, "scenes");
        if (!Directory.Exists(scenesRoot)) return;

        foreach (var file in Directory.EnumerateFiles(scenesRoot, "*.scene.json", SearchOption.AllDirectories))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var scene = doc.RootElement;

                if (scene.TryGetProperty("entities", out var entities) && entities.ValueKind == JsonValueKind.Array)
                {
                    var sceneTargets = CollectSceneTargets(entities);
                    foreach (var entity in entities.EnumerateArray())
                        ValidateSceneEntity(root, file, entity, report, sceneTargets);
                }

                if (scene.TryGetProperty("tilemaps", out var tilemaps) && tilemaps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tilemap in tilemaps.EnumerateArray())
                    {
                        if (tilemap.ValueKind == JsonValueKind.String)
                            ValidateAssetPath(root, tilemap.GetString() ?? string.Empty, file, report);
                        else if (tilemap.ValueKind == JsonValueKind.Object && TryGetString(tilemap, "path", out var path))
                            ValidateAssetPath(root, path, file, report);
                    }
                }
            }
            catch
            {
                // JSON validity is reported by ValidateExistingAssets.
            }
        }
    }

    private static void ValidateSceneEntity(
        string root,
        string sceneFile,
        JsonElement entity,
        AssetValidationReport report,
        IReadOnlySet<string> sceneTargets)
    {
        if (TryGetString(entity, "texturePath", out var texturePath))
            ValidateAssetPath(root, texturePath, sceneFile, report);

        if (TryGetString(entity, "scriptPath", out var scriptPath))
            ValidateProjectPath(root, scriptPath, sceneFile, report);

        if (!entity.TryGetProperty("components", out var components))
            return;

        if (components.ValueKind == JsonValueKind.Object)
        {
            foreach (var component in components.EnumerateObject())
                ValidateSceneComponent(root, sceneFile, component.Name, component.Value, report, sceneTargets);
        }
        else if (components.ValueKind == JsonValueKind.Array)
        {
            foreach (var component in components.EnumerateArray())
            {
                if (component.ValueKind != JsonValueKind.Object || !TryGetString(component, "type", out var type))
                    continue;

                if (component.TryGetProperty("properties", out var props))
                    ValidateSceneComponent(root, sceneFile, type, props, report, sceneTargets);
            }
        }
    }

    private static void ValidateSceneComponent(
        string root,
        string sceneFile,
        string type,
        JsonElement props,
        AssetValidationReport report,
        IReadOnlySet<string> sceneTargets)
    {
        if (props.ValueKind != JsonValueKind.Object)
            return;

        if (type.Equals("SpriteRenderer", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetString(props, "sprite", out var sprite)
                || TryGetString(props, "texture", out sprite)
                || TryGetString(props, "texturePath", out sprite))
                ValidateAssetPath(root, sprite, sceneFile, report);
        }
        else if (type.Equals("Script", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetString(props, "file", out var script) || TryGetString(props, "path", out script))
                ValidateProjectPath(root, script, sceneFile, report);
        }
        else if (type.Equals("AudioSource", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetString(props, "clip", out var clip) || TryGetString(props, "file", out clip) || TryGetString(props, "path", out clip))
                ValidateAssetPath(root, clip.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? clip : $"audio/{clip}", sceneFile, report);
        }
        else if (type.Equals("Camera", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetString(props, "follow_target", out var target) || TryGetString(props, "followTarget", out target))
            {
                if (!sceneTargets.Contains(target.Trim()))
                    report.Add(AssetIssueSeverity.Warning, "scene.camera.follow.missing", $"Camera follow target not found in scene by id/name: {target}", sceneFile);
            }
        }
        else if (type.Equals("Collider2D", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetString(props, "shape", out var shape) && shape.Equals("polygon", StringComparison.OrdinalIgnoreCase))
                report.Add(AssetIssueSeverity.Warning, "scene.collider.polygon.experimental", "Polygon Collider2D is saved by the editor but not fully supported by runtime yet.", sceneFile);
        }
        else if (type.Equals("TagLayer", StringComparison.OrdinalIgnoreCase))
        {
            report.Add(AssetIssueSeverity.Info, "scene.taglayer.saved", "TagLayer is saved for editor/scripts; project tag/layer registry is not enforced yet.", sceneFile);
        }
    }

    private static HashSet<string> CollectSceneTargets(JsonElement entities)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities.EnumerateArray())
        {
            if (TryGetString(entity, "id", out var id))
                targets.Add(id.Trim());
            if (TryGetString(entity, "name", out var name))
                targets.Add(name.Trim());
        }

        return targets;
    }

    private static void ValidateAssetPath(string root, string rel, string sourceFile, AssetValidationReport report, string? baseDir = null)
    {
        if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel)) return;
        var normalized = rel.Replace('\\', '/').TrimStart('/');
        var full = ResolveAssetPath(root, normalized, baseDir);
        if (!File.Exists(full))
            report.Add(AssetIssueSeverity.Error, "asset.reference.missing", $"Referenced asset not found: {normalized}", $"{sourceFile} -> {full}");
    }

    private static void ValidateProjectPath(string root, string rel, string sourceFile, AssetValidationReport report)
    {
        if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel)) return;
        var normalized = rel.Replace('\\', '/').TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(root, normalized));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return;
        if (!File.Exists(full))
            report.Add(AssetIssueSeverity.Error, "asset.reference.missing", $"Referenced file not found: {normalized}", $"{sourceFile} -> {full}");
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;

        value = prop.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string ResolveAssetPath(string root, string rel, string? baseDir)
    {
        if (baseDir is not null)
        {
            var fromBase = Path.GetFullPath(Path.Combine(baseDir, rel));
            if (fromBase.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return fromBase;
        }

        var clean = rel.StartsWith("res/", StringComparison.OrdinalIgnoreCase) ? rel[4..] : rel;
        return Path.GetFullPath(Path.Combine(root, "res", clean));
    }

    private static bool LooksLikeAssetPath(string rel)
    {
        var ext = Path.GetExtension(rel).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".wav" or ".ogg" or ".mp3" or ".ttf" or ".otf" or ".json";
    }

    private static void RequireFile(string root, string rel, AssetValidationReport report)
    {
        var path = Path.Combine(root, rel);
        report.Add(
            File.Exists(path) ? AssetIssueSeverity.Info : AssetIssueSeverity.Error,
            File.Exists(path) ? $"{rel}.present" : $"{rel}.missing",
            File.Exists(path) ? $"{rel} found." : $"{rel} not found.",
            path);
    }

    private static void ValidateSignature(string file, byte[] expected, string code, string message, AssetValidationReport report)
    {
        using var stream = File.OpenRead(file);
        if (stream.Length < expected.Length)
        {
            report.Add(AssetIssueSeverity.Error, code, message, file);
            return;
        }

        Span<byte> actual = stackalloc byte[expected.Length];
        _ = stream.Read(actual);
        for (var i = 0; i < expected.Length; i++)
        {
            if (actual[i] != expected[i])
            {
                report.Add(AssetIssueSeverity.Error, code, message, file);
                return;
            }
        }
    }

    private static void ValidateJson(string file, AssetValidationReport report)
    {
        try
        {
            using var _ = JsonDocument.Parse(File.ReadAllText(file));
        }
        catch (Exception ex)
        {
            report.Add(AssetIssueSeverity.Error, "asset.json.invalid", $"Invalid JSON: {ex.Message}", file);
        }
    }

    private static void ValidateFont(string file, AssetValidationReport report)
    {
        using var stream = File.OpenRead(file);
        Span<byte> header = stackalloc byte[4];
        if (stream.Length < 4 || stream.Read(header) != 4)
        {
            report.Add(AssetIssueSeverity.Error, "asset.font.invalid", "Font file is too small.", file);
            return;
        }

        var isTrueType = header[0] == 0x00 && header[1] == 0x01 && header[2] == 0x00 && header[3] == 0x00;
        var isOpenType = header[0] == 'O' && header[1] == 'T' && header[2] == 'T' && header[3] == 'O';
        if (!isTrueType && !isOpenType)
            report.Add(AssetIssueSeverity.Warning, "asset.font.unknown", "Font header is not a standard TTF/OTF header.", file);
    }
}
