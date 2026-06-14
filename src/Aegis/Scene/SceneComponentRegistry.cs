using Aegis.Core;
using Aegis.Display;
using Aegis.Audio;
using Aegis.Physics;
using Aegis.Scripting;
using AegisEditor.Shared.Models;
using Microsoft.Xna.Framework;

namespace Aegis.Scene;

public sealed class SceneComponentRegistry
{
    private readonly Dictionary<string, Action<Object2D, SceneEntityDto>> _appliers = new(StringComparer.OrdinalIgnoreCase);

    public SceneComponentRegistry()
    {
        Register("Transform", ApplyTransform);
        Register("SpriteRenderer", ApplySpriteRenderer);
        Register("Collider2D", ApplyCollider2D);
        Register("Rigidbody2D", ApplyRigidbody2D);
        Register("AudioSource", ApplyAudioSource);
        Register("Camera", ApplyCamera);
        Register("Script", ApplyScript);
        Register("TagLayer", ApplyTagLayer);
    }

    public void Register(string componentType, Action<Object2D, SceneEntityDto> apply)
    {
        if (string.IsNullOrWhiteSpace(componentType))
            throw new ArgumentException("Component type cannot be empty.", nameof(componentType));

        _appliers[componentType.Trim()] = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    public bool Has(string componentType) => _appliers.ContainsKey(componentType);

    public void ApplyAll(Object2D obj, SceneEntityDto entity)
    {
        foreach (var component in entity.Components)
        {
            if (_appliers.TryGetValue(component.Type, out var apply))
                apply(obj, entity);
            else if (!string.IsNullOrWhiteSpace(component.Type))
                AegisLog.Warn("Scene", $"Componente de cena ainda nao suportado: {component.Type}");
        }
    }

    private static void ApplyTransform(Object2D obj, SceneEntityDto entity)
    {
        var transform = SceneComponentJson.Get(entity, "Transform");
        var position = SceneComponentJson.FloatArray(transform, "position", 2);
        var scale = SceneComponentJson.FloatArray(transform, "scale", 2);

        obj.X = Finite(position?[0] ?? entity.X);
        obj.Y = Finite(position?[1] ?? entity.Y);
        obj.ScaleX = Finite(scale?[0] ?? entity.ScaleX, 1f);
        obj.ScaleY = Finite(scale?[1] ?? entity.ScaleY, 1f);
        obj.Rotation = Finite(SceneComponentJson.Float(transform, "rotation") ?? entity.Rotation);
    }

    private static void ApplySpriteRenderer(Object2D obj, SceneEntityDto entity)
    {
        if (obj is not Bitmap bitmap)
            return;

        var sprite = SceneComponentJson.Get(entity, "SpriteRenderer");
        if (sprite is null)
            return;

        bitmap.FlipX = SceneComponentJson.Bool(sprite, "flip_x", "flipX") ?? bitmap.FlipX;
        bitmap.FlipY = SceneComponentJson.Bool(sprite, "flip_y", "flipY") ?? bitmap.FlipY;
        obj.Z = (int)MathF.Round(SceneComponentJson.Float(sprite, "sorting_order", "sortingOrder", "order") ?? obj.Z);

        var color = SceneComponentJson.FloatArray(sprite, "color", 4);
        if (color is { Length: >= 4 })
            bitmap.Alpha = Math.Clamp(color[3], 0f, 1f);
    }

    private static void ApplyCollider2D(Object2D obj, SceneEntityDto entity)
    {
        var collider = SceneComponentJson.Get(entity, "Collider2D");
        if (collider is null)
            return;

        var shape = SceneComponentJson.String(collider, "shape")?.Trim().ToLowerInvariant() ?? "box";
        var size = SceneComponentJson.FloatArray(collider, "size", 2);
        var offset = SceneComponentJson.FloatArray(collider, "offset", 2);
        var width = MathF.Max(0.001f, Finite(size?[0] ?? 32f, 32f));
        var height = MathF.Max(0.001f, Finite(size?[1] ?? 32f, 32f));
        var offX = Finite(offset?[0] ?? 0f);
        var offY = Finite(offset?[1] ?? 0f);

        var c = new Collider(obj, width, height, offX, offY)
        {
            Shape = shape == "circle" ? ColliderShape.Circle : ColliderShape.AABB,
            IsTrigger = SceneComponentJson.Bool(collider, "is_trigger", "isTrigger") ?? false,
        };

        if (c.Shape == ColliderShape.Circle)
            c.Radius = MathF.Max(0.001f, MathF.Min(width, height) * 0.5f);

        CollisionSystem.Instance.Register(c);
    }

    private static void ApplyRigidbody2D(Object2D obj, SceneEntityDto entity)
    {
        var body = SceneComponentJson.Get(entity, "Rigidbody2D");
        if (body is null)
            return;

        var type = SceneComponentJson.String(body, "type")?.Trim().ToLowerInvariant() ?? "dynamic";
        var rb = new Rigidbody2D(obj)
        {
            IsKinematic = type is "kinematic" or "static",
            GravityScale = MathF.Max(0f, Finite(SceneComponentJson.Float(body, "gravity_scale", "gravityScale") ?? 1f, 1f)),
            GroundFriction = MathF.Max(0f, Finite(SceneComponentJson.Float(body, "linear_drag", "linearDrag", "groundFriction") ?? 0f)),
        };

        PhysicsWorld.Instance.AddBody(rb);
    }

    private static void ApplyAudioSource(Object2D obj, SceneEntityDto entity)
    {
        var audio = SceneComponentJson.Get(entity, "AudioSource");
        if (audio is null)
            return;

        if (!(SceneComponentJson.Bool(audio, "play_on_start", "playOnStart") ?? false))
            return;

        var clip = SceneComponentJson.String(audio, "clip", "file", "path");
        if (string.IsNullOrWhiteSpace(clip))
            return;

        try
        {
            var volume = Math.Clamp(SceneComponentJson.Float(audio, "volume") ?? 1f, 0f, 1f);
            var pitch = Math.Clamp((SceneComponentJson.Float(audio, "pitch") ?? 1f) - 1f, -1f, 1f);
            var loop = SceneComponentJson.Bool(audio, "loop") ?? false;
            if (loop)
                AudioManager.PlayMusic(NormalizeAudioClip(clip), true);
            else
                AudioManager.PlaySound(NormalizeAudioClip(clip), volume, pitch);
        }
        catch (Exception ex)
        {
            AegisLog.Warn("Scene", $"AudioSource '{entity.Name}' nao iniciou '{clip}': {ex.Message}");
        }
    }

    private static void ApplyCamera(Object2D obj, SceneEntityDto entity)
    {
        var camera = SceneComponentJson.Get(entity, "Camera");
        if (camera is null)
            return;

        var cam = Camera2D.Instance;
        cam.SetZoom(MathF.Max(0.01f, SceneComponentJson.Float(camera, "zoom") ?? 1f));

        var bg = SceneComponentJson.FloatArray(camera, "background_color", 3);
        if (bg is { Length: >= 3 })
        {
            AegisGame.ClearColor = new Color(
                Math.Clamp(bg[0], 0f, 1f),
                Math.Clamp(bg[1], 0f, 1f),
                Math.Clamp(bg[2], 0f, 1f),
                bg.Length >= 4 ? Math.Clamp(bg[3], 0f, 1f) : 1f);
        }

        var followTarget = SceneComponentJson.String(camera, "follow_target", "followTarget");
        if (string.IsNullOrWhiteSpace(followTarget))
            cam.MoveTo(obj.X, obj.Y);
    }

    private static void ApplyScript(Object2D obj, SceneEntityDto entity)
    {
        SceneScriptHost.Register(obj, entity);
    }

    private static void ApplyTagLayer(Object2D obj, SceneEntityDto entity)
    {
        // Dados ficam no .scene.json para editor/validador/scripts. Runtime de tags/layers virá em sistema dedicado.
    }

    private static string NormalizeAudioClip(string clip)
    {
        var normalized = clip.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            ? normalized[6..]
            : normalized;
    }

    private static float Finite(float value, float fallback = 0f)
        => float.IsFinite(value) ? value : fallback;
}
