using Aegis.Display;
using Aegis.Editor;
using Aegis.Input;
using Aegis.Physics;
using Aegis.Platform;
using Aegis.Resource;
using Aegis.Scene;
using Aegis.World;
using Aegis.Effects;
using Aegis.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Aegis.Core;

public sealed class AegisGame : Game
{
    public static Color ClearColor { get; set; } = Color.Black;

    public static AegisGame Current { get; private set; } = null!;

    private readonly App _app;
    private readonly string _luaEntry;
    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch = null!;

    private const float FixedDeltaTime = 1f / 60f;
    private const float MaxFrameDelta = 0.25f;
    private const int MaxPhysicsSteps = 5;
    private float _physicsAccumulator;

    internal AegisGame(App app, string luaEntry)
    {
        Current = this;
        _app = app;
        _luaEntry = luaEntry;

        ConfigManager.Initialize(Directory.GetCurrentDirectory(), app.ScreenWidth, app.ScreenHeight);

        var initialMode = ConfigManager.Current.displayMode ?? "windowed";
        var initialBorderless = IsBorderlessMode(initialMode);
        var initialWidth = ConfigManager.Current.windowWidth;
        var initialHeight = ConfigManager.Current.windowHeight;
        if (initialBorderless)
        {
            var display = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
            initialWidth = display.Width;
            initialHeight = display.Height;
        }

        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = initialWidth,
            PreferredBackBufferHeight = initialHeight,
            IsFullScreen = false,
            SynchronizeWithVerticalRetrace = ConfigManager.Current.vsync,
            PreferMultiSampling = false,
            HardwareModeSwitch = false,
        };

        Content.RootDirectory = "res";
        IsMouseVisible = true;
        Window.Title = app.Title;
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 60.0);
        SetBorderlessWindow(initialBorderless);
        Window.ClientSizeChanged += (_, _) => SyncViewportSize();

        EditorPipeHost.Instance.Attach(this, app);
        EditorPipeHost.Instance.TryStartFromEnvironment();
    }

    protected override void LoadContent()
    {
        DisplayWakeLock.Enable();
        PhysicsWorld.Instance.Reset();
        Rigidbody2D.Gravity = 800f;
        Camera2D.Instance.ResetForNewSession();
        _physicsAccumulator = 0f;

        _spriteBatch = new SpriteBatch(GraphicsDevice);

        ResManager.Initialize(GraphicsDevice, Content);
        SaveManager.Initialize(Directory.GetCurrentDirectory());
        Renderer.Initialize(_spriteBatch, GraphicsDevice);
        FontManager.Initialize(GraphicsDevice);

        Camera2D.Instance.ViewWidth = GraphicsDevice.Viewport.Width;
        Camera2D.Instance.ViewHeight = GraphicsDevice.Viewport.Height;
        AegisLog.Info("Display", $"mode={ConfigManager.Current.displayMode} viewport={GraphicsDevice.Viewport.Width}x{GraphicsDevice.Viewport.Height} window={Window.ClientBounds.Width}x{Window.ClientBounds.Height}");

        WindowIcon.TrySet(Window.Handle, Path.Combine("res", "aegis-logo.png"), GraphicsDevice);

        _app.S2D   = new Scene2D();
        _app.Ui2D  = new Scene2D();

        _app.Lua.RegisterAll();
        _app.Lua.ExecuteFile(_luaEntry);
        HotReloadManager.Instance.Initialize(_luaEntry, _app.Lua);
        if (!_app.Lua.HasFunction("aegis_init"))
            throw new InvalidOperationException("[Aegis|Lua] Função obrigatória aegis_init não encontrada no script principal.");
        _app.Lua.CallFunction("aegis_init");
        LoadInitialEditorSceneIfRequested();
        InputManager.HardSyncFromHardware();
    }

    private void LoadInitialEditorSceneIfRequested()
    {
        var scenePath = Environment.GetEnvironmentVariable("AEGIS_START_SCENE");
        if (string.IsNullOrWhiteSpace(scenePath))
            return;

        var normalized = scenePath.Trim().Replace('\\', '/');
        _app.Lua.LoadScene(normalized, clear: true);
        AegisLog.Info("Scene", $"initial scene override: {normalized}");
    }

    protected override void Update(GameTime gameTime)
    {
        var elapsed = gameTime.ElapsedGameTime;
        EditorPipeHost.Instance.PumpAndFlush(elapsed);

        var dt = (float)elapsed.TotalSeconds;
        if (!float.IsFinite(dt) || dt <= 0f) dt = FixedDeltaTime;
        dt = MathF.Min(dt, MaxFrameDelta);

        var pause = EditorPipeHost.SimulationPausedByEditor;
        if (!pause)
        {
            InputManager.Update();
            _app.Lua.UpdateShake(dt);
            _app.Lua.TickTime(dt);            // Sprint 2: acumula getTime()
            _app.Lua.UpdateButtons();         // Sprint 2: atualiza botões interativos
            _app.Lua.UpdateFloatTexts(dt);    // Sprint 2: move e remove float texts
            _app.Lua.UpdateDraggables();      // Final: drag & drop
            _app.Lua.UpdateAutozoom(dt);      // Final: câmera autozoom por densidade
            _app.Lua.CallFunction("aegis_update", dt);
            _app.Lua.UpdateSceneScripts(dt);

            var sceneStackPaused = SceneManager.Instance.IsSceneStackActive;
            if (!sceneStackPaused)
            {
            _physicsAccumulator = MathF.Min(_physicsAccumulator + dt, MaxFrameDelta);
            for (var steps = 0; steps < MaxPhysicsSteps && _physicsAccumulator >= FixedDeltaTime; steps++)
            {
                PhysicsWorld.Instance.Step(FixedDeltaTime);
                _physicsAccumulator -= FixedDeltaTime;
            }

            _app.S2D.Update(dt);
            Camera2D.Instance.Update(dt);
            }
            _app.Ui2D.Update(dt);
            SceneManager.Instance.Update(dt);
            TweenManager.Instance.Update(dt);
            ScreenEffects.Instance.Update(dt);
            HotReloadManager.Instance.Update(dt);
        }

        DebugOverlay.Instance.Update(dt);

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(ClearColor);

        var cam = Camera2D.Instance;
        if (cam.Active)
        {
            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, null, null, null, cam.GetTransform());
        }
        else
        {
            Renderer.Begin();
        }

        _app.S2D.Draw(_spriteBatch);
        _app.Lua.CallFunction("aegis_draw");
        DebugOverlay.Instance.DrawHitboxes(_spriteBatch);

        if (cam.Active) _spriteBatch.End();
        else Renderer.End();

        // Camada UI: grafo Ui2D + primitivas imediatas (sem transformação da câmera).
        Renderer.Begin();
        _app.Ui2D.Draw(_spriteBatch);
        _app.Lua.CallFunction("aegis_draw_ui");
        Renderer.End();

        if (SceneManager.Instance.FadeAlpha > 0.001f)
        {
            Renderer.Begin();
            SceneManager.Instance.DrawOverlay(_spriteBatch);
            Renderer.End();
        }

        Renderer.Begin();
        ScreenEffects.Instance.Draw(_spriteBatch, GraphicsDevice);
        DrawScreenShaderFallback();
        DebugOverlay.Instance.Draw(_spriteBatch, _app.S2D);
        Renderer.End();

        base.Draw(gameTime);
    }



    private void DrawScreenShaderFallback()
    {
        var shader = ShaderManager.ScreenShader;
        if (shader is null || ResManager.Pixel is null) return;
        var w = GraphicsDevice.Viewport.Width;
        var h = GraphicsDevice.Viewport.Height;

        if (shader.Name == "vignette")
        {
            var alpha = Math.Clamp(shader.Intensity, 0f, 1f) * 0.45f;
            var thickX = Math.Max(1, w / 8);
            var thickY = Math.Max(1, h / 8);
            _spriteBatch.Draw(ResManager.Pixel, new Rectangle(0, 0, w, thickY), Color.Black * alpha);
            _spriteBatch.Draw(ResManager.Pixel, new Rectangle(0, h - thickY, w, thickY), Color.Black * alpha);
            _spriteBatch.Draw(ResManager.Pixel, new Rectangle(0, 0, thickX, h), Color.Black * alpha);
            _spriteBatch.Draw(ResManager.Pixel, new Rectangle(w - thickX, 0, thickX, h), Color.Black * alpha);
        }
        else if (shader.Name == "crt")
        {
            var alpha = Math.Clamp(shader.Intensity <= 0f ? 0.35f : shader.Intensity, 0f, 1f) * 0.35f;
            for (int y = 0; y < h; y += 4)
                _spriteBatch.Draw(ResManager.Pixel, new Rectangle(0, y, w, 1), Color.Black * alpha);
        }
    }

    public void ApplyWindowConfig(int width, int height, bool fullscreen)
        => ApplyWindowConfig(width, height, fullscreen ? "borderless" : "windowed");

    public void ApplyWindowConfig(int width, int height, string? displayMode)
    {
        displayMode = string.IsNullOrWhiteSpace(displayMode) ? "windowed" : displayMode.Trim().ToLowerInvariant();
        var borderless = IsBorderlessMode(displayMode);

        width = Math.Clamp(width, 320, 7680);
        height = Math.Clamp(height, 240, 4320);

        if (borderless)
        {
            var display = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
            width = Math.Clamp(display.Width, 320, 7680);
            height = Math.Clamp(display.Height, 240, 4320);
        }

        _graphics.HardwareModeSwitch = false;
        _graphics.IsFullScreen = false;
        _graphics.PreferredBackBufferWidth = width;
        _graphics.PreferredBackBufferHeight = height;
        SetBorderlessWindow(borderless);
        _graphics.ApplyChanges();

        if (borderless)
            Window.Position = Point.Zero;

        SyncViewportSize();
        AegisLog.Info("Display", $"applied mode={displayMode} viewport={GraphicsDevice.Viewport.Width}x{GraphicsDevice.Viewport.Height} window={Window.ClientBounds.Width}x{Window.ClientBounds.Height}");
    }

    private static bool IsBorderlessMode(string? displayMode)
        => displayMode?.Trim().ToLowerInvariant() is "borderless" or "fullscreen" or "fullscreen-borderless";

    private void SyncViewportSize()
    {
        if (GraphicsDevice is null) return;
        Camera2D.Instance.ViewWidth = GraphicsDevice.Viewport.Width;
        Camera2D.Instance.ViewHeight = GraphicsDevice.Viewport.Height;
    }

    private void SetBorderlessWindow(bool enabled)
    {
        var type = Window.GetType();
        var prop = type.GetProperty("IsBorderlessEXT") ?? type.GetProperty("IsBorderless");
        if (prop is not null && prop.CanWrite && prop.PropertyType == typeof(bool))
            prop.SetValue(Window, enabled);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisplayWakeLock.Disable();
            EditorPipeHost.Instance.Shutdown();
            PhysicsWorld.Instance.Reset();
            // Ensure audio resources are cleaned up to avoid OpenAL warnings
            try
            {
                Aegis.Audio.AudioManager.StopMusic();
                Aegis.Audio.AudioManager.Unload();
            }
            catch { /* best-effort cleanup; ignore errors during dispose */ }
        }

        base.Dispose(disposing);
    }
}
