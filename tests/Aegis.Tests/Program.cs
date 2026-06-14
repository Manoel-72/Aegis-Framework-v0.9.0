using Aegis.CLI;
using Aegis.Core;
using Aegis.Display;
using Aegis.Physics;
using Aegis.Resource;
using Aegis.Scene;
using Aegis.Scripting.Components;
using Aegis.Systems;
using Aegis.World;
using AegisEditor.Shared.Models;
using NLua;
using System.Diagnostics;

namespace Aegis.Tests;

internal static class Program
{
    private static readonly List<string> Failures = [];

    public static int Main()
    {
        Run("ConfigManager sanitizes resolution and display mode", ConfigManagerSanitizesConfig);
        Run("ConfigManager writes and loads aegis.cfg", ConfigManagerPersistsConfig);
        Run("ComponentFactory creates group on world and UI roots", ComponentFactoryCreatesGroups);
        Run("LuaRuntime seeded random is repeatable", LuaRuntimeSeededRandomIsRepeatable);
        Run("LuaRuntime asset API validates project assets", LuaRuntimeAssetApiValidatesAssets);
        Run("FontManager resolves project fallback font candidates", FontManagerResolvesProjectFallbackFont);
        Run("FontManager normalizes font size", FontManagerNormalizesFontSize);
        Run("AssetManifest categorizes project assets", AssetManifestCategorizesProjectAssets);
        Run("AssetValidator validates project assets", AssetValidatorValidatesProjectAssets);
        Run("AssetValidator reports missing references", AssetValidatorReportsMissingReferences);
        Run("AssetValidator reports missing scene references", AssetValidatorReportsMissingSceneReferences);
        Run("TiledMapDocument parses object layers and properties", TiledMapDocumentParsesObjects);
        Run("SceneJsonLoader instantiates editor scene entities", SceneJsonLoaderInstantiatesEntities);
        Run("SceneJsonLoader reads component-style scene JSON", SceneJsonLoaderReadsComponentStyleScene);
        Run("SceneJsonLoader resolves camera follow target", SceneJsonLoaderResolvesCameraFollowTarget);
        Run("ProjectCreator creates a runnable project skeleton", ProjectCreatorCreatesSkeleton);
        Run("SceneManager transition none loads registered scene", SceneManagerTransitionLoadsScene);
        Run("SceneManager push and pop restores Lua callbacks", SceneManagerPushPopRestoresCallbacks);
        Run("CLI build web creates validated package", CliBuildWebCreatesPackage);

        if (Failures.Count == 0)
        {
            Console.WriteLine("[Aegis.Tests] OK");
            return 0;
        }

        Console.Error.WriteLine("[Aegis.Tests] FAILED");
        foreach (var failure in Failures)
            Console.Error.WriteLine("- " + failure);
        return 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine("[PASS] " + name);
        }
        catch (Exception ex)
        {
            Failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ConfigManagerSanitizesConfig()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "aegis.cfg"), """
{
  "windowWidth": 10,
  "windowHeight": 99999,
  "displayMode": "fullscreen",
  "fullscreen": false,
  "masterVolume": 5
}
""");

        ConfigManager.Initialize(dir.Path, 1280, 720);

        AssertEqual(320, ConfigManager.Current.windowWidth, "windowWidth should be clamped");
        AssertEqual(4320, ConfigManager.Current.windowHeight, "windowHeight should be clamped");
        AssertEqual("borderless", ConfigManager.Current.displayMode, "fullscreen alias should normalize to borderless");
        AssertTrue(ConfigManager.Current.fullscreen, "borderless mode should set fullscreen compatibility flag");
        AssertEqual(1f, ConfigManager.Current.masterVolume, "masterVolume should be clamped");
    }

    private static void ConfigManagerPersistsConfig()
    {
        using var dir = new TempDir();
        ConfigManager.Initialize(dir.Path, 800, 600);
        ConfigManager.SetDisplayMode("window");
        ConfigManager.SetResolution(1440, 900);

        var text = File.ReadAllText(Path.Combine(dir.Path, "aegis.cfg"));
        AssertContains(text, "\"windowWidth\": 1440", "width should be saved");
        AssertContains(text, "\"windowHeight\": 900", "height should be saved");
        AssertContains(text, "\"displayMode\": \"windowed\"", "display mode should be saved normalized");
    }

    private static void ComponentFactoryCreatesGroups()
    {
        var app = new App("Tests", 640, 480)
        {
            S2D = new Scene2D(),
            Ui2D = new Scene2D(),
        };
        var factory = new ComponentFactory(app);

        using var lua = new Lua();
        lua.DoString("opts = { x = 12, y = 34, z = 7, alpha = 0.5, scale = 2 }");
        var worldObj = factory.Create("group", (LuaTable)lua["opts"]);

        AssertTrue(ReferenceEquals(app.S2D, worldObj.Parent), "default group should be parented to world root");
        AssertEqual(12f, worldObj.X, "x should be applied");
        AssertEqual(34f, worldObj.Y, "y should be applied");
        AssertEqual(7, worldObj.Z, "z should be applied");
        AssertEqual(0.5f, worldObj.Alpha, "alpha should be applied");
        AssertEqual(2f, worldObj.ScaleX, "scaleX should use scale fallback");
        AssertEqual(2f, worldObj.ScaleY, "scaleY should use scale fallback");

        lua.DoString("ui_opts = { layer = 'ui' }");
        var uiObj = factory.Create("group", (LuaTable)lua["ui_opts"]);
        AssertTrue(ReferenceEquals(app.Ui2D, uiObj.Parent), "ui group should be parented to UI root");
    }

    private static void LuaRuntimeSeededRandomIsRepeatable()
    {
        var app = new App("Random Test", 640, 480)
        {
            S2D = new Scene2D(),
            Ui2D = new Scene2D(),
        };
        using var lua = new Aegis.Scripting.LuaRuntime(app);

        lua.SetRandomSeed(2026);
        var a = lua.RandomInt(1, 1000);
        var b = lua.RandomFloat(0f, 1f);

        lua.SetRandomSeed(2026);
        AssertEqual(a, lua.RandomInt(1, 1000), "same seed should repeat randomInt");
        AssertEqual(b, lua.RandomFloat(0f, 1f), "same seed should repeat randomFloat");
    }

    private static void LuaRuntimeAssetApiValidatesAssets()
    {
        using var dir = new TempDir();
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(dir.Path);
            Directory.CreateDirectory(Path.Combine(dir.Path, "res", "sprites"));
            File.WriteAllText(Path.Combine(dir.Path, "main.lua"), "function aegis_init() end\n");
            File.WriteAllText(Path.Combine(dir.Path, "aegis.toml"), "entry = \"main.lua\"\n");
            File.WriteAllText(Path.Combine(dir.Path, "aegis.cfg"), "{}\n");
            WriteMinimalPng(Path.Combine(dir.Path, "res", "sprites", "player.png"));

            var app = new App("Asset API Test", 640, 480)
            {
                S2D = new Scene2D(),
                Ui2D = new Scene2D(),
            };
            using var lua = new Aegis.Scripting.LuaRuntime(app);

            AssertEqual("sprites/player.png", lua.Asset("sprites\\player.png"), "asset should normalize slashes");
            AssertTrue(lua.AssetExists("sprites/player.png"), "assetExists should return true for existing asset");
            AssertTrue(lua.ValidateAssets(), "validateAssets should pass for project with referenced files");

            var missingThrown = false;
            try
            {
                _ = lua.Asset("sprites/missing.png");
            }
            catch (FileNotFoundException ex)
            {
                missingThrown = ex.Message.Contains("Asset nao encontrado", StringComparison.OrdinalIgnoreCase);
            }

            AssertTrue(missingThrown, "missing asset should throw a clear asset error");
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    private static void FontManagerResolvesProjectFallbackFont()
    {
        using var dir = new TempDir();
        var previousRoot = FontManager.FontRoot;
        try
        {
            var fontRoot = Path.Combine(dir.Path, "res", "fonts");
            Directory.CreateDirectory(fontRoot);
            var expected = Path.Combine(fontRoot, "Inter-Regular.ttf");
            File.WriteAllBytes(expected, [0, 1, 2, 3]);

            FontManager.FontRoot = fontRoot;
            var resolved = FontManager.ResolveDefaultFontPathForTests();

            AssertEqual(Path.GetFullPath(expected), Path.GetFullPath(resolved), "default font should prefer Inter-Regular.ttf from project font root");
        }
        finally
        {
            FontManager.FontRoot = previousRoot;
        }
    }

    private static void FontManagerNormalizesFontSize()
    {
        AssertEqual(8, FontManager.NormalizeSizeForTests(1), "font size should clamp to minimum");
        AssertEqual(24, FontManager.NormalizeSizeForTests(24), "font size should preserve valid value");
        AssertEqual(96, FontManager.NormalizeSizeForTests(999), "font size should clamp to maximum");
    }

    private static void AssetValidatorValidatesProjectAssets()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "main.lua"), """
function aegis_init()
    aegis.playSound("jump.wav")
end
""");
        File.WriteAllText(Path.Combine(dir.Path, "aegis.toml"), "entry = \"main.lua\"\n");
        File.WriteAllText(Path.Combine(dir.Path, "aegis.cfg"), "{}\n");
        Directory.CreateDirectory(Path.Combine(dir.Path, "res", "audio"));
        WriteMinimalWav(Path.Combine(dir.Path, "res", "audio", "jump.wav"));

        var report = AssetValidator.ValidateProject(dir.Path);

        AssertEqual(0, report.ErrorCount, "valid project should have no asset errors");
    }

    private static void AssetManifestCategorizesProjectAssets()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "res", "sprites"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "res", "audio"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "res", "fonts"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "res", "tilemaps"));

        File.WriteAllBytes(Path.Combine(dir.Path, "res", "sprites", "player.png"), [0x89, 0x50, 0x4E, 0x47]);
        WriteMinimalWav(Path.Combine(dir.Path, "res", "audio", "jump.wav"));
        File.WriteAllBytes(Path.Combine(dir.Path, "res", "fonts", "Inter-Regular.ttf"), [0, 1, 0, 0]);
        File.WriteAllText(Path.Combine(dir.Path, "res", "tilemaps", "level1.json"), "{}");

        var manifest = AssetManifest.Build(dir.Path);

        AssertEqual(4, manifest.Entries.Count, "manifest should index all files in res");
        AssertEqual(1, manifest.SpriteCount, "manifest should count sprites");
        AssertEqual(1, manifest.AudioCount, "manifest should count audio");
        AssertEqual(1, manifest.FontCount, "manifest should count fonts");
        AssertEqual(1, manifest.TilemapCount, "manifest should count tilemaps");
    }

    private static void AssetValidatorReportsMissingReferences()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "main.lua"), """
function aegis_init()
    aegis.playSound("missing.wav")
    aegis.create("sprite", { path = "sprites/player.png" })
end
""");
        File.WriteAllText(Path.Combine(dir.Path, "aegis.toml"), "entry = \"main.lua\"\n");
        Directory.CreateDirectory(Path.Combine(dir.Path, "res"));

        var report = AssetValidator.ValidateProject(dir.Path);

        AssertTrue(report.ErrorCount >= 2, "missing audio and sprite should be reported");
        AssertTrue(report.Issues.Any(i => i.Code == "asset.reference.missing"), "missing references should use asset.reference.missing code");
    }

    private static void AssetValidatorReportsMissingSceneReferences()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "main.lua"), "function aegis_init() end\n");
        File.WriteAllText(Path.Combine(dir.Path, "aegis.toml"), "entry = \"main.lua\"\n");
        Directory.CreateDirectory(Path.Combine(dir.Path, "res"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "scenes"));
        File.WriteAllText(Path.Combine(dir.Path, "scenes", "main.scene.json"), """
{
  "format": "aegis.scene",
  "version": 2,
  "entities": [
    {
      "id": "player",
      "name": "Player",
      "type": "Sprite",
      "components": {
        "SpriteRenderer": { "sprite": "sprites/missing.png" },
        "Script": { "file": "scripts/player.lua" }
      }
    }
  ]
}
""");

        var report = AssetValidator.ValidateProject(dir.Path);

        AssertTrue(report.ErrorCount >= 2, "missing scene sprite and script should be reported");
        AssertTrue(report.Issues.Any(i => i.Path?.Contains("main.scene.json", StringComparison.OrdinalIgnoreCase) == true), "scene file should be mentioned in issue path");
    }

    private static void TiledMapDocumentParsesObjects()
    {
        var map = TiledMapDocument.Parse("""
{
  "width": 20,
  "height": 12,
  "tilewidth": 16,
  "tileheight": 16,
  "properties": [
    { "name": "music", "type": "string", "value": "stage1.wav" }
  ],
  "tilesets": [
    { "firstgid": 1, "name": "base", "image": "../sprites/tiles.png", "tilewidth": 16, "tileheight": 16, "columns": 4, "tilecount": 16 }
  ],
  "layers": [
    { "type": "tilelayer", "name": "Ground", "width": 20, "height": 12, "visible": true, "opacity": 0.75, "data": [] },
    {
      "type": "objectgroup",
      "name": "Objects",
      "objects": [
        {
          "id": 1,
          "name": "Start",
          "type": "player_spawn",
          "x": 32,
          "y": 48,
          "width": 16,
          "height": 16,
          "properties": [
            { "name": "facing", "type": "string", "value": "right" },
            { "name": "checkpoint", "type": "bool", "value": true }
          ]
        }
      ]
    }
  ]
}
""");

        AssertEqual(20, map.Width, "map width should parse");
        AssertEqual(12, map.Height, "map height should parse");
        AssertEqual("stage1.wav", map.Properties["music"], "map property should parse");
        AssertEqual(2, map.Layers.Count, "layers should parse");
        AssertEqual("Ground", map.Layers[0].Name, "tile layer name should parse");
        AssertEqual(0.75f, map.Layers[0].Opacity, "layer opacity should parse");
        AssertEqual(1, map.Objects.Count, "object layer item should parse");
        AssertEqual("Objects", map.Objects[0].LayerName, "object layer name should parse");
        AssertEqual("player_spawn", map.Objects[0].Type, "object type should parse");
        AssertEqual("right", map.Objects[0].Properties["facing"], "object string property should parse");
        AssertEqual("true", map.Objects[0].Properties["checkpoint"], "object bool property should parse");
        AssertEqual(1, map.Tilesets.Count, "tileset refs should parse");
    }

    private static void SceneJsonLoaderInstantiatesEntities()
    {
        using var dir = new TempDir();
        var scenePath = Path.Combine(dir.Path, "main.scene.json");
        File.WriteAllText(scenePath, """
{
  "format": "aegis.scene",
  "version": 2,
  "name": "Runtime Test",
  "kind": "2d",
  "entities": [
    { "id": "root", "name": "Root", "type": "Group", "x": 10, "y": 20, "scaleX": 1, "scaleY": 1 },
    { "id": "cam", "name": "Camera", "type": "Camera", "parentId": "root", "x": 30, "y": 40, "scaleX": 2, "scaleY": 3 }
  ],
  "tilemaps": []
}
""");

        var root = new Scene2D();
        var result = new SceneJsonLoader().Load(scenePath, root);

        AssertEqual("Runtime Test", result.Name, "scene name should load");
        AssertEqual(2, result.EntityCount, "entity count should load");
        AssertEqual(1, root.Children.Count, "only root entity should be parented to scene root");

        var group = root.Children[0];
        AssertEqual(10f, group.X, "group x should apply");
        AssertEqual(20f, group.Y, "group y should apply");
        AssertEqual(1, group.Children.Count, "child entity should be parented by parentId");

        var cameraMarker = group.Children[0];
        AssertEqual(30f, cameraMarker.X, "child x should apply");
        AssertEqual(40f, cameraMarker.Y, "child y should apply");
        AssertEqual(2f, cameraMarker.ScaleX, "child scaleX should apply");
        AssertEqual(3f, cameraMarker.ScaleY, "child scaleY should apply");
    }

    private static void SceneJsonLoaderReadsComponentStyleScene()
    {
        PhysicsWorld.Instance.Reset();
        using var dir = new TempDir();
        var scenePath = Path.Combine(dir.Path, "component.scene.json");
        File.WriteAllText(scenePath, """
{
  "format": "aegis.scene",
  "version": 2,
  "name": "Component Scene",
  "kind": "2d",
  "entities": [
    {
      "id": "entity-42",
      "name": "Player",
      "type": "Group",
      "components": {
        "Transform": {
          "position": [100, 200],
          "rotation": 0.25,
          "scale": [2, 3]
        },
        "Collider2D": {
          "shape": "box",
          "size": [32, 48],
          "offset": [1, 2],
          "is_trigger": true
        },
        "Rigidbody2D": {
          "type": "kinematic",
          "gravity_scale": 0,
          "linear_drag": 4
        }
      }
    }
  ],
  "tilemaps": []
}
""");

        var root = new Scene2D();
        var result = new SceneJsonLoader().Load(scenePath, root);

        AssertEqual("Component Scene", result.Name, "component scene name should load");
        AssertEqual(1, result.EntityCount, "component entity count should load");
        AssertEqual(1, root.Children.Count, "component entity should be created");

        var player = root.Children[0];
        AssertEqual(100f, player.X, "Transform.position x should apply");
        AssertEqual(200f, player.Y, "Transform.position y should apply");
        AssertEqual(2f, player.ScaleX, "Transform.scale x should apply");
        AssertEqual(3f, player.ScaleY, "Transform.scale y should apply");
        AssertEqual(0.25f, player.Rotation, "Transform.rotation should apply");
        AssertEqual(1, CollisionSystem.Instance.ColliderCount, "Collider2D should register collider");
        AssertEqual(1, PhysicsWorld.Instance.BodyCount, "Rigidbody2D should register body");
        PhysicsWorld.Instance.Reset();
    }

    private static void SceneJsonLoaderResolvesCameraFollowTarget()
    {
        using var dir = new TempDir();
        var scenePath = Path.Combine(dir.Path, "camera-follow.scene.json");

        File.WriteAllText(scenePath, """
{
  "format": "aegis.scene",
  "version": 2,
  "name": "Camera Follow Scene",
  "kind": "2d",
  "entities": [
    {
      "id": "player-id",
      "name": "Player By Id",
      "type": "Group",
      "components": {
        "Transform": { "position": [120, 240], "scale": [1, 1] }
      }
    },
    {
      "id": "camera-id",
      "name": "Main Camera",
      "type": "Camera",
      "components": {
        "Transform": { "position": [0, 0], "scale": [1, 1] },
        "Camera": { "zoom": 1, "follow_target": "player-id" }
      }
    }
  ],
  "tilemaps": []
}
""");

        Camera2D.Instance.ResetForNewSession();
        _ = new SceneJsonLoader().Load(scenePath, new Scene2D());
        AssertTrue(Camera2D.Instance.Active, "camera should activate when follow_target resolves by id");
        Camera2D.Instance.Update(1f);
        AssertEqual(120f, Camera2D.Instance.X, "camera should follow target resolved by id on x");
        AssertEqual(240f, Camera2D.Instance.Y, "camera should follow target resolved by id on y");

        File.WriteAllText(scenePath, """
{
  "format": "aegis.scene",
  "version": 2,
  "name": "Camera Follow Scene",
  "kind": "2d",
  "entities": [
    {
      "id": "hero-id",
      "name": "Hero By Name",
      "type": "Group",
      "components": {
        "Transform": { "position": [320, 160], "scale": [1, 1] }
      }
    },
    {
      "id": "camera-name",
      "name": "Main Camera",
      "type": "Camera",
      "components": {
        "Transform": { "position": [0, 0], "scale": [1, 1] },
        "Camera": { "zoom": 1, "follow_target": "Hero By Name" }
      }
    }
  ],
  "tilemaps": []
}
""");

        Camera2D.Instance.ResetForNewSession();
        _ = new SceneJsonLoader().Load(scenePath, new Scene2D());
        AssertTrue(Camera2D.Instance.Active, "camera should activate when follow_target resolves by name");
        Camera2D.Instance.Update(1f);
        AssertEqual(320f, Camera2D.Instance.X, "camera should follow target resolved by name on x");
        AssertEqual(160f, Camera2D.Instance.Y, "camera should follow target resolved by name on y");
        Camera2D.Instance.ResetForNewSession();
    }

    private static void ProjectCreatorCreatesSkeleton()
    {
        using var dir = new TempDir();
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(dir.Path);
            ProjectCreator.Create("sample-game");

            var root = Path.Combine(dir.Path, "sample-game");
            AssertTrue(Directory.Exists(root), "project root should exist");
            AssertTrue(File.Exists(Path.Combine(root, "main.lua")), "main.lua should exist");
            AssertTrue(File.Exists(Path.Combine(root, "aegis.toml")), "aegis.toml should exist");
            AssertTrue(File.Exists(Path.Combine(root, "res", "sprites", "player.png")), "player sprite should exist");
            AssertContains(File.ReadAllText(Path.Combine(root, "aegis.toml")), "entry  = \"main.lua\"", "entry should point to main.lua");
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    private static void SceneManagerTransitionLoadsScene()
    {
        using var dir = new TempDir();
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(dir.Path);
            Directory.CreateDirectory(Path.Combine(dir.Path, "scenes"));
            File.WriteAllText(Path.Combine(dir.Path, "scenes", "menu.lua"), """
function aegis_init()
end

function scene_loaded_marker()
end
""");

            var app = new App("Scene Test", 640, 480)
            {
                S2D = new Scene2D(),
                Ui2D = new Scene2D(),
            };
            using var lua = new Aegis.Scripting.LuaRuntime(app);
            lua.RegisterAll();

            SceneManager.Instance.RegisterScene("menu", "scenes/menu.lua");
            SceneManager.Instance.TransitionTo("menu", "none", 0.01f);

            AssertTrue(lua.HasFunction("scene_loaded_marker"), "transition should load the registered Lua scene immediately");
            AssertTrue(!SceneManager.Instance.IsTransitioning, "none transition should finish immediately");
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    private static void SceneManagerPushPopRestoresCallbacks()
    {
        using var dir = new TempDir();
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(dir.Path);
            Directory.CreateDirectory(Path.Combine(dir.Path, "scenes"));
            File.WriteAllText(Path.Combine(dir.Path, "scenes", "pause.lua"), """
function aegis_init()
    overlay_inited = true
end

function aegis_update(dt)
    overlay_updated = true
end
""");

            var app = new App("Scene Stack Test", 640, 480)
            {
                S2D = new Scene2D(),
                Ui2D = new Scene2D(),
            };
            using var lua = new Aegis.Scripting.LuaRuntime(app);
            lua.RegisterAll();
            lua.ExecuteString("""
base_updated = false
overlay_updated = false
overlay_inited = false

function aegis_update(dt)
    base_updated = true
end
""");

            SceneManager.Instance.RegisterScene("pause", "scenes/pause.lua");
            SceneManager.Instance.PushScene("pause");

            AssertTrue(IsLuaTrue(lua.GetGlobal("overlay_inited")), "pushScene should run overlay init");
            lua.CallFunction("aegis_update", 0.016f);
            AssertTrue(IsLuaTrue(lua.GetGlobal("overlay_updated")), "pushScene should replace update callback");
            AssertTrue(!IsLuaTrue(lua.GetGlobal("base_updated")), "base update should be paused while overlay is active");

            AssertTrue(SceneManager.Instance.PopScene(), "popScene should return true when a scene is stacked");
            lua.CallFunction("aegis_update", 0.016f);
            AssertTrue(IsLuaTrue(lua.GetGlobal("base_updated")), "popScene should restore previous update callback");
            AssertTrue(!SceneManager.Instance.PopScene(), "popScene should return false when stack is empty");
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    private static void CliBuildWebCreatesPackage()
    {
        using var dir = new TempDir();
        var repoRoot = FindRepoRoot();
        var gameRoot = Path.Combine(dir.Path, "web-game");
        Directory.CreateDirectory(gameRoot);
        File.WriteAllText(Path.Combine(gameRoot, "main.lua"), "function aegis_init() end\n");
        File.WriteAllText(Path.Combine(gameRoot, "aegis.toml"), """
[game]
title = "test-web-build"
width = 640
height = 480
entry = "main.lua"
""");

        var exitCode = RunProcess(
            "dotnet",
            $"run --no-restore --project \"{Path.Combine(repoRoot, "src", "Aegis.CLI", "Aegis.CLI.csproj")}\" -- build \"{gameRoot}\" --target web",
            repoRoot);

        AssertEqual(0, exitCode, "web build command should succeed");

        var outDir = Path.Combine(repoRoot, "dist", "test-web-build-web");
        AssertTrue(Directory.Exists(outDir), "web build output directory should exist");
        AssertTrue(File.Exists(Path.Combine(outDir, "index.html")), "web build should create index.html");
        AssertTrue(File.Exists(Path.Combine(outDir, "game", "main.lua")), "web build should copy main.lua");
        AssertTrue(File.Exists(Path.Combine(repoRoot, "dist", "test-web-build-web.zip")), "web build should create zip");
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool IsLuaTrue(object? value)
        => value is bool b && b;

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}. Expected '{expected}', got '{actual}'.");
    }

    private static void AssertContains(string haystack, string needle, string message)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
            throw new InvalidOperationException($"{message}. Missing '{needle}'.");
    }

    private static int RunProcess(string fileName, string arguments, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        if (process is null) return 1;

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr.TrimEnd());
        return process.ExitCode;
    }

    private static void WriteMinimalWav(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        const short channels = 1;
        const int sampleRate = 8000;
        const short bitsPerSample = 16;
        const short blockAlign = channels * bitsPerSample / 8;
        const int byteRate = sampleRate * blockAlign;
        var sampleCount = 80;
        var dataSize = sampleCount * blockAlign;

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);
        for (var i = 0; i < sampleCount; i++)
            writer.Write((short)0);
    }

    private static void WriteMinimalPng(string path)
    {
        const string png1x1 =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=";
        File.WriteAllBytes(path, Convert.FromBase64String(png1x1));
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Aegis.sln"))
                && File.Exists(Path.Combine(current.FullName, "src", "Aegis.CLI", "Aegis.CLI.csproj")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find Aegis repository root.");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aegis-tests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best effort cleanup; tests should not fail because temp cleanup was denied.
            }
        }
    }
}
