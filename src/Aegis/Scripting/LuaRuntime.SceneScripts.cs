using System.Text.Json;
using Aegis.Core;
using Aegis.Scene;
using AegisEditor.Shared.Models;
using NLua;

namespace Aegis.Scripting;

public sealed partial class LuaRuntime
{
    private readonly List<SceneScriptInstance> _sceneScripts = new();
    private int _sceneScriptSeq;

    internal void RegisterSceneScript(Object2D obj, SceneEntityDto entity)
    {
        var script = SceneComponentJson.Get(entity, "Script");
        var file = SceneComponentJson.String(script, "file", "path");
        if (script is null || string.IsNullOrWhiteSpace(file))
            return;

        var full = ResolveProjectFile(file);
        if (!File.Exists(full))
        {
            AegisLog.Warn("Scene", $"Script component de '{entity.Name}' nao encontrou arquivo: {file}");
            return;
        }

        try
        {
            var result = DoFileChecked(full, $"load entity script '{entity.Name}'");
            var module = result.FirstOrDefault() as LuaTable;
            if (module is null)
            {
                AegisLog.Warn("Scene", $"Script '{file}' deve retornar uma tabela Lua.");
                return;
            }

            var init = module["init"] as LuaFunction;
            var update = module["update"] as LuaFunction;
            if (init is null && update is null)
            {
                AegisLog.Warn("Scene", $"Script '{file}' nao possui init(self) nem update(self, dt).");
                return;
            }

            var self = CreateSceneScriptSelf(obj, entity, script);
            var instance = new SceneScriptInstance(file, obj, entity.Id, self, init, update);
            _sceneScripts.Add(instance);

            SyncObjectToSelf(instance);
            CallSceneScript(instance, init, "init", self);
            SyncSelfToObject(instance);
        }
        catch (Exception ex)
        {
            AegisLog.Warn("Scene", $"Falha ao carregar Script component '{file}' em '{entity.Name}': {ex.Message}");
        }
    }

    internal void UpdateSceneScripts(float dt)
    {
        if (_sceneScripts.Count == 0)
            return;

        foreach (var script in _sceneScripts.ToArray())
        {
            try
            {
                SyncObjectToSelf(script);
                CallSceneScript(script, script.Update, "update", script.Self, dt);
                SyncSelfToObject(script);
            }
            catch (Exception ex)
            {
                AegisLog.Warn("Scene", $"Erro em Script component '{script.File}': {ex.Message}");
            }
        }
    }

    internal void ClearSceneScripts()
        => _sceneScripts.Clear();

    private static void CallSceneScript(SceneScriptInstance script, LuaFunction? fn, string callback, params object[] args)
    {
        if (fn is null) return;

        try
        {
            fn.Call(args);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Erro em {callback}(self) do Script component '{script.File}': {ex.Message}", ex);
        }
    }

    private LuaTable CreateSceneScriptSelf(Object2D obj, SceneEntityDto entity, ComponentDto script)
    {
        var name = $"_aegis_scene_script_self_{++_sceneScriptSeq}";
        _lua.NewTable(name);
        var self = (LuaTable)_lua[name];
        self["id"] = entity.Id;
        self["name"] = entity.Name;
        self["type"] = entity.Type;
        self["object"] = obj;

        _lua.NewTable($"{name}_properties");
        var props = (LuaTable)_lua[$"{name}_properties"];
        if (script.Properties.TryGetValue("properties", out var properties))
            FillLuaTableFromJson(props, properties);
        self["properties"] = props;

        return self;
    }

    private void SyncObjectToSelf(SceneScriptInstance script)
    {
        script.Self["x"] = script.Object.X;
        script.Self["y"] = script.Object.Y;
        script.Self["scaleX"] = script.Object.ScaleX;
        script.Self["scaleY"] = script.Object.ScaleY;
        script.Self["rotation"] = script.Object.Rotation;
        script.Self["visible"] = script.Object.Visible;
    }

    private static void SyncSelfToObject(SceneScriptInstance script)
    {
        script.Object.X = LuaFloat(script.Self["x"], script.Object.X);
        script.Object.Y = LuaFloat(script.Self["y"], script.Object.Y);
        script.Object.ScaleX = LuaFloat(script.Self["scaleX"], script.Object.ScaleX);
        script.Object.ScaleY = LuaFloat(script.Self["scaleY"], script.Object.ScaleY);
        script.Object.Rotation = LuaFloat(script.Self["rotation"], script.Object.Rotation);
        script.Object.Visible = LuaBool(script.Self["visible"], script.Object.Visible);
    }

    private string ResolveProjectFile(string relativePath)
    {
        var root = Path.GetFullPath(_gameRoot);
        var safe = relativePath.Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar, '/');
        var full = Path.GetFullPath(Path.Combine(root, safe));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Script fora da pasta do jogo: {relativePath}");
        return full;
    }

    private static float LuaFloat(object? value, float fallback)
    {
        try
        {
            if (value is null) return fallback;
            var result = Convert.ToSingle(value);
            return float.IsFinite(result) ? result : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool LuaBool(object? value, bool fallback)
    {
        try
        {
            return value is null ? fallback : Convert.ToBoolean(value);
        }
        catch
        {
            return fallback;
        }
    }

    private void FillLuaTableFromJson(LuaTable table, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in value.EnumerateObject())
            table[prop.Name] = JsonToLuaValue(prop.Value);
    }

    private object? JsonToLuaValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var i) ? i : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object => JsonObjectToLuaTable(value),
            JsonValueKind.Array => JsonArrayToLuaTable(value),
            _ => null,
        };
    }

    private LuaTable JsonObjectToLuaTable(JsonElement value)
    {
        var name = $"_aegis_scene_script_props_{++_sceneScriptSeq}";
        _lua.NewTable(name);
        var table = (LuaTable)_lua[name];
        FillLuaTableFromJson(table, value);
        return table;
    }

    private LuaTable JsonArrayToLuaTable(JsonElement value)
    {
        var name = $"_aegis_scene_script_array_{++_sceneScriptSeq}";
        _lua.NewTable(name);
        var table = (LuaTable)_lua[name];
        var index = 1;
        foreach (var item in value.EnumerateArray())
            table[index++] = JsonToLuaValue(item);
        return table;
    }

    private sealed record SceneScriptInstance(
        string File,
        Object2D Object,
        string EntityId,
        LuaTable Self,
        LuaFunction? Init,
        LuaFunction? Update);
}

internal static class SceneScriptHost
{
    private static LuaRuntime? _runtime;

    public static void Attach(LuaRuntime runtime)
        => _runtime = runtime;

    public static void Register(Object2D obj, SceneEntityDto entity)
        => _runtime?.RegisterSceneScript(obj, entity);
}
