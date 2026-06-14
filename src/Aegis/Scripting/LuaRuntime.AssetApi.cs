using Aegis.Resource;
using NLua;

namespace Aegis.Scripting;

public sealed partial class LuaRuntime
{
    private int _assetReportSeq;

    public string Asset(string path)
    {
        var normalized = NormalizeAssetPath(path);
        var full = ResolveAssetFullPath(normalized);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException(
                "[Aegis|Asset] Asset nao encontrado.\n" +
                $"  Pedido: {path}\n" +
                $"  Normalizado: {normalized}\n" +
                $"  Esperado: {full}\n" +
                "  Dica: coloque o arquivo dentro da pasta res/ do projeto e use caminho relativo, exemplo: sprites/player.png");
        }

        return normalized;
    }

    public bool AssetExists(string path)
    {
        try
        {
            return File.Exists(ResolveAssetFullPath(NormalizeAssetPath(path)));
        }
        catch
        {
            return false;
        }
    }

    public bool ValidateAssets()
    {
        var report = AssetValidator.ValidateProject(_gameRoot);
        LogAssetReport(report);
        return !report.HasErrors;
    }

    public LuaTable AssetReport()
    {
        var report = AssetValidator.ValidateProject(_gameRoot);
        var prefix = $"_aegis_asset_report_{++_assetReportSeq}";
        _lua.NewTable(prefix);
        var table = (LuaTable)_lua[prefix];
        table["ok"] = !report.HasErrors;
        table["errors"] = report.ErrorCount;
        table["warnings"] = report.WarningCount;

        _lua.NewTable($"{prefix}_issues");
        var issues = (LuaTable)_lua[$"{prefix}_issues"];
        var index = 1;
        foreach (var issue in report.Issues)
        {
            var itemName = $"{prefix}_issue_{index}";
            _lua.NewTable(itemName);
            var item = (LuaTable)_lua[itemName];
            item["severity"] = issue.Severity.ToString().ToLowerInvariant();
            item["code"] = issue.Code;
            item["message"] = issue.Message;
            item["path"] = issue.Path ?? string.Empty;
            issues[index++] = item;
        }

        table["issues"] = issues;
        return table;
    }

    private static void LogAssetReport(AssetValidationReport report)
    {
        foreach (var issue in report.Issues)
        {
            var message = string.IsNullOrWhiteSpace(issue.Path)
                ? $"{issue.Code}: {issue.Message}"
                : $"{issue.Code}: {issue.Message} ({issue.Path})";

            if (issue.Severity == AssetIssueSeverity.Error)
                Aegis.Core.AegisLog.Error("Asset", message);
            else if (issue.Severity == AssetIssueSeverity.Warning)
                Aegis.Core.AegisLog.Warn("Asset", message);
        }
    }

    private string ResolveAssetFullPath(string normalized)
    {
        var root = Path.GetFullPath(Path.Combine(_gameRoot, "res"));
        var clean = normalized.StartsWith("res/", StringComparison.OrdinalIgnoreCase) ? normalized[4..] : normalized;
        var full = Path.GetFullPath(Path.Combine(root, clean));

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"[Aegis|Asset] Caminho fora da pasta res/: '{normalized}'");

        return full;
    }

    private static string NormalizeAssetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("[Aegis|Asset] Caminho de asset vazio.", nameof(path));

        if (Path.IsPathRooted(path))
            throw new InvalidOperationException($"[Aegis|Asset] Use caminho relativo dentro de res/: '{path}'");

        return path.Replace('\\', '/').TrimStart('/');
    }
}
