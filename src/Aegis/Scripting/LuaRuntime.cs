using Aegis.Audio;
using Aegis.Core;
using Aegis.Display;
using Aegis.Input;
using Aegis.Physics;
using Aegis.Resource;
using Aegis.Scene;
using Aegis.Scripting.Components;
using Aegis.World;
using Aegis.Effects;
using Aegis.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NLua;

namespace Aegis.Scripting;

/// <summary>
/// Integração NLua: registra a API aegis.* e gerencia ciclo de vida dos scripts.
/// v0.4: CollisionSystem AABB · layers · onCollide · Rigidbody2D · Raycast
/// </summary>
public sealed partial class LuaRuntime : IDisposable
{
    private readonly Lua _lua;
    private readonly App _app;
    private readonly ComponentFactory _components;
    private readonly Dictionary<string, LuaApiStatus> _apiStatuses = new();
    private readonly HashSet<string> _warnedExperimentalApis = new(StringComparer.Ordinal);
    private static Random _rng = new();

    private float _shakeTime;
    private float _shakeIntensity;

    // Mapa de Colliders para manter referência e permitir remoção
    private readonly Dictionary<int, Collider> _colliders = new();
    private int _colliderIdSeq = 0;
    private string _gameRoot = Directory.GetCurrentDirectory();

    private string _mainLuaFullPath = string.Empty;
    private ParticleSystem2D? _particles;
    private LuaFunction? _onSceneEnter;
    private LuaFunction? _onSceneExit;
    private LuaTable? _sceneData;
    private readonly Stack<SceneStackFrame> _sceneStack = new();

    private enum LuaApiStatus
    {
        Stable,
        Legacy,
        Experimental
    }

    public LuaRuntime(App app)
    {
        _app = app;
        _components = new ComponentFactory(app);
        _lua = new Lua();
        _lua.State.Encoding = System.Text.Encoding.UTF8;
        SceneManager.Instance.Initialize(app, this);
        SceneScriptHost.Attach(this);
    }

    public void RegisterAll()
    {
        _lua.NewTable("aegis");
        _lua["aegis_init"]    = null;
        _lua["aegis_update"]  = null;
        _lua["aegis_draw"]    = null;
        _lua["aegis_draw_ui"] = null; // primitivas imediatas na camada UI (Ui2D já desenhado antes)

        // ── Core ────────────────────────────────────────────────────
        Reg("aegis.__apiWarning",    nameof(ApiWarning));

        // Stable MVP API. Prefer this for new templates; legacy aliases remain registered below.
        Reg("aegis.create",          nameof(Create));
        Reg("aegis.destroy",         nameof(Destroy));
        Reg("aegis.asset",           nameof(Asset));
        Reg("aegis.assetExists",     nameof(AssetExists));
        Reg("aegis.validateAssets",  nameof(ValidateAssets));
        Reg("aegis.assetReport",     nameof(AssetReport));

        RegLegacy("aegis.newSprite",       nameof(NewSprite));
        RegLegacy("aegis.newRect",         nameof(NewRect));
        RegLegacy("aegis.removeObject",    nameof(RemoveObject));

        // ── Transform ───────────────────────────────────────────────
        Reg("aegis.setPosition",     nameof(SetPosition));
        Reg("aegis.setPositionNorm", nameof(SetPositionNorm));
        Reg("aegis.centerX",         nameof(CenterX));
        Reg("aegis.setZ",            nameof(SetZ));
        Reg("aegis.getZ",            nameof(GetZ));
        RegLegacy("aegis.setZOrder",       nameof(SetZ));
        RegLegacy("aegis.getZOrder",       nameof(GetZ));
        Reg("aegis.move",            nameof(Move));
        Reg("aegis.setScale",        nameof(SetScale));
        Reg("aegis.setRotation",     nameof(SetRotation));
        Reg("aegis.setAlpha",        nameof(SetAlpha));
        Reg("aegis.setVisible",      nameof(SetVisible));
        Reg("aegis.setFlip",         nameof(SetFlip));
        Reg("aegis.setAnimFlip",     nameof(SetFlip));
        Reg("aegis.setPivot",        nameof(SetPivot));
        Reg("aegis.getX",            nameof(GetX));
        Reg("aegis.getY",            nameof(GetY));
        Reg("aegis.getWidth",        nameof(GetWidth));
        Reg("aegis.getHeight",       nameof(GetHeight));

        // ── Label ───────────────────────────────────────────────────
        RegLegacy("aegis.newLabel",        nameof(NewLabel));
        Reg("aegis.newLabelSize",          nameof(NewLabelSize));
        Reg("aegis.setText",         nameof(SetText));
        Reg("aegis.setColor",        nameof(SetColor));

        // ── Input ───────────────────────────────────────────────────
        Reg("aegis.keyDown",         nameof(KeyDown));
        Reg("aegis.keyPressed",      nameof(KeyPressed));
        Reg("aegis.mouseX",          nameof(GetMouseX));
        Reg("aegis.mouseY",          nameof(GetMouseY));
        Reg("aegis.mouseLeft",       nameof(MouseLeft));
        Reg("aegis.mouseLeftJust",   nameof(MouseLeftJust));
        Reg("aegis.mouseRight",      nameof(MouseRight));
        Reg("aegis.mouseRightJust",  nameof(MouseRightJust));
        Reg("aegis.mouseScroll",     nameof(GetScrollDelta));
        Reg("aegis.padConnected",     nameof(PadConnected));
        Reg("aegis.padDown",          nameof(PadDown));
        Reg("aegis.padPressed",       nameof(PadPressed));
        Reg("aegis.padAxis",          nameof(PadAxis));
        Reg("aegis.padVibrate",       nameof(PadVibrate));

        // ── Screen ──────────────────────────────────────────────────
        Reg("aegis.screenWidth",     nameof(GetScreenWidth));
        Reg("aegis.screenHeight",    nameof(GetScreenHeight));

        // ── Utils ───────────────────────────────────────────────────
        Reg("aegis.log",             nameof(Log));
        Reg("aegis.setRandomSeed",   nameof(SetRandomSeed));
        Reg("aegis.randomInt",       nameof(RandomInt));
        Reg("aegis.randomFloat",     nameof(RandomFloat));
        Reg("aegis.clearAll",        nameof(ClearAll));
        Reg("aegis.uiClear",         nameof(UiClear));
        Reg("aegis.worldClear",      nameof(WorldClear));
        Reg("aegis.drawText",        nameof(DrawText));
        Reg("aegis.drawRect",        nameof(DrawRect));
        Reg("aegis.drawSprite",      nameof(DrawSprite));
        Reg("aegis.drawLine",        nameof(DrawLine));
        Reg("aegis.drawCircle",      nameof(DrawCircle));

        // ── v0.2 AnimatedSprite ─────────────────────────────────────
        RegLegacy("aegis.newAnim",         nameof(NewAnim));
        Reg("aegis.playAnim",        nameof(PlayAnim));
        Reg("aegis.stopAnim",        nameof(StopAnim));
        Reg("aegis.resumeAnim",      nameof(ResumeAnim));
        Reg("aegis.animFrame",       nameof(AnimFrame));
        Reg("aegis.animPlaying",     nameof(AnimPlaying));

        // ── v0.2 Camera2D ───────────────────────────────────────────
        Reg("aegis.setCameraTarget", nameof(SetCameraTarget));
        Reg("aegis.setCameraOff",    nameof(SetCameraOff));
        Reg("aegis.setCameraZoom",   nameof(SetCameraZoom));
        Reg("aegis.setCameraOffset", nameof(SetCameraOffset));
        Reg("aegis.setCameraLimits", nameof(SetCameraLimits));
        Reg("aegis.setCameraDeadzone", nameof(SetCameraDeadzone));
        Reg("aegis.setCameraLookahead", nameof(SetCameraLookahead));
        Reg("aegis.cameraX",         nameof(GetCameraX));
        Reg("aegis.cameraY",         nameof(GetCameraY));
        Reg("aegis.screenToWorldX",  nameof(ScreenToWorldX));
        Reg("aegis.screenToWorldY",  nameof(ScreenToWorldY));

        // ── v0.2 ScreenShake ────────────────────────────────────────
        Reg("aegis.screenShake",     nameof(ScreenShake));

        // ── v0.3 Audio ──────────────────────────────────────────────
        Reg("aegis.playSound",       nameof(PlaySound));
        Reg("aegis.playSoundEx",     nameof(PlaySoundEx));
        Reg("aegis.playMusic",       nameof(PlayMusic));
        Reg("aegis.stopMusic",       nameof(StopMusic));
        Reg("aegis.pauseMusic",      nameof(PauseMusic));
        Reg("aegis.resumeMusic",     nameof(ResumeMusic));
        Reg("aegis.setSfxVolume",    nameof(SetSfxVolume));
        Reg("aegis.setMusicVolume",  nameof(SetMusicVolume));
        Reg("aegis.musicPlaying",    nameof(MusicPlaying));
        Reg("aegis.playSoundAt",      nameof(PlaySoundAt));
        Reg("aegis.setGroupVolume",   nameof(SetGroupVolume));
        Reg("aegis.crossfadeTo",      nameof(CrossfadeTo));
        Reg("aegis.playMusicLooped",  nameof(PlayMusicLooped));

        // ── v0.3 RichLabel ──────────────────────────────────────────
        RegLegacy("aegis.newRichLabel",    nameof(NewRichLabel));
        Reg("aegis.newRichLabelSize",      nameof(NewRichLabelSize));
        Reg("aegis.setMarkup",       nameof(SetMarkup));
        Reg("aegis.setPivotRich",    nameof(SetPivotRich));

        // ── v0.3 Font TTF ───────────────────────────────────────────
        Reg("aegis.loadFont",        nameof(LoadFont));
        Reg("aegis.loadDefaultFont", nameof(LoadDefaultFont));
        Reg("aegis.setFont",         nameof(SetFont));
        Reg("aegis.setFontRich",     nameof(SetFontRich));

        // ── v0.3 NineSlice ──────────────────────────────────────────
        RegLegacy("aegis.newPanel",        nameof(NewPanel));
        Reg("aegis.setPanelSize",    nameof(SetPanelSize));
        RegLegacy("aegis.newFlow",         nameof(NewFlow));
        Reg("aegis.flowAdd",         nameof(FlowAdd));
        Reg("aegis.flowLayout",      nameof(FlowLayout));
        Reg("aegis.flowSet",         nameof(FlowSet));

        // ── v0.4 Collider & CollisionSystem ─────────────────────────
        Reg("aegis.addCollider",        nameof(AddCollider));
        Reg("aegis.addCircleCollider",  nameof(AddCircleCollider));
        RegExperimental("aegis.addSlopeCollider",   nameof(AddSlopeCollider));   // Slope/Ramp
        RegExperimental("aegis.setSlopeDir",        nameof(SetSlopeDir));        // "left" | "right"
        Reg("aegis.removeCollider",     nameof(RemoveCollider));
        Reg("aegis.setColliderLayer",nameof(SetColliderLayer));
        Reg("aegis.setColliderMask", nameof(SetColliderMask));
        Reg("aegis.setTrigger",      nameof(SetTrigger));
        RegExperimental("aegis.setOneWay",       nameof(SetOneWay));   // Sprint 4: plataformas one-way
        Reg("aegis.setColliderOffset",nameof(SetColliderOffset));
        Reg("aegis.onCollide",       nameof(OnCollide));
        Reg("aegis.onCollideEnter",  nameof(OnCollideEnter));
        Reg("aegis.onCollideExit",   nameof(OnCollideExit));
        Reg("aegis.getColliderOf",   nameof(GetColliderOf));

        // ── v0.4 Rigidbody2D ────────────────────────────────────────
        Reg("aegis.addRigidbody",    nameof(AddRigidbody));
        Reg("aegis.removeRigidbody", nameof(RemoveRigidbody));
        Reg("aegis.setVelocity",     nameof(SetVelocity));
        Reg("aegis.setVelocityX",    nameof(SetVelocityX));
        Reg("aegis.addImpulseY",     nameof(AddImpulseY));
        Reg("aegis.setVel",          nameof(SetVel));
        Reg("aegis.setVelX",         nameof(SetVelX));
        Reg("aegis.jumpY",           nameof(JumpY));
        Reg("aegis.addVelocity",     nameof(AddVelocity));
        Reg("aegis.getVelocityX",    nameof(GetVelocityX));
        Reg("aegis.getVelocityY",    nameof(GetVelocityY));
        Reg("aegis.setGravity",      nameof(SetGravity));
        Reg("aegis.setGroundFriction", nameof(SetGroundFriction));
        Reg("aegis.setGlobalGravity",nameof(SetGlobalGravity));
        Reg("aegis.setKinematic",    nameof(SetKinematic));
        Reg("aegis.isGrounded",      nameof(IsGrounded));

        // ── v0.5 Sprites & Assets ──────────────────────────────────
        Reg("aegis.setFrame",        nameof(SetFrame));
        Reg("aegis.clearFrame",      nameof(ClearFrame));
        Reg("aegis.loadAtlas",       nameof(LoadAtlas));
        Reg("aegis.setAtlasFrame",   nameof(SetAtlasFrame));

        // ── v0.6 Animator ───────────────────────────────────────────
        Reg("aegis.newAnimator",     nameof(NewAnimator));
        Reg("aegis.addClip",         nameof(AddClip));
        Reg("aegis.play",            nameof(Play));
        Reg("aegis.stopAnimator",    nameof(StopAnimator));
        Reg("aegis.currentClip",     nameof(CurrentClip));
        Reg("aegis.animFinished",    nameof(AnimFinished));
        Reg("aegis.isAnimFinished",  nameof(AnimFinished));
        Reg("aegis.onAnimEnd",       nameof(OnAnimEnd));
        Reg("aegis.newAtlasAnimator", nameof(NewAtlasAnimator));
        Reg("aegis.addAtlasClip",    nameof(AddAtlasClip));

        // ── v0.4 Raycast ────────────────────────────────────────────
        Reg("aegis.raycast",         nameof(DoRaycast));
        Reg("aegis.raycastMask",     nameof(DoRaycastMask));
        Reg("aegis.lineOfSight",     nameof(LineOfSight));

        // ── v0.7 Tilemaps, Procedural, SceneManager ──────────────────
        Reg("aegis.loadTilemap",     nameof(LoadTilemap));
        Reg("aegis.createTilemap",   nameof(CreateTilemap));
        Reg("aegis.generateTilemap", nameof(GenerateTilemap));
        Reg("aegis.setTile",         nameof(SetTile));
        Reg("aegis.getTile",         nameof(GetTile));
        Reg("aegis.setTileCulling",  nameof(SetTileCulling));
        Reg("aegis.buildTilemapColliders", nameof(BuildTilemapColliders));
        Reg("aegis.clearTilemapColliders", nameof(ClearTilemapColliders));
        Reg("aegis.tilemapColliderCount",  nameof(TilemapColliderCount));
        Reg("aegis.mapObjects",      nameof(MapObjects));
        Reg("aegis.mapObjectsByType", nameof(MapObjectsByType));
        Reg("aegis.spawnMapObjects", nameof(SpawnMapObjects));
        Reg("aegis.newNavGrid",      nameof(NewNavGrid));
        Reg("aegis.navFromTilemap",  nameof(NavFromTilemap));
        Reg("aegis.navFindPath",     nameof(NavFindPath));
        Reg("aegis.navSetSolid",     nameof(NavSetSolid));
        Reg("aegis.navIsSolid",      nameof(NavIsSolid));
        Reg("aegis.perlin",          nameof(Perlin));
        Reg("aegis.registerScene",   nameof(RegisterScene));
        Reg("aegis.loadScene",       nameof(LoadScene));
        Reg("aegis.transitionTo",    nameof(TransitionTo));
        Reg("aegis.pushScene",       nameof(PushScene));
        Reg("aegis.popScene",        nameof(PopScene));
        Reg("aegis.sceneData",       nameof(SceneData));
        Reg("aegis.onSceneEnter",    nameof(OnSceneEnter));
        Reg("aegis.onSceneExit",     nameof(OnSceneExit));
        Reg("aegis.newAreaTrigger",  nameof(NewAreaTrigger));
        Reg("aegis.onTriggerEnter",  nameof(OnTriggerEnter));
        Reg("aegis.onTriggerStay",   nameof(OnTriggerStay));
        Reg("aegis.onTriggerExit",   nameof(OnTriggerExit));
        Reg("aegis.checkTrigger",    nameof(CheckTrigger));
        Reg("aegis.clearTriggers",   nameof(ClearTriggers));

        // ── v0.8 Save/Config/Effects/Debug ───────────────────────
        Reg("aegis.save",            nameof(Save));
        Reg("aegis.load",            nameof(Load));
        Reg("aegis.loadConfig",      nameof(LoadConfig));
        Reg("aegis.setFullscreen",   nameof(SetFullscreen));
        Reg("aegis.setDisplayMode",  nameof(SetDisplayMode));
        Reg("aegis.setResolution",   nameof(SetResolution));
        Reg("aegis.burst",           nameof(Burst));
        Reg("aegis.newEmitter",      nameof(NewEmitter));
        Reg("aegis.stopEmitter",     nameof(StopEmitter));
        Reg("aegis.tween",           nameof(Tween));
        Reg("aegis.newSequence",     nameof(NewSequence));
        Reg("aegis.seqAdd",          nameof(SeqAdd));
        Reg("aegis.seqWait",         nameof(SeqWait));
        Reg("aegis.seqPlay",         nameof(SeqPlay));
        Reg("aegis.seqStop",         nameof(SeqStop));
        Reg("aegis.fadeIn",          nameof(FadeIn));
        Reg("aegis.fadeOut",         nameof(FadeOut));
        Reg("aegis.flashScreen",     nameof(FlashScreen));
        Reg("aegis.setHotReload",    nameof(SetHotReload));
        Reg("aegis.setShader",       nameof(SetShader));
        Reg("aegis.clearShader",     nameof(ClearShader));
        Reg("aegis.setScreenShader", nameof(SetScreenShader));
        Reg("aegis.clearScreenShader", nameof(ClearScreenShader));

        // ── Sprint 2 — APIs de jogo urgentes ─────────────────────
        Reg("aegis.getTime",         nameof(GetTime));
        Reg("aegis.lookAt",          nameof(LookAt));
        Reg("aegis.overlapCircle",   nameof(OverlapCircle));
        Reg("aegis.overlapRect",     nameof(OverlapRect));
        Reg("aegis.newButton",       nameof(NewButton));
        Reg("aegis.onHover",         nameof(OnHover));
        Reg("aegis.onPress",         nameof(OnPress));
        Reg("aegis.floatText",       nameof(FloatText));
        RegLegacy("aegis.newProgressBar",  nameof(NewProgressBar));
        Reg("aegis.setBarValue",     nameof(SetBarValue));
        Reg("aegis.setBarColors",    nameof(SetBarColors));

        // ── Sprint 3 — Física e gameplay ─────────────────────────
        Reg("aegis.isTouchingWall",  nameof(IsTouchingWall));
        Reg("aegis.wallSide",        nameof(WallSide));
        Reg("aegis.newPool",         nameof(NewPool));
        Reg("aegis.poolGet",         nameof(PoolGet));
        Reg("aegis.poolReturn",      nameof(PoolReturn));
        Reg("aegis.poolClear",       nameof(PoolClear));
        Reg("aegis.poolCount",       nameof(PoolCount));

        // ── Drag & Drop ──────────────────────────────────────────────────
        RegExperimental("aegis.newDraggable",    nameof(NewDraggable));
        RegExperimental("aegis.onDragStart",     nameof(OnDragStart));
        RegExperimental("aegis.onDragMove",      nameof(OnDragMove));
        RegExperimental("aegis.onDragEnd",       nameof(OnDragEnd));
        RegExperimental("aegis.getDragTarget",   nameof(GetDragTarget));

        // ── Z-order dinâmico ─────────────────────────────────────────────
        Reg("aegis.bringToFront",    nameof(BringToFront));
        Reg("aegis.sendToBack",      nameof(SendToBack));
        Reg("aegis.setZRelative",    nameof(SetZRelative));

        // ── Hand / Card Layout ───────────────────────────────────────────
        RegExperimental("aegis.newHand",         nameof(NewHand));
        RegExperimental("aegis.handAdd",         nameof(HandAdd));
        RegExperimental("aegis.handRemove",      nameof(HandRemove));
        RegExperimental("aegis.handLayout",      nameof(HandLayout));
        RegExperimental("aegis.handSetHover",    nameof(HandSetHover));

        // ── Camera Autozoom ──────────────────────────────────────────────
        RegExperimental("aegis.setCameraAutozoom", nameof(SetCameraAutozoom));

        // ── Upgrade / Skill Tree ─────────────────────────────────────────
        RegExperimental("aegis.addUpgrade",      nameof(AddUpgrade));
        RegExperimental("aegis.onUpgradeChosen", nameof(OnUpgradeChosen));
        RegExperimental("aegis.getUpgradeLevel", nameof(GetUpgradeLevel));
        RegExperimental("aegis.showUpgrades",    nameof(ShowUpgrades));
        RegExperimental("aegis.hideUpgrades",    nameof(HideUpgrades));

        // ── Audio Spatial 3D ─────────────────────────────────────────────
        RegExperimental("aegis.playSoundAt3D",   nameof(PlaySoundAt3D));
    }

    private sealed record SceneStackFrame(
        string Name,
        LuaFunction? Init,
        LuaFunction? Update,
        LuaFunction? Draw,
        LuaFunction? DrawUi,
        LuaFunction? OnEnter,
        LuaFunction? OnExit,
        LuaTable? Data,
        Object2D[] WorldChildren,
        Object2D[] UiChildren);

    private static T Require<T>(T? value, string apiName) where T : class
        => value ?? throw new ArgumentNullException(apiName, $"[Aegis|Lua] Argumento obrigatório nulo em {apiName}.");

    private static float RequireFinite(float value, string argumentName, float fallback = 0f)
    {
        if (float.IsFinite(value)) return value;
        AegisLog.Warn("Lua", $"Valor inválido em {argumentName}; usando {fallback}.");
        return fallback;
    }

    // ── Core ──────────────────────────────────────────────────────────
    public void SetHotReload(bool enabled) => HotReloadManager.Instance.Enabled = enabled;

    public void ReloadMainScript(string path)
    {
        ClearAll();
        _sceneStack.Clear();
        _lua["aegis_init"]    = null;
        _lua["aegis_update"]  = null;
        _lua["aegis_draw"]    = null;
        _lua["aegis_draw_ui"] = null;
        DoFileChecked(path, "reload");
        if (!HasFunction("aegis_init"))
            throw new InvalidOperationException("[Aegis|Lua] Função obrigatória aegis_init não encontrada após reload.");
        CallFunction("aegis_init");
        InputManager.HardSyncFromHardware();
    }

    public void ExecuteFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"[Aegis] Script não encontrado: '{full}'");
        _gameRoot = Directory.GetCurrentDirectory();
        _mainLuaFullPath = full;
        DoFileChecked(full, "boot");
    }

    /// <summary>HOT_RELOAD vindo do Aegis Editor: recarrega module ou main.lua dentro da pasta do jogo.</summary>
    public void EditorHotReloadFile(string relativeOrGameRelativePath)
    {
        var root = Path.GetFullPath(_gameRoot);
        var safe = relativeOrGameRelativePath.Replace('\\', '/').TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(root, safe));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"[Aegis|Scene] Script fora da pasta do jogo: '{relativeOrGameRelativePath}'");

        if (!File.Exists(full))
            throw new FileNotFoundException($"[Aegis|EditorIPC] Script não encontrado: '{full}'");

        if (string.Equals(full, _mainLuaFullPath, StringComparison.OrdinalIgnoreCase))
            ReloadMainScript(full);
        else
            DoFileChecked(full, "hot reload");
    }

    public void LoadSceneFile(string path)
    {
        var root = Path.GetFullPath(_gameRoot);
        var safePath = path.Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, safePath));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"[Aegis|Scene] Cena fora da pasta do jogo: '{path}'");
        if (!File.Exists(full))
            throw new FileNotFoundException($"[Aegis|Scene] Cena não encontrada: '{full}'");

        ClearAll();
        _sceneStack.Clear();
        _lua["aegis_init"]    = null;
        _lua["aegis_update"]  = null;
        _lua["aegis_draw"]    = null;
        _lua["aegis_draw_ui"] = null;
        _onSceneEnter = null;
        _onSceneExit = null;
        DoFileChecked(full, $"load scene '{path}'");
        if (!HasFunction("aegis_init"))
            throw new InvalidOperationException($"[Aegis|Lua] Função obrigatória aegis_init não encontrada na cena: {path}");
        CallFunction("aegis_init");
    }

    internal void PushSceneFile(string name, string path, LuaTable? data)
    {
        var root = Path.GetFullPath(_gameRoot);
        var safePath = path.Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, safePath));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"[Aegis|Scene] Cena fora da pasta do jogo: '{path}'");
        if (!File.Exists(full))
            throw new FileNotFoundException($"[Aegis|Scene] Cena nao encontrada: '{full}'");

        var frame = new SceneStackFrame(
            name,
            _lua["aegis_init"] as LuaFunction,
            _lua["aegis_update"] as LuaFunction,
            _lua["aegis_draw"] as LuaFunction,
            _lua["aegis_draw_ui"] as LuaFunction,
            _onSceneEnter,
            _onSceneExit,
            _sceneData,
            _app.S2D?.Children.ToArray() ?? Array.Empty<Object2D>(),
            _app.Ui2D?.Children.ToArray() ?? Array.Empty<Object2D>());

        _sceneStack.Push(frame);
        _sceneData = data;
        _onSceneEnter = null;
        _onSceneExit = null;
        _lua["aegis_init"] = null;
        _lua["aegis_update"] = null;
        _lua["aegis_draw"] = null;
        _lua["aegis_draw_ui"] = null;

        DoFileChecked(full, $"push scene '{name}'");
        if (!HasFunction("aegis_init"))
            throw new InvalidOperationException($"[Aegis|Lua] Funcao obrigatoria aegis_init nao encontrada na cena: {path}");

        CallFunction("aegis_init");
        NotifySceneEnter(name, data);
        InputManager.HardSyncFromHardware();
    }

    internal bool PopSceneFrame()
    {
        if (_sceneStack.Count == 0) return false;

        var frame = _sceneStack.Pop();
        NotifySceneExit("overlay", frame.Name, _sceneData);
        RemoveChildrenCreatedAfterPush(_app.S2D, frame.WorldChildren);
        RemoveChildrenCreatedAfterPush(_app.Ui2D, frame.UiChildren);

        _lua["aegis_init"] = frame.Init;
        _lua["aegis_update"] = frame.Update;
        _lua["aegis_draw"] = frame.Draw;
        _lua["aegis_draw_ui"] = frame.DrawUi;
        _onSceneEnter = frame.OnEnter;
        _onSceneExit = frame.OnExit;
        _sceneData = frame.Data;
        InputManager.HardSyncFromHardware();
        return true;
    }

    private static void RemoveChildrenCreatedAfterPush(Scene2D? scene, Object2D[] kept)
    {
        if (scene is null) return;
        var keep = new HashSet<Object2D>(kept);
        foreach (var child in scene.Children.ToArray())
        {
            if (!keep.Contains(child))
                child.RemoveFromParent();
        }
    }

    public void ExecuteString(string code)
    {
        try
        {
            _lua.DoString(code);
        }
        catch (Exception ex) when (IsLuaException(ex))
        {
            throw new InvalidOperationException(BuildLuaErrorMessage("execute string", "<string>", ex), ex);
        }
    }

    public bool HasFunction(string name) => _lua[name] is LuaFunction;

    public object? GetGlobal(string name) => _lua[name];

    public void CallFunction(string name, params object[] args)
    {
        if (_lua[name] is not LuaFunction fn) return;

        try
        {
            fn.Call(args);
        }
        catch (Exception ex) when (IsLuaException(ex))
        {
            throw new InvalidOperationException(BuildLuaErrorMessage($"call {name}", _mainLuaFullPath, ex), ex);
        }
    }

    internal object[] DoFileChecked(string fullPath, string phase)
    {
        try
        {
            return _lua.DoFile(fullPath);
        }
        catch (Exception ex) when (IsLuaException(ex))
        {
            throw new InvalidOperationException(BuildLuaErrorMessage(phase, fullPath, ex), ex);
        }
    }

    private static bool IsLuaException(Exception ex)
        => ex.GetType().FullName?.StartsWith("NLua.", StringComparison.Ordinal) == true
           || ex.GetType().FullName?.StartsWith("KeraLua.", StringComparison.Ordinal) == true;

    private static string BuildLuaErrorMessage(string phase, string file, Exception ex)
    {
        var raw = ex.Message.Replace("\r\n", "\n").Trim();
        var tip = LuaErrorTip(raw);
        var path = string.IsNullOrWhiteSpace(file) ? "<unknown>" : file;

        return "[Aegis|Lua] Erro no script.\n" +
               $"  Fase: {phase}\n" +
               $"  Arquivo: {path}\n" +
               $"  Erro: {raw}\n" +
               $"  Dica: {tip}";
    }

    private static string LuaErrorTip(string message)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("attempt to call a nil value"))
            return "voce chamou uma funcao que nao existe. Confira o nome da API, exemplo: aegis.create(...) em vez de funcao global antiga.";
        if (lower.Contains("attempt to index a nil value"))
            return "algum objeto/tabela esta nil. Confira se o asset, entidade ou retorno de funcao foi criado antes de usar.";
        if (lower.Contains("asset nao encontrado") || lower.Contains("texture not found") || lower.Contains("file not found"))
            return "confira o caminho do arquivo dentro de res/. Use aegis.asset(\"sprites/player.png\") para validar cedo.";
        if (lower.Contains("syntax") || lower.Contains("unexpected symbol") || lower.Contains("near"))
            return "ha erro de sintaxe Lua perto da linha indicada. Confira parenteses, end, virgulas e aspas.";

        return "abra o arquivo indicado, confira a linha do erro e rode aegis doctor <jogo> para validar assets e estrutura.";
    }


    private static float TableFloat(LuaTable? table, string key, float fallback)
    {
        try { return table?[key] is null ? fallback : Convert.ToSingle(table[key]); }
        catch { return fallback; }
    }

    private static bool TableBool(LuaTable? table, string key, bool fallback)
    {
        try { return table?[key] is null ? fallback : Convert.ToBoolean(table[key]); }
        catch { return fallback; }
    }


    private static string TableString(LuaTable? table, string key, string fallback)
    {
        try { return table?[key]?.ToString() ?? fallback; }
        catch { return fallback; }
    }

    private static int[] ReadIntArray(LuaTable? table)
    {
        if (table is null) return Array.Empty<int>();
        var list = new List<int>();
        foreach (var value in table.Values)
        {
            try { list.Add(Convert.ToInt32(value)); }
            catch { }
        }
        return list.ToArray();
    }

    public void Dispose() => _lua.Dispose();
}
