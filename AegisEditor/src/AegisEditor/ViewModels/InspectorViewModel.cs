using System.Collections.ObjectModel;
using System.Text.Json;
using AegisEditor.Shared.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AegisEditor.ViewModels;

public sealed partial class InspectorViewModel : ObservableObject
{
    private static readonly string[] OfficialComponents =
    [
        "SpriteRenderer",
        "Collider2D",
        "Rigidbody2D",
        "AudioSource",
        "Script",
        "Camera",
        "TagLayer",
    ];

    [ObservableProperty]
    private SceneEntityDto? _target;

    [ObservableProperty]
    private string? _selectedComponentType;

    [ObservableProperty]
    private SpriteRendererInspector? _spriteRenderer;

    [ObservableProperty]
    private Collider2DInspector? _collider2D;

    [ObservableProperty]
    private Rigidbody2DInspector? _rigidbody2D;

    [ObservableProperty]
    private AudioSourceInspector? _audioSource;

    [ObservableProperty]
    private ScriptInspector? _script;

    [ObservableProperty]
    private CameraInspector? _camera;

    [ObservableProperty]
    private TagLayerInspector? _tagLayer;

    private SceneEntityDto? _editBefore;

    public ObservableCollection<string> ComponentTypes { get; } = new(OfficialComponents);

    public event EventHandler<InspectorEditCommit>? EditCommitted;

    public bool HasTarget => Target is not null;
    public bool HasSpriteRenderer => SpriteRenderer is not null;
    public bool HasCollider2D => Collider2D is not null;
    public bool HasRigidbody2D => Rigidbody2D is not null;
    public bool HasAudioSource => AudioSource is not null;
    public bool HasScript => Script is not null;
    public bool HasCamera => Camera is not null;
    public bool HasTagLayer => TagLayer is not null;

    public void ApplySelection(SceneEntityDto? entity)
    {
        CommitEdit();
        Target = entity;
        RebuildComponents();
    }

    public void BeginEdit()
    {
        if (Target is null || _editBefore is not null)
            return;

        _editBefore = Clone(Target);
    }

    public void CommitEdit()
    {
        if (_editBefore is null || Target is null)
        {
            _editBefore = null;
            return;
        }

        var before = _editBefore;
        var after = Clone(Target);
        _editBefore = null;

        if (JsonSerializer.Serialize(before) == JsonSerializer.Serialize(after))
            return;

        EditCommitted?.Invoke(this, new InspectorEditCommit(before, after));
        RebuildComponents();
    }

    [RelayCommand]
    private void AddComponent()
    {
        if (Target is null || string.IsNullOrWhiteSpace(SelectedComponentType))
            return;

        BeginEdit();
        EnsureComponent(Target, SelectedComponentType.Trim());
        CommitEdit();
    }

    [RelayCommand]
    private void RemoveComponent(string? type)
    {
        if (Target is null || string.IsNullOrWhiteSpace(type))
            return;

        BeginEdit();
        Target.Components.RemoveAll(c => c.Type.Equals(type.Trim(), StringComparison.OrdinalIgnoreCase));
        CommitEdit();
    }

    public void AssignScript(string scriptPath)
    {
        if (Target is null || string.IsNullOrWhiteSpace(scriptPath))
            return;

        BeginEdit();
        var component = EnsureComponent(Target, "Script");
        var normalized = scriptPath.Replace('\\', '/');
        component.Properties["file"] = JsonSerializer.SerializeToElement(normalized);
        Target.ScriptPath = normalized;
        CommitEdit();
    }

    partial void OnTargetChanged(SceneEntityDto? oldValue, SceneEntityDto? newValue)
    {
        OnPropertyChanged(nameof(HasTarget));
    }

    private void RebuildComponents()
    {
        var entity = Target;
        SpriteRenderer = entity is null ? null : Build(entity, "SpriteRenderer", c => new SpriteRendererInspector(this, entity, c));
        Collider2D = entity is null ? null : Build(entity, "Collider2D", c => new Collider2DInspector(this, c));
        Rigidbody2D = entity is null ? null : Build(entity, "Rigidbody2D", c => new Rigidbody2DInspector(this, c));
        AudioSource = entity is null ? null : Build(entity, "AudioSource", c => new AudioSourceInspector(this, c));
        Script = entity is null ? null : Build(entity, "Script", c => new ScriptInspector(this, entity, c));
        Camera = entity is null ? null : Build(entity, "Camera", c => new CameraInspector(this, c));
        TagLayer = entity is null ? null : Build(entity, "TagLayer", c => new TagLayerInspector(this, c));

        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(HasSpriteRenderer));
        OnPropertyChanged(nameof(HasCollider2D));
        OnPropertyChanged(nameof(HasRigidbody2D));
        OnPropertyChanged(nameof(HasAudioSource));
        OnPropertyChanged(nameof(HasScript));
        OnPropertyChanged(nameof(HasCamera));
        OnPropertyChanged(nameof(HasTagLayer));
    }

    private static T? Build<T>(SceneEntityDto entity, string type, Func<ComponentDto, T> create)
    {
        var component = entity.Components.FirstOrDefault(c => c.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
        return component is null ? default : create(component);
    }

    private static ComponentDto EnsureComponent(SceneEntityDto entity, string type)
    {
        var component = entity.Components.FirstOrDefault(c => c.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
        if (component is not null)
            return component;

        component = new ComponentDto { Type = type };
        ApplyDefaults(entity, component);
        entity.Components.Add(component);
        return component;
    }

    private static void ApplyDefaults(SceneEntityDto entity, ComponentDto component)
    {
        switch (component.Type)
        {
            case "SpriteRenderer":
                component.Properties["sprite"] = JsonSerializer.SerializeToElement(entity.TexturePath ?? string.Empty);
                component.Properties["color"] = JsonSerializer.SerializeToElement(new[] { 1f, 1f, 1f, 1f });
                component.Properties["flip_x"] = JsonSerializer.SerializeToElement(false);
                component.Properties["flip_y"] = JsonSerializer.SerializeToElement(false);
                component.Properties["layer"] = JsonSerializer.SerializeToElement("Default");
                component.Properties["sorting_order"] = JsonSerializer.SerializeToElement(0);
                break;
            case "Collider2D":
                component.Properties["shape"] = JsonSerializer.SerializeToElement("box");
                component.Properties["size"] = JsonSerializer.SerializeToElement(new[] { 32f, 32f });
                component.Properties["offset"] = JsonSerializer.SerializeToElement(new[] { 0f, 0f });
                component.Properties["is_trigger"] = JsonSerializer.SerializeToElement(false);
                break;
            case "Rigidbody2D":
                component.Properties["type"] = JsonSerializer.SerializeToElement("dynamic");
                component.Properties["gravity_scale"] = JsonSerializer.SerializeToElement(1f);
                component.Properties["velocity"] = JsonSerializer.SerializeToElement(new[] { 0f, 0f });
                component.Properties["linear_drag"] = JsonSerializer.SerializeToElement(0f);
                break;
            case "AudioSource":
                component.Properties["clip"] = JsonSerializer.SerializeToElement(string.Empty);
                component.Properties["volume"] = JsonSerializer.SerializeToElement(1f);
                component.Properties["pitch"] = JsonSerializer.SerializeToElement(1f);
                component.Properties["loop"] = JsonSerializer.SerializeToElement(false);
                component.Properties["play_on_start"] = JsonSerializer.SerializeToElement(false);
                break;
            case "Script":
                component.Properties["file"] = JsonSerializer.SerializeToElement(entity.ScriptPath ?? string.Empty);
                component.Properties["properties"] = JsonSerializer.SerializeToElement(new Dictionary<string, object>());
                break;
            case "Camera":
                component.Properties["zoom"] = JsonSerializer.SerializeToElement(1f);
                component.Properties["viewport"] = JsonSerializer.SerializeToElement(new[] { 0f, 0f, 1f, 1f });
                component.Properties["background_color"] = JsonSerializer.SerializeToElement(new[] { 0.03f, 0.06f, 0.09f, 1f });
                component.Properties["follow_target"] = JsonSerializer.SerializeToElement(string.Empty);
                break;
            case "TagLayer":
                component.Properties["tag"] = JsonSerializer.SerializeToElement("Untagged");
                component.Properties["layer"] = JsonSerializer.SerializeToElement("Default");
                break;
        }
    }

    private static SceneEntityDto Clone(SceneEntityDto entity)
    {
        var json = JsonSerializer.Serialize(entity);
        return JsonSerializer.Deserialize<SceneEntityDto>(json)
            ?? throw new InvalidOperationException("Falha ao clonar entidade do Inspector.");
    }

    internal void Edit(Action change)
    {
        if (Target is null)
            return;

        BeginEdit();
        change();
        CommitEdit();
    }
}

public abstract partial class ComponentInspectorBase(InspectorViewModel owner, ComponentDto component) : ObservableObject
{
    protected ComponentDto Component { get; } = component;
    protected InspectorViewModel Owner { get; } = owner;

    protected string String(string key, string fallback = "")
        => Component.Properties.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    protected float Float(string key, float fallback = 0f)
        => Component.Properties.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var result)
            ? result
            : fallback;

    protected int Int(string key, int fallback = 0)
        => Component.Properties.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : fallback;

    protected bool Bool(string key, bool fallback = false)
        => Component.Properties.TryGetValue(key, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : fallback;

    protected float ArrayFloat(string key, int index, float fallback = 0f)
    {
        if (!Component.Properties.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Array)
            return fallback;

        var i = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (i++ == index && item.ValueKind == JsonValueKind.Number && item.TryGetSingle(out var result))
                return result;
        }

        return fallback;
    }

    protected void Set<T>(string key, T value, string propertyName)
    {
        Owner.Edit(() => Component.Properties[key] = JsonSerializer.SerializeToElement(value));
        OnPropertyChanged(propertyName);
    }

    protected void SetArray(string key, float[] values, string propertyName)
    {
        Owner.Edit(() => Component.Properties[key] = JsonSerializer.SerializeToElement(values));
        OnPropertyChanged(propertyName);
    }
}

public sealed partial class SpriteRendererInspector(InspectorViewModel owner, SceneEntityDto entity, ComponentDto component)
    : ComponentInspectorBase(owner, component)
{
    public string Texture
    {
        get => String("sprite", entity.TexturePath ?? string.Empty);
        set
        {
            Owner.Edit(() =>
            {
                Component.Properties["sprite"] = JsonSerializer.SerializeToElement(value ?? string.Empty);
                entity.TexturePath = value;
            });
            OnPropertyChanged();
        }
    }

    public float ColorR { get => ArrayFloat("color", 0, 1f); set => SetColor(0, value); }
    public float ColorG { get => ArrayFloat("color", 1, 1f); set => SetColor(1, value); }
    public float ColorB { get => ArrayFloat("color", 2, 1f); set => SetColor(2, value); }
    public float Alpha { get => ArrayFloat("color", 3, 1f); set => SetColor(3, value); }
    public bool FlipX { get => Bool("flip_x"); set => Set("flip_x", value, nameof(FlipX)); }
    public bool FlipY { get => Bool("flip_y"); set => Set("flip_y", value, nameof(FlipY)); }
    public string Layer { get => String("layer", "Default"); set => Set("layer", value ?? "Default", nameof(Layer)); }
    public int SortingOrder { get => Int("sorting_order"); set => Set("sorting_order", value, nameof(SortingOrder)); }

    private void SetColor(int index, float value)
    {
        var color = new[] { ColorR, ColorG, ColorB, Alpha };
        color[index] = Math.Clamp(value, 0f, 1f);
        SetArray("color", color, index switch
        {
            0 => nameof(ColorR),
            1 => nameof(ColorG),
            2 => nameof(ColorB),
            _ => nameof(Alpha),
        });
    }
}

public sealed partial class Collider2DInspector(InspectorViewModel owner, ComponentDto component)
    : ComponentInspectorBase(owner, component)
{
    public string[] ShapeOptions { get; } = ["box", "circle", "polygon"];
    public string Shape { get => String("shape", "box"); set => Set("shape", value ?? "box", nameof(Shape)); }
    public float OffsetX { get => ArrayFloat("offset", 0); set => SetVector("offset", 0, value, nameof(OffsetX)); }
    public float OffsetY { get => ArrayFloat("offset", 1); set => SetVector("offset", 1, value, nameof(OffsetY)); }
    public float Width { get => ArrayFloat("size", 0, 32f); set => SetVector("size", 0, MathF.Max(0.001f, value), nameof(Width)); }
    public float Height { get => ArrayFloat("size", 1, 32f); set => SetVector("size", 1, MathF.Max(0.001f, value), nameof(Height)); }
    public bool IsTrigger { get => Bool("is_trigger"); set => Set("is_trigger", value, nameof(IsTrigger)); }

    private void SetVector(string key, int index, float value, string propertyName)
    {
        var values = new[] { ArrayFloat(key, 0), ArrayFloat(key, 1) };
        values[index] = value;
        SetArray(key, values, propertyName);
    }
}

public sealed partial class Rigidbody2DInspector(InspectorViewModel owner, ComponentDto component)
    : ComponentInspectorBase(owner, component)
{
    public string[] TypeOptions { get; } = ["dynamic", "static", "kinematic"];
    public string BodyType { get => String("type", "dynamic"); set => Set("type", value ?? "dynamic", nameof(BodyType)); }
    public float GravityScale { get => Float("gravity_scale", 1f); set => Set("gravity_scale", MathF.Max(0f, value), nameof(GravityScale)); }
    public float VelocityX { get => ArrayFloat("velocity", 0); set => SetVelocity(0, value, nameof(VelocityX)); }
    public float VelocityY { get => ArrayFloat("velocity", 1); set => SetVelocity(1, value, nameof(VelocityY)); }
    public float LinearDrag { get => Float("linear_drag"); set => Set("linear_drag", MathF.Max(0f, value), nameof(LinearDrag)); }

    private void SetVelocity(int index, float value, string propertyName)
    {
        var values = new[] { VelocityX, VelocityY };
        values[index] = value;
        SetArray("velocity", values, propertyName);
    }
}

public sealed partial class AudioSourceInspector(InspectorViewModel owner, ComponentDto component)
    : ComponentInspectorBase(owner, component)
{
    public string Clip { get => String("clip"); set => Set("clip", value ?? string.Empty, nameof(Clip)); }
    public float Volume { get => Float("volume", 1f); set => Set("volume", Math.Clamp(value, 0f, 1f), nameof(Volume)); }
    public float Pitch { get => Float("pitch", 1f); set => Set("pitch", Math.Clamp(value, 0.01f, 4f), nameof(Pitch)); }
    public bool Loop { get => Bool("loop"); set => Set("loop", value, nameof(Loop)); }
    public bool PlayOnStart { get => Bool("play_on_start"); set => Set("play_on_start", value, nameof(PlayOnStart)); }
}

public sealed partial class ScriptInspector(InspectorViewModel owner, SceneEntityDto entity, ComponentDto component)
    : ComponentInspectorBase(owner, component)
{
    public string File
    {
        get => String("file", entity.ScriptPath ?? string.Empty);
        set
        {
            Owner.Edit(() =>
            {
                Component.Properties["file"] = JsonSerializer.SerializeToElement(value ?? string.Empty);
                entity.ScriptPath = value;
            });
            OnPropertyChanged();
        }
    }

    public string PublicVariablesJson
    {
        get => Component.Properties.TryGetValue("properties", out var value) ? value.GetRawText() : "{}";
        set
        {
            Owner.Edit(() =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(value) ? "{}" : value);
                    Component.Properties["properties"] = doc.RootElement.Clone();
                }
                catch
                {
                    Component.Properties["properties"] = JsonSerializer.SerializeToElement(new Dictionary<string, object>());
                }
            });
            OnPropertyChanged();
        }
    }
}

public sealed partial class CameraInspector(InspectorViewModel owner, ComponentDto component)
    : ComponentInspectorBase(owner, component)
{
    public float Zoom { get => Float("zoom", 1f); set => Set("zoom", MathF.Max(0.01f, value), nameof(Zoom)); }
    public string FollowTarget { get => String("follow_target"); set => Set("follow_target", value ?? string.Empty, nameof(FollowTarget)); }
    public float ViewportX { get => ArrayFloat("viewport", 0); set => SetViewport(0, value, nameof(ViewportX)); }
    public float ViewportY { get => ArrayFloat("viewport", 1); set => SetViewport(1, value, nameof(ViewportY)); }
    public float ViewportW { get => ArrayFloat("viewport", 2, 1f); set => SetViewport(2, MathF.Max(0.01f, value), nameof(ViewportW)); }
    public float ViewportH { get => ArrayFloat("viewport", 3, 1f); set => SetViewport(3, MathF.Max(0.01f, value), nameof(ViewportH)); }
    public float BackgroundR { get => ArrayFloat("background_color", 0, 0.03f); set => SetBackground(0, value, nameof(BackgroundR)); }
    public float BackgroundG { get => ArrayFloat("background_color", 1, 0.06f); set => SetBackground(1, value, nameof(BackgroundG)); }
    public float BackgroundB { get => ArrayFloat("background_color", 2, 0.09f); set => SetBackground(2, value, nameof(BackgroundB)); }

    private void SetViewport(int index, float value, string propertyName)
    {
        var values = new[] { ViewportX, ViewportY, ViewportW, ViewportH };
        values[index] = value;
        SetArray("viewport", values, propertyName);
    }

    private void SetBackground(int index, float value, string propertyName)
    {
        var values = new[] { BackgroundR, BackgroundG, BackgroundB, 1f };
        values[index] = Math.Clamp(value, 0f, 1f);
        SetArray("background_color", values, propertyName);
    }
}

public sealed partial class TagLayerInspector(InspectorViewModel owner, ComponentDto component)
    : ComponentInspectorBase(owner, component)
{
    public string Tag { get => String("tag", "Untagged"); set => Set("tag", value ?? "Untagged", nameof(Tag)); }
    public string Layer { get => String("layer", "Default"); set => Set("layer", value ?? "Default", nameof(Layer)); }
}

public sealed record InspectorEditCommit(SceneEntityDto Before, SceneEntityDto After);
