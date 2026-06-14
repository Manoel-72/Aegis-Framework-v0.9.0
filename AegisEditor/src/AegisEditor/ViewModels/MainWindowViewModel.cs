using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using AegisEditor.Services;
using AegisEditor.Shared.Messages;
using AegisEditor.Shared.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AegisEditor.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IEditorBridgeClient _bridge;
    private readonly IEditorLogSink _log;
    private readonly ISceneSerializer _sceneSerializer;
    private readonly IRuntimeLauncher _runtimeLauncher;
    private readonly IUiThreadScheduler _ui;
    private readonly string _recentProjectsFile;
    private SceneState _currentScene = SceneState.CreateDefault2D();
    private CancellationTokenSource? _autoSaveCts;
    private string? _activeRuntimeScenePath;
    private bool _applyingScene;
    private bool _applyingHistory;
    private readonly Stack<IEditorAction> _undoStack = new();
    private readonly Stack<IEditorAction> _redoStack = new();

    public HierarchyViewModel Hierarchy { get; }

    public InspectorViewModel Inspector { get; }

    public ViewportViewModel Viewport { get; }

    public AssetBrowserViewModel AssetBrowser { get; }

    public LuaEditorViewModel LuaEditor { get; }

    public ConsoleViewModel ConsoleLog { get; }

    public MainWindowViewModel(
        HierarchyViewModel hierarchy,
        InspectorViewModel inspector,
        ViewportViewModel viewport,
        AssetBrowserViewModel assetBrowser,
        LuaEditorViewModel luaEditor,
        ConsoleViewModel consoleLog,
        IEditorBridgeClient bridge,
        IEditorLogSink log,
        ISceneSerializer sceneSerializer,
        IRuntimeLauncher runtimeLauncher,
        IUiThreadScheduler ui)
    {
        Hierarchy = hierarchy;
        Inspector = inspector;
        Viewport = viewport;
        AssetBrowser = assetBrowser;
        LuaEditor = luaEditor;
        ConsoleLog = consoleLog;
        _bridge = bridge;
        _log = log;
        _sceneSerializer = sceneSerializer;
        _runtimeLauncher = runtimeLauncher;
        _ui = ui;
        _recentProjectsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AegisEditor",
            "recent-projects.json");

        Hierarchy.SelectedEntityChanged += (_, entity) =>
        {
            Inspector.ApplySelection(entity);
            if (!ReferenceEquals(Viewport.SelectedEntity, entity))
                Viewport.SelectedEntity = entity;
        };

        Viewport.SelectedEntityChanged += (_, entity) =>
        {
            if (!ReferenceEquals(Hierarchy.SelectedEntity, entity))
                Hierarchy.SelectedEntity = entity;
            Inspector.ApplySelection(entity);
        };

        AssetBrowser.SpriteCreateRequested += (_, entity) => AddEntityToScene(entity);
        Hierarchy.CreateEntityRequested += (_, type) => AddEntityToScene(CreateEditorEntity(type));
        Hierarchy.DeleteEntityRequested += (_, entity) => RemoveEntitiesFromScene([entity], recordUndo: true);
        Viewport.SpriteDropRequested += (_, drop) =>
            AddEntityToScene(AssetBrowser.CreateSpriteEntity(drop.TexturePath, drop.X, drop.Y));
        Viewport.DeleteEntitiesRequested += (_, entities) => RemoveEntitiesFromScene(entities, recordUndo: true);
        Viewport.TransformCommitted += (_, commit) => RecordTransform(commit);
        Inspector.EditCommitted += (_, commit) => RecordInspectorEdit(commit);

        bridge.MessageReceived += OnRuntimeInbound;
        runtimeLauncher.RuntimeExited += (_, _) => DeleteActiveRuntimeScene();

        var detectedRepo = AegisEngineLocator.FindRepoRootContainingCli();
        var demoNear = detectedRepo is not null ? AegisEngineLocator.DefaultDemoNearRepo(detectedRepo) : null;
        if (demoNear is not null)
            GamePreviewFolder = demoNear;

        LoadRecentProjects();
    }

    public ObservableCollection<string> RecentProjects { get; } = [];

    [ObservableProperty]
    private bool _isHubVisible = true;

    [ObservableProperty]
    private bool _isWorkspaceVisible;

    [ObservableProperty]
    private string _currentProjectName = "Nenhum projeto aberto";

    [ObservableProperty]
    private string _hubStatus = "Escolha uma acao para comecar.";

    [ObservableProperty]
    private string _sceneProjectPath = string.Empty;

    /// <summary>Pasta do jogo (onde está main.lua); preenchido com demo-platformer se existir ao lado da engine.</summary>
    [ObservableProperty]
    private string _gamePreviewFolder = string.Empty;

    /// <summary>Raiz do repositório com <c>src/Aegis.CLI</c>; deixar vazio para detetar a partir da pasta do editor.</summary>
    [ObservableProperty]
    private string _engineRepoOverride = string.Empty;

    public void OpenProject(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        if (!Directory.Exists(fullPath))
        {
            HubStatus = "Pasta de projeto nao encontrada.";
            _log.Post(EditorLogLevel.Error, $"Pasta de projeto nao encontrada: {fullPath}");
            return;
        }

        if (!File.Exists(Path.Combine(fullPath, "main.lua")))
        {
            HubStatus = "A pasta escolhida nao parece ser um jogo Aegis.";
            _log.Post(EditorLogLevel.Warning, $"Sem main.lua em: {fullPath}");
            return;
        }

        GamePreviewFolder = fullPath;
        Directory.CreateDirectory(Path.Combine(fullPath, "scripts"));
        Viewport.ProjectRoot = fullPath;
        CurrentProjectName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        IsHubVisible = false;
        IsWorkspaceVisible = true;
        EnsureDefaultScene(fullPath);
        EnsureEditorProjectBootScene(fullPath);
        _ = AssetBrowser.OpenProjectAsync(fullPath);
        HubStatus = $"Projeto aberto: {CurrentProjectName}";
        AddRecentProject(fullPath);
        _log.Post(EditorLogLevel.Info, $"Projeto aberto: {fullPath}");
    }

    [RelayCommand]
    private void BackToHub()
    {
        IsWorkspaceVisible = false;
        IsHubVisible = true;
        HubStatus = "Escolha uma acao para comecar.";
    }

    [RelayCommand]
    private void NewProject()
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AegisProjects");
            Directory.CreateDirectory(root);

            var baseName = "NovoJogo";
            var project = Path.Combine(root, baseName);
            var index = 1;
            while (Directory.Exists(project))
                project = Path.Combine(root, baseName + index++);

            CreateMinimalProject(project);
            OpenProject(project);
            HubStatus = $"Novo projeto criado: {Path.GetFileName(project)}";
        }
        catch (Exception ex)
        {
            HubStatus = "Nao foi possivel criar o projeto.";
            _log.Post(EditorLogLevel.Error, $"Falha ao criar projeto: {ex.Message}");
        }
    }

    [RelayCommand]
    private void DeleteSelectedEntity()
    {
        var selected = Viewport.SelectedEntities.Count > 0
            ? Viewport.SelectedEntities.ToArray()
            : (Hierarchy.SelectedEntity ?? Viewport.SelectedEntity) is { } entity
                ? [entity]
                : [];

        RemoveEntitiesFromScene(selected, recordUndo: true);
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            _log.Post(EditorLogLevel.Info, "Nada para desfazer.");
            return;
        }

        var action = _undoStack.Pop();
        _applyingHistory = true;
        try
        {
            action.Undo(this);
            _redoStack.Push(action);
        }
        finally
        {
            _applyingHistory = false;
        }

        Viewport.NotifyRedraw();
        ScheduleAutoSave(syncRuntime: true);
        _log.Post(EditorLogLevel.Info, $"Undo: {action.Name}");
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            _log.Post(EditorLogLevel.Info, "Nada para refazer.");
            return;
        }

        var action = _redoStack.Pop();
        _applyingHistory = true;
        try
        {
            action.Redo(this);
            _undoStack.Push(action);
        }
        finally
        {
            _applyingHistory = false;
        }

        Viewport.NotifyRedraw();
        ScheduleAutoSave(syncRuntime: true);
        _log.Post(EditorLogLevel.Info, $"Redo: {action.Name}");
    }

    [RelayCommand]
    private void ResetEditorView()
        => Viewport.ResetViewCommand.Execute(null);

    [RelayCommand]
    private void ToggleSnap()
        => Viewport.ToggleSnapCommand.Execute(null);

    [RelayCommand]
    private void ValidateAssets()
        => AssetBrowser.ValidateProjectCommand.Execute(null);

    [RelayCommand]
    private async Task NewSceneAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(GamePreviewFolder))
        {
            _log.Post(EditorLogLevel.Warning, "Abra um projeto antes de criar uma cena.");
            return;
        }

        try
        {
            var scenePath = DefaultScenePath(GamePreviewFolder);
            SceneProjectPath = scenePath;
            _currentScene = SceneState.CreateDefault2D("Main");
            await _sceneSerializer.SaveAsync(scenePath, _currentScene, cancellationToken).ConfigureAwait(true);
            ApplyScene(_currentScene);
            _log.Post(EditorLogLevel.Info, $"Cena criada: {scenePath}");
        }
        catch (Exception ex)
        {
            _log.Post(EditorLogLevel.Error, $"Falha ao criar cena: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenExample()
    {
        var repo = !string.IsNullOrWhiteSpace(EngineRepoOverride)
            ? Path.GetFullPath(EngineRepoOverride.Trim())
            : AegisEngineLocator.FindRepoRootContainingCli();

        var demo = repo is not null ? AegisEngineLocator.DefaultDemoNearRepo(repo) : null;
        if (demo is null)
        {
            HubStatus = "Nao encontrei examples/demo-platformer perto da engine.";
            _log.Post(EditorLogLevel.Warning, "Exemplo demo-platformer nao encontrado.");
            return;
        }

        OpenProject(demo);
    }

    [RelayCommand]
    private void OpenRecentProject(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return;

        OpenProject(projectPath);
    }

    [RelayCommand]
    private void OpenDocumentation()
    {
        var repo = AegisEngineLocator.FindRepoRootContainingCli();
        var docsPath = repo is not null ? Path.Combine(repo, "docs", "GUIA_CURSO_AEGIS_ENGINE_VERSAO_ATUAL.md") : null;
        if (docsPath is null || !File.Exists(docsPath))
        {
            HubStatus = "Documentacao nao encontrada.";
            _log.Post(EditorLogLevel.Warning, "Documentacao principal nao encontrada.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = docsPath,
                UseShellExecute = true
            });
            HubStatus = "Documentacao aberta.";
        }
        catch (Exception ex)
        {
            HubStatus = "Nao foi possivel abrir a documentacao.";
            _log.Post(EditorLogLevel.Error, $"Falha ao abrir documentacao: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RunGameAndConnectAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(GamePreviewFolder))
        {
            _log.Post(EditorLogLevel.Warning, "Define a pasta do jogo (ex.: demo-platformer) antes de Correr.");
            return;
        }

        var repo = !string.IsNullOrWhiteSpace(EngineRepoOverride)
            ? Path.GetFullPath(EngineRepoOverride.Trim())
            : AegisEngineLocator.FindRepoRootContainingCli();

        if (repo is null || !Directory.Exists(repo))
        {
            _log.Post(EditorLogLevel.Error,
                "Não encontrei a raíz da engine (ficheiro src/Aegis.CLI/Aegis.CLI.csproj). Define «raíz motor» manualmente.");
            return;
        }

        var csproj = Path.Combine(repo, "src", "Aegis.CLI", "Aegis.CLI.csproj");
        if (!File.Exists(csproj))
        {
            _log.Post(EditorLogLevel.Error,
                $"CLI não encontrado: {csproj}. Ajuste a pasta «raíz motor».");
            return;
        }

        var gameDir = Path.GetFullPath(GamePreviewFolder.Trim());
        if (!Directory.Exists(gameDir))
        {
            _log.Post(EditorLogLevel.Error, $"Pasta do jogo não existe: {gameDir}");
            return;
        }

        if (!File.Exists(Path.Combine(gameDir, "main.lua")))
        {
            _log.Post(EditorLogLevel.Error,
                $"Sem main.lua em {gameDir}. Escolha a pasta certa do jogo.");
            return;
        }

        await SaveCurrentSceneAsync(cancellationToken, syncRuntime: false, logSuccess: false).ConfigureAwait(true);
        var activeScenePath = await SaveActiveRuntimeSceneAsync(cancellationToken).ConfigureAwait(true);
        var activeSceneRelativePath = Path.GetRelativePath(gameDir, activeScenePath).Replace('\\', '/');

        if (_bridge.IsConnected)
        {
            await SendSceneLoadToRuntimeAsync(cancellationToken).ConfigureAwait(true);
            await _bridge.SendLineAsync(EditorCommand.PlayLine(), cancellationToken).ConfigureAwait(true);
            _log.Post(EditorLogLevel.Info, "Runtime ja estava aberto; cena salva e recarregada.");
            return;
        }

        var usedPrebuiltCli = false;
        if (!_runtimeLauncher.IsRuntimeRunning)
        {
            try
            {
                await _bridge.DisconnectAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _log.Post(EditorLogLevel.Warning, $"Disconnect antes do run: {ex.Message}");
            }

            var prebuiltCli = AegisEngineLocator.FindPrebuiltAegisCliExecutable(repo);

            string? errMsg;
            bool start;
            if (prebuiltCli is not null)
            {
                usedPrebuiltCli = true;
                _log.Post(EditorLogLevel.Info,
                    $"A arrancar aegis-cli compilado ({Path.GetFileName(prebuiltCli)}); WD = pasta do jogo.");

                start = _runtimeLauncher.TryStartDetached(
                    new RuntimeLaunchArguments(
                        ExecutablePath: prebuiltCli,
                        WorkingDirectory: gameDir,
                        ArgumentList: ["run", gameDir, "--editor-pipe", "--scene", activeSceneRelativePath]),
                    out errMsg);
            }
            else
            {
                _log.Post(EditorLogLevel.Warning,
                    "Sem exe em src/Aegis.CLI/bin/*/net8.0. Executa primeiro: dotnet build src/Aegis.CLI. Alternativa: dotnet run desde o utilizador.");

                start = _runtimeLauncher.TryStartDetached(
                    new RuntimeLaunchArguments(
                        ExecutablePath: "dotnet",
                        WorkingDirectory: repo,
                        ArgumentList:
                        [
                            "run",
                            "--project",
                            csproj,
                            "--",
                            "run",
                            gameDir,
                            "--editor-pipe",
                            "--scene",
                            activeSceneRelativePath,
                        ]),
                    out errMsg);
            }

            if (!start)
            {
                _log.Post(EditorLogLevel.Error, errMsg ?? "Falha ao iniciar o processo do jogo.");
                return;
            }
        }
        else
        {
            _log.Post(EditorLogLevel.Info, "Runtime ja esta rodando; tentando reconectar sem abrir outra janela.");
        }

        _log.Post(EditorLogLevel.Info,
            "Aguardando o runtime (se fechar já, aparece código de saída no consola abaixo).");

        var warmUpMs = usedPrebuiltCli ? 2000 : 5000;
        await Task.Delay(warmUpMs, cancellationToken).ConfigureAwait(true);

        for (var attempt = 0; attempt < 24; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                await _bridge.ConnectAsync(cancellationToken).ConfigureAwait(true);
                if (_bridge.IsConnected)
                {
                    _log.Post(EditorLogLevel.Info, $"Ligado ao runtime ({attempt + 1} tentativa(s)).");
                    await SendSceneLoadToRuntimeAsync(cancellationToken).ConfigureAwait(true);
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.Post(EditorLogLevel.Warning, $"Connect tentativa {attempt + 1}: {ex.Message}");
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(true);
        }

        _log.Post(EditorLogLevel.Warning,
            "Não ligou ao pipe. Confirma que o jogo continua aberto; se terminou, vê a linha «Processo do runtime terminou» acima.");
    }

    [RelayCommand]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _bridge.ConnectAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Post(EditorLogLevel.Error, $"Connect failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _bridge.DisconnectAsync(cancellationToken).ConfigureAwait(true);
            _log.Post(EditorLogLevel.Info, "Disconnected from runtime pipe.");
        }
        catch (Exception ex)
        {
            _log.Post(EditorLogLevel.Warning, $"Disconnect failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task LoadSceneAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(SceneProjectPath))
        {
            _log.Post(EditorLogLevel.Warning, "Set a scene JSON path before loading.");
            return;
        }

        try
        {
            var normalized = SceneProjectPath.Trim();
            var state = await _sceneSerializer.LoadAsync(normalized, cancellationToken).ConfigureAwait(true);
            _currentScene = state;
            ApplyScene(state);

            if (_bridge.IsConnected)
                await SendSceneLoadToRuntimeAsync(cancellationToken).ConfigureAwait(true);
            else
                _log.Post(EditorLogLevel.Warning, "Scene loaded locally; runtime not connected (SCENE_LOAD not sent).");
        }
        catch (Exception ex)
        {
            _log.Post(EditorLogLevel.Error, $"Load scene failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveSceneAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(SceneProjectPath))
        {
            _log.Post(EditorLogLevel.Warning, "Defina um caminho .scene.json antes de salvar.");
            return;
        }

        try
        {
            await SaveCurrentSceneAsync(cancellationToken, syncRuntime: true, logSuccess: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Post(EditorLogLevel.Error, $"Falha ao salvar cena: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task PlayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SendWhenConnected(EditorCommand.PlayLine(), cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Post(EditorLogLevel.Error, ex.Message);
        }
    }

    [RelayCommand]
    private async Task PauseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SendWhenConnected(EditorCommand.PauseLine(), cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Post(EditorLogLevel.Error, ex.Message);
        }
    }

    [RelayCommand]
    private async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_bridge.IsConnected)
            {
                await _bridge.SendLineAsync(EditorCommand.StopLine(), cancellationToken).ConfigureAwait(true);
                await _bridge.DisconnectAsync(cancellationToken).ConfigureAwait(true);
            }

            if (!_runtimeLauncher.TryStopRuntime(out var error))
                _log.Post(EditorLogLevel.Warning, error ?? "Nao foi possivel parar o runtime.");

            DeleteActiveRuntimeScene();
        }
        catch (Exception ex)
        {
            _log.Post(EditorLogLevel.Error, ex.Message);
        }
    }

    [RelayCommand]
    private async Task RestartRuntimeAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(true);
        await Task.Delay(400, cancellationToken).ConfigureAwait(true);
        await RunGameAndConnectAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task HotReloadLuaAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SendWhenConnected(
                EditorCommand.HotReloadLine("scripts/player.lua"),
                cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Post(EditorLogLevel.Error, ex.Message);
        }
    }

    private async Task SendWhenConnected(string line, CancellationToken cancellationToken)
    {
        if (!_bridge.IsConnected)
        {
            _log.Post(EditorLogLevel.Warning, "Not connected.");
            return;
        }

        await _bridge.SendLineAsync(line, cancellationToken).ConfigureAwait(true);
    }

    private void OnRuntimeInbound(object? sender, RuntimeInboundEnvelope e)
    {
        try
        {
            var opts = IpcSerializerOptions.Create();
            switch (e.Type)
            {
                case RuntimeEvent.SceneState:
                    var scene = JsonSerializer.Deserialize<SceneStatePayload>(e.Payload, opts);
                    if (scene is not null)
                    {
                        if (string.IsNullOrWhiteSpace(SceneProjectPath))
                        {
                            Hierarchy.ApplyScenePayload(scene);
                            Viewport.ApplyScenePayload(scene);
                        }
                    }

                    break;

                case RuntimeEvent.EntityUpdated:
                    var eu = JsonSerializer.Deserialize<EntityUpdatedPayload>(e.Payload, opts);
                    if (eu is not null)
                    {
                        foreach (var entity in Hierarchy.Entities)
                        {
                            if (entity.Id != eu.Id) continue;
                            entity.X = eu.X;
                            entity.Y = eu.Y;
                            break;
                        }

                        foreach (var entity in Viewport.Entities)
                        {
                            if (entity.Id != eu.Id) continue;
                            entity.X = eu.X;
                            entity.Y = eu.Y;
                            break;
                        }

                        Viewport.NotifyRedraw();
                    }

                    break;

                case RuntimeEvent.Log:
                    var logPayload = JsonSerializer.Deserialize<LogPayload>(e.Payload, opts);
                    if (logPayload is null) break;

                    var level = logPayload.Level.Trim().Equals("warn", StringComparison.OrdinalIgnoreCase)
                        ? EditorLogLevel.Warning
                        : logPayload.Level.Trim().Equals("error", StringComparison.OrdinalIgnoreCase)
                            ? EditorLogLevel.Error
                            : EditorLogLevel.Info;
                    _log.Post(level, logPayload.Message);
                    break;

                case RuntimeEvent.Connected:
                    var connected = JsonSerializer.Deserialize<ConnectedPayload>(e.Payload, opts);
                    if (connected is not null)
                        _log.Post(EditorLogLevel.Info, $"CONNECTED runtime {connected.RuntimeVersion}");

                    break;

                case RuntimeEvent.Error:
                    var err = JsonSerializer.Deserialize<ErrorPayload>(e.Payload, opts);
                    if (err is not null)
                        _log.Post(EditorLogLevel.Error, err.Message);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Post(EditorLogLevel.Error, $"IPC parse failure ({e.Type}): {ex.Message}");
        }
    }

    private void LoadRecentProjects()
    {
        try
        {
            if (!File.Exists(_recentProjectsFile))
                return;

            var items = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_recentProjectsFile));
            if (items is null) return;

            foreach (var item in items.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
                RecentProjects.Add(item);
        }
        catch (Exception ex)
        {
            _log.Post(EditorLogLevel.Warning, $"Nao foi possivel carregar projetos recentes: {ex.Message}");
        }
    }

    private void EnsureDefaultScene(string projectPath)
    {
        try
        {
            var scenePath = DefaultScenePath(projectPath);
            SceneProjectPath = scenePath;

            if (!File.Exists(scenePath))
            {
                _currentScene = SceneState.CreateDefault2D("Main");
                _sceneSerializer.SaveAsync(scenePath, _currentScene).GetAwaiter().GetResult();
                _log.Post(EditorLogLevel.Info, $"Cena padrao criada: {scenePath}");
            }
            else
            {
                _currentScene = _sceneSerializer.LoadAsync(scenePath).GetAwaiter().GetResult();
                _log.Post(EditorLogLevel.Info, $"Cena padrao carregada: {scenePath}");
            }

            ApplyScene(_currentScene);
        }
        catch (Exception ex)
        {
            _log.Post(EditorLogLevel.Warning, $"Nao foi possivel preparar main.scene.json: {ex.Message}");
        }
    }

    private void EnsureEditorProjectBootScene(string projectPath)
    {
        var mainLua = Path.Combine(projectPath, "main.lua");
        if (!File.Exists(mainLua)) return;

        try
        {
            var text = File.ReadAllText(mainLua);
            if (text.Contains("aegis.loadScene(\"scenes/main.scene.json\")", StringComparison.Ordinal))
                return;

            var looksLikeOldEditorTemplate =
                text.Contains("Projeto Aegis criado pelo editor.", StringComparison.Ordinal)
                && text.Contains("aegis.newRect(aegis.screenWidth()", StringComparison.Ordinal);

            if (!looksLikeOldEditorTemplate)
                return;

            File.WriteAllText(mainLua, """
function aegis_init()
    aegis.log("Projeto Aegis criado pelo editor.")
    aegis.loadScene("scenes/main.scene.json")
end

function aegis_update(dt)
end

function aegis_draw()
end
""");
            _log.Post(EditorLogLevel.Info, "main.lua atualizado para carregar scenes/main.scene.json.");
        }
        catch (Exception ex)
        {
            _log.Post(EditorLogLevel.Warning, $"Nao foi possivel atualizar main.lua do projeto: {ex.Message}");
        }
    }

    private void ApplyScene(SceneState state)
    {
        _applyingScene = true;
        try
        {
            UnsubscribeSceneEntityChanges();
            Hierarchy.ApplySceneState(state);
            Viewport.ApplySceneState(state);
            SubscribeSceneEntityChanges();
        }
        finally
        {
            _applyingScene = false;
        }
    }

    private void AddEntityToScene(SceneEntityDto entity)
        => AddEntityToScene(entity, recordUndo: true);

    private void AddEntityToScene(SceneEntityDto entity, bool recordUndo)
    {
        if (recordUndo)
            RecordAction(new CreateEntityAction(CloneEntity(entity), Hierarchy.Entities.Count));

        _currentScene.Entities.Add(entity);
        entity.PropertyChanged += SceneEntity_PropertyChanged;
        Hierarchy.Entities.Add(entity);
        Viewport.AddEntity(entity);
        Hierarchy.SelectedEntity = entity;
        Viewport.NotifyRedraw();
        _log.Post(EditorLogLevel.Info, $"Entidade adicionada: {entity.Name}");
        ScheduleAutoSave(syncRuntime: true);
    }

    private void RemoveEntityFromScene(SceneEntityDto entity)
        => RemoveEntitiesFromScene([entity], recordUndo: true);

    private void RemoveEntitiesFromScene(IReadOnlyList<SceneEntityDto> entities, bool recordUndo)
    {
        if (entities.Count == 0)
            return;

        var unique = entities
            .Where(e => Hierarchy.Entities.Contains(e))
            .DistinctBy(e => e.Id)
            .ToArray();
        if (unique.Length == 0)
            return;

        if (recordUndo)
        {
            var snapshots = unique
                .Select(e => new DeletedEntitySnapshot(CloneEntity(e), Hierarchy.Entities.IndexOf(e)))
                .OrderBy(s => s.Index)
                .ToArray();
            RecordAction(new DeleteEntitiesAction(snapshots));
        }

        foreach (var entity in unique)
            RemoveEntityCore(entity);

        Viewport.NotifyRedraw();
        _log.Post(EditorLogLevel.Info, unique.Length == 1
            ? $"Entidade removida: {unique[0].Name}"
            : $"Entidades removidas: {unique.Length}");
        ScheduleAutoSave(syncRuntime: true);
    }

    private void RemoveEntityCore(SceneEntityDto entity)
    {
        entity.PropertyChanged -= SceneEntity_PropertyChanged;
        _currentScene.Entities.Remove(entity);
        Hierarchy.Entities.Remove(entity);
        Viewport.Entities.Remove(entity);
        Viewport.SelectedEntities.Remove(entity);
        if (ReferenceEquals(Hierarchy.SelectedEntity, entity))
            Hierarchy.SelectedEntity = null;
        if (ReferenceEquals(Viewport.SelectedEntity, entity))
            Viewport.SelectedEntity = null;
    }

    private void InsertEntityAt(SceneEntityDto entity, int index)
    {
        entity.PropertyChanged += SceneEntity_PropertyChanged;
        var safeIndex = Math.Clamp(index, 0, Hierarchy.Entities.Count);
        _currentScene.Entities.Insert(Math.Clamp(index, 0, _currentScene.Entities.Count), entity);
        Hierarchy.Entities.Insert(safeIndex, entity);
        Viewport.Entities.Insert(Math.Clamp(index, 0, Viewport.Entities.Count), entity);
        Viewport.SelectOnly(entity);
        Hierarchy.SelectedEntity = entity;
    }

    private void RecordTransform(EntityTransformCommit commit)
    {
        var before = commit.Before.ToArray();
        var after = commit.After.ToArray();
        if (before.Length != after.Length || before.Length == 0)
            return;

        if (!before.Zip(after).Any(pair => HasTransformChanged(pair.First, pair.Second)))
            return;

        RecordAction(new TransformEntitiesAction(before, after));
    }

    private void RecordInspectorEdit(InspectorEditCommit commit)
    {
        if (_applyingScene || _applyingHistory)
            return;

        if (!Hierarchy.Entities.Any(e => e.Id.Equals(commit.Before.Id, StringComparison.Ordinal)))
            return;

        RecordAction(new EditEntityAction(CloneEntity(commit.Before), CloneEntity(commit.After)));
        ScheduleAutoSave(syncRuntime: true);
    }

    private static bool HasTransformChanged(EntityTransform a, EntityTransform b)
        => !a.Id.Equals(b.Id, StringComparison.Ordinal)
           || Math.Abs(a.X - b.X) > 0.001f
           || Math.Abs(a.Y - b.Y) > 0.001f
           || Math.Abs(a.ScaleX - b.ScaleX) > 0.001f
           || Math.Abs(a.ScaleY - b.ScaleY) > 0.001f
           || Math.Abs(a.Rotation - b.Rotation) > 0.001f;

    private void RecordAction(IEditorAction action)
    {
        if (_applyingHistory)
            return;

        _undoStack.Push(action);
        _redoStack.Clear();
    }

    private void ApplyTransforms(IEnumerable<EntityTransform> transforms)
    {
        foreach (var transform in transforms)
        {
            var entity = Hierarchy.Entities.FirstOrDefault(e => e.Id.Equals(transform.Id, StringComparison.Ordinal));
            if (entity is null)
                continue;

            entity.X = transform.X;
            entity.Y = transform.Y;
            entity.ScaleX = transform.ScaleX;
            entity.ScaleY = transform.ScaleY;
            entity.Rotation = transform.Rotation;
        }
    }

    private void ApplyEntitySnapshot(SceneEntityDto snapshot)
    {
        var entity = Hierarchy.Entities.FirstOrDefault(e => e.Id.Equals(snapshot.Id, StringComparison.Ordinal));
        if (entity is null)
            return;

        entity.Name = snapshot.Name;
        entity.Type = snapshot.Type;
        entity.X = snapshot.X;
        entity.Y = snapshot.Y;
        entity.ScaleX = snapshot.ScaleX;
        entity.ScaleY = snapshot.ScaleY;
        entity.Rotation = snapshot.Rotation;
        entity.TexturePath = snapshot.TexturePath;
        entity.ScriptPath = snapshot.ScriptPath;
        entity.ParentId = snapshot.ParentId;
        entity.Children = snapshot.Children.ToList();
        entity.Components = snapshot.Components
            .Select(c => new ComponentDto
            {
                Type = c.Type,
                Properties = c.Properties.ToDictionary(p => p.Key, p => p.Value),
            })
            .ToList();

        Viewport.SelectOnly(entity);
        Hierarchy.SelectedEntity = entity;
        Inspector.ApplySelection(entity);
    }

    private static SceneEntityDto CloneEntity(SceneEntityDto entity)
    {
        var json = JsonSerializer.Serialize(entity);
        return JsonSerializer.Deserialize<SceneEntityDto>(json)
            ?? throw new InvalidOperationException("Falha ao clonar entidade.");
    }

    private void SceneEntity_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applyingScene || _applyingHistory) return;
        ScheduleAutoSave(syncRuntime: true);
    }

    private void SubscribeSceneEntityChanges()
    {
        foreach (var entity in Hierarchy.Entities)
            entity.PropertyChanged += SceneEntity_PropertyChanged;
    }

    private void UnsubscribeSceneEntityChanges()
    {
        foreach (var entity in Hierarchy.Entities)
            entity.PropertyChanged -= SceneEntity_PropertyChanged;
    }

    private void ScheduleAutoSave(bool syncRuntime)
    {
        if (string.IsNullOrWhiteSpace(SceneProjectPath)) return;

        try { _autoSaveCts?.Cancel(); }
        catch { /* ignore */ }
        _autoSaveCts?.Dispose();
        _autoSaveCts = new CancellationTokenSource();
        var token = _autoSaveCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token).ConfigureAwait(false);
                _ui.Post(() => _ = SaveCurrentSceneAsync(token, syncRuntime, logSuccess: false));
            }
            catch (OperationCanceledException)
            {
                /* superseded by newer edit */
            }
            catch (Exception ex)
            {
                _log.Post(EditorLogLevel.Error, $"Autosave falhou: {ex.Message}");
            }
        }, token);
    }

    private async Task SaveCurrentSceneAsync(CancellationToken cancellationToken, bool syncRuntime, bool logSuccess)
    {
        if (string.IsNullOrWhiteSpace(SceneProjectPath)) return;

        _currentScene.Entities = Hierarchy.Entities.ToList();
        _currentScene.Tilemaps = Viewport.Tilemaps.ToList();
        NormalizeSceneComponents(_currentScene);

        await _sceneSerializer.SaveAsync(SceneProjectPath.Trim(), _currentScene, cancellationToken)
            .ConfigureAwait(false);

        if (logSuccess)
            _log.Post(EditorLogLevel.Info, $"Cena salva: {SceneProjectPath}");

        if (!string.IsNullOrWhiteSpace(_activeRuntimeScenePath))
            await _sceneSerializer.SaveAsync(_activeRuntimeScenePath, _currentScene, cancellationToken)
                .ConfigureAwait(false);

        if (syncRuntime && _bridge.IsConnected)
            await SendSceneLoadToRuntimeAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SaveActiveRuntimeSceneAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(GamePreviewFolder))
            throw new InvalidOperationException("Projeto nao aberto.");

        _currentScene.Entities = Hierarchy.Entities.ToList();
        _currentScene.Tilemaps = Viewport.Tilemaps.ToList();
        NormalizeSceneComponents(_currentScene);

        var activePath = ActiveRuntimeScenePath(GamePreviewFolder);
        _activeRuntimeScenePath = activePath;
        await _sceneSerializer.SaveAsync(activePath, _currentScene, cancellationToken)
            .ConfigureAwait(false);
        return activePath;
    }

    private async Task SendSceneLoadToRuntimeAsync(CancellationToken cancellationToken)
    {
        if (!_bridge.IsConnected || string.IsNullOrWhiteSpace(SceneProjectPath) || string.IsNullOrWhiteSpace(GamePreviewFolder))
            return;

        var targetPath = !string.IsNullOrWhiteSpace(_activeRuntimeScenePath)
            ? _activeRuntimeScenePath
            : SceneProjectPath.Trim();
        var relPath = Path.GetRelativePath(GamePreviewFolder, targetPath).Replace('\\', '/');
        await _bridge.SendLineAsync(EditorCommand.SceneLoadLine(relPath), cancellationToken)
            .ConfigureAwait(false);
        _log.Post(EditorLogLevel.Info, $"Runtime scene reload: {relPath}");
    }

    private static void NormalizeSceneComponents(SceneState scene)
    {
        foreach (var entity in scene.Entities)
        {
            UpsertTransform(entity);
            if (entity.Type.Equals("Sprite", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(entity.TexturePath))
                UpsertSpriteRenderer(entity);
        }
    }

    private static void UpsertTransform(SceneEntityDto entity)
    {
        var transform = GetOrAddComponent(entity, "Transform");
        transform.Properties["position"] = JsonSerializer.SerializeToElement(new[] { entity.X, entity.Y });
        transform.Properties["rotation"] = JsonSerializer.SerializeToElement(entity.Rotation);
        transform.Properties["scale"] = JsonSerializer.SerializeToElement(new[] { entity.ScaleX, entity.ScaleY });
    }

    private static void UpsertSpriteRenderer(SceneEntityDto entity)
    {
        var sprite = GetOrAddComponent(entity, "SpriteRenderer");
        if (!string.IsNullOrWhiteSpace(entity.TexturePath))
            sprite.Properties["sprite"] = JsonSerializer.SerializeToElement(entity.TexturePath.Replace('\\', '/'));
        if (!sprite.Properties.ContainsKey("color"))
            sprite.Properties["color"] = JsonSerializer.SerializeToElement(new[] { 1f, 1f, 1f, 1f });
        if (!sprite.Properties.ContainsKey("flip_x"))
            sprite.Properties["flip_x"] = JsonSerializer.SerializeToElement(false);
        if (!sprite.Properties.ContainsKey("flip_y"))
            sprite.Properties["flip_y"] = JsonSerializer.SerializeToElement(false);
        if (!sprite.Properties.ContainsKey("layer"))
            sprite.Properties["layer"] = JsonSerializer.SerializeToElement("Default");
        if (!sprite.Properties.ContainsKey("sorting_order"))
            sprite.Properties["sorting_order"] = JsonSerializer.SerializeToElement(0);
    }

    private static ComponentDto GetOrAddComponent(SceneEntityDto entity, string type)
    {
        var component = entity.Components.FirstOrDefault(c => c.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
        if (component is not null)
            return component;

        component = new ComponentDto { Type = type };
        entity.Components.Add(component);
        return component;
    }

    private SceneEntityDto CreateEditorEntity(string type)
    {
        var normalized = string.IsNullOrWhiteSpace(type) ? "Empty" : type.Trim();
        var count = Hierarchy.Entities.Count(e => e.Type.Equals(normalized, StringComparison.OrdinalIgnoreCase)) + 1;
        return new SceneEntityDto
        {
            Id = normalized.ToLowerInvariant() + "-" + Guid.NewGuid().ToString("N")[..8],
            Name = normalized switch
            {
                "Camera" => $"Camera_{count:00}",
                "Sprite" => $"Sprite_{count:00}",
                _ => $"Entity_{Hierarchy.Entities.Count + 1:00}",
            },
            Type = normalized,
            X = normalized.Equals("Camera", StringComparison.OrdinalIgnoreCase) ? 48 : 180,
            Y = normalized.Equals("Camera", StringComparison.OrdinalIgnoreCase) ? 48 : 160,
            ScaleX = normalized.Equals("Camera", StringComparison.OrdinalIgnoreCase) ? 1.2f : 1f,
            ScaleY = normalized.Equals("Camera", StringComparison.OrdinalIgnoreCase) ? 1.2f : 1f,
        };
    }

    private static string DefaultScenePath(string projectPath)
        => Path.Combine(Path.GetFullPath(projectPath), "scenes", "main.scene.json");

    private static string ActiveRuntimeScenePath(string projectPath)
        => Path.Combine(Path.GetFullPath(projectPath), "scenes", "active.scene.json");

    private void DeleteActiveRuntimeScene()
    {
        var path = _activeRuntimeScenePath;
        _activeRuntimeScenePath = null;

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.Post(EditorLogLevel.Warning, $"Nao foi possivel remover active.scene.json: {ex.Message}");
        }
    }

    private void CreateMinimalProject(string projectPath)
    {
        Directory.CreateDirectory(projectPath);
        Directory.CreateDirectory(Path.Combine(projectPath, "res", "sprites"));
        Directory.CreateDirectory(Path.Combine(projectPath, "res", "audio"));
        Directory.CreateDirectory(Path.Combine(projectPath, "res", "tilemaps"));
        Directory.CreateDirectory(Path.Combine(projectPath, "scenes"));
        Directory.CreateDirectory(Path.Combine(projectPath, "scripts"));

        var name = Path.GetFileName(projectPath);
        File.WriteAllText(Path.Combine(projectPath, "aegis.toml"), $$"""
[game]
title = "{{name}}"
width = 1280
height = 720
entry = "main.lua"
""");

        File.WriteAllText(Path.Combine(projectPath, "aegis.cfg"), """
{
  "windowWidth": 1280,
  "windowHeight": 720,
  "displayMode": "windowed",
  "fullscreen": false,
  "vsync": true,
  "masterVolume": 1
}
""");

        File.WriteAllText(Path.Combine(projectPath, "main.lua"), """
function aegis_init()
    aegis.log("Projeto Aegis criado pelo editor.")
    aegis.loadScene("scenes/main.scene.json")
end

function aegis_update(dt)
end

function aegis_draw()
end
""");

        _sceneSerializer.SaveAsync(
            DefaultScenePath(projectPath),
            SceneState.CreateDefault2D("Main")).GetAwaiter().GetResult();
    }

    private void AddRecentProject(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        for (var i = RecentProjects.Count - 1; i >= 0; i--)
        {
            if (RecentProjects[i].Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                RecentProjects.RemoveAt(i);
        }

        RecentProjects.Insert(0, fullPath);
        while (RecentProjects.Count > 8)
            RecentProjects.RemoveAt(RecentProjects.Count - 1);

        SaveRecentProjects();
    }

    private void SaveRecentProjects()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_recentProjectsFile)!);
            File.WriteAllText(_recentProjectsFile, JsonSerializer.Serialize(RecentProjects.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _log.Post(EditorLogLevel.Warning, $"Nao foi possivel salvar projetos recentes: {ex.Message}");
        }
    }

    private interface IEditorAction
    {
        string Name { get; }
        void Undo(MainWindowViewModel editor);
        void Redo(MainWindowViewModel editor);
    }

    private sealed record DeletedEntitySnapshot(SceneEntityDto Entity, int Index);

    private sealed record CreateEntityAction(SceneEntityDto Entity, int Index) : IEditorAction
    {
        public string Name => $"create {Entity.Name}";

        public void Undo(MainWindowViewModel editor)
        {
            var entity = editor.Hierarchy.Entities.FirstOrDefault(e => e.Id.Equals(Entity.Id, StringComparison.Ordinal));
            if (entity is not null)
                editor.RemoveEntityCore(entity);
        }

        public void Redo(MainWindowViewModel editor)
            => editor.InsertEntityAt(CloneEntity(Entity), Index);
    }

    private sealed record DeleteEntitiesAction(IReadOnlyList<DeletedEntitySnapshot> Items) : IEditorAction
    {
        public string Name => Items.Count == 1 ? "delete entity" : $"delete {Items.Count} entities";

        public void Undo(MainWindowViewModel editor)
        {
            foreach (var item in Items.OrderBy(i => i.Index))
                editor.InsertEntityAt(CloneEntity(item.Entity), item.Index);
        }

        public void Redo(MainWindowViewModel editor)
        {
            foreach (var item in Items)
            {
                var entity = editor.Hierarchy.Entities.FirstOrDefault(e => e.Id.Equals(item.Entity.Id, StringComparison.Ordinal));
                if (entity is not null)
                    editor.RemoveEntityCore(entity);
            }
        }
    }

    private sealed record TransformEntitiesAction(
        IReadOnlyList<EntityTransform> Before,
        IReadOnlyList<EntityTransform> After) : IEditorAction
    {
        public string Name => Before.Count == 1 ? "move entity" : $"move {Before.Count} entities";

        public void Undo(MainWindowViewModel editor)
            => editor.ApplyTransforms(Before);

        public void Redo(MainWindowViewModel editor)
            => editor.ApplyTransforms(After);
    }

    private sealed record EditEntityAction(SceneEntityDto Before, SceneEntityDto After) : IEditorAction
    {
        public string Name => $"edit {After.Name}";

        public void Undo(MainWindowViewModel editor)
            => editor.ApplyEntitySnapshot(Before);

        public void Redo(MainWindowViewModel editor)
            => editor.ApplyEntitySnapshot(After);
    }
}
