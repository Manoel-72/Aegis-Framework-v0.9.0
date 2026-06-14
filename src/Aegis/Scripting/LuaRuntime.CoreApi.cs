using Aegis.Core;
using Aegis.Display;
using Aegis.Effects;
using Aegis.Physics;
using Aegis.Scene;
using Aegis.World;
using Microsoft.Xna.Framework;
using NLua;

namespace Aegis.Scripting;

public sealed partial class LuaRuntime
{
    public Object2D Create(string kind, LuaTable? opts = null)
        => _components.Create(kind, opts);

    public void Destroy(Object2D obj)
        => RemoveObject(obj);

    public SpriteNode NewSprite(string path, bool hud = false)
        => _components.CreateSprite(path, hud);

    public Bitmap NewRect(int w, int h, float r, float g, float b, bool hud = false)
        => _components.CreateRect(w, h, new Color(r, g, b), hud);

    public void RemoveObject(Object2D obj)
    {
        // Remove componentes físicos primeiro. Se o objeto sai da cena mas o
        // collider continua registrado, ele vira uma colisão invisível.
        RemoveRigidbody(obj);

        var ownedColliderIds = _colliders
            .Where(kv => kv.Value.Owner == obj)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in ownedColliderIds)
        {
            CollisionSystem.Instance.Unregister(_colliders[id]);
            _colliders.Remove(id);
        }

        obj.RemoveFromParent();
    }

    // ── Transform ─────────────────────────────────────────────────────
    public void  SetPosition(Object2D o, float x, float y)
    {
        Require(o, nameof(SetPosition));
        o.X = RequireFinite(x, nameof(x));
        o.Y = RequireFinite(y, nameof(y));
    }
    public void  SetPositionNorm(Object2D o, float nx, float ny)
    {
        Require(o, nameof(SetPositionNorm));
        o.X = RequireFinite(nx, nameof(nx)) * _app.ScreenWidth;
        o.Y = RequireFinite(ny, nameof(ny)) * _app.ScreenHeight;
    }
    public float CenterX(Object2D o)
    {
        Require(o, nameof(CenterX));
        return o.X + (o is Bitmap b ? b.TextureWidth * 0.5f : 0f);
    }
    public void  SetZ(Object2D o, int z)
    {
        Require(o, nameof(SetZ));
        o.Z = z;
    }
    public int   GetZ(Object2D o)
    {
        Require(o, nameof(GetZ));
        return o.Z;
    }
    public void  Move(Object2D o, float dx, float dy)      { Require(o, nameof(Move)); o.X += RequireFinite(dx, nameof(dx)); o.Y += RequireFinite(dy, nameof(dy)); }
    public void  SetScale(Object2D o, float sx, float sy)  { Require(o, nameof(SetScale)); o.ScaleX = RequireFinite(sx, nameof(sx), 1f); o.ScaleY = RequireFinite(sy, nameof(sy), 1f); }
    public void  SetRotation(Object2D o, float rad)        { Require(o, nameof(SetRotation)); o.Rotation = RequireFinite(rad, nameof(rad)); }
    public void  SetAlpha(Object2D o, float a)             => o.Alpha = Math.Clamp(a, 0f, 1f);
    public void  SetVisible(Object2D o, bool v)            => o.Visible = v;
    public void  SetFlip(Bitmap b, bool flipX, bool flipY = false) { b.FlipX = flipX; b.FlipY = flipY; }
    public void  SetPivot(Bitmap b, float px, float py)    => b.Pivot = new Vector2(px, py);
    public float GetX(Object2D o)    => o.X;
    public float GetY(Object2D o)    => o.Y;
    public int   GetWidth(Bitmap b)  => b.TextureWidth;
    public int   GetHeight(Bitmap b) => b.TextureHeight;

    // ── Label ─────────────────────────────────────────────────────────

    public void  Log(string msg)                   => AegisLog.Info("Lua", msg);
    public void  SetRandomSeed(int seed)           => _rng = new Random(seed);
    public int   RandomInt(int min, int max)        => _rng.Next(min, max + 1);
    public float RandomFloat(float min, float max)  =>
        min + (float)_rng.NextDouble() * (max - min);

    /// Equivalente a clearAll/World.Clear do roadmap antigo.
    public void ClearAll()
    {
        // BUG #4 fix: matar todos os tweens antes de destruir objetos,
        // evitando tweens zumbis que atualizam Object2D já removidos da cena.
        TweenManager.Instance.KillAll();

        PhysicsWorld.Instance.Reset();
        _rigidbodies.Clear();
        _colliders.Clear();

        foreach (var child in _app.S2D.Children.ToArray())
            child.RemoveFromParent();

        if (_app.Ui2D is not null)
        {
            foreach (var child in _app.Ui2D.Children.ToArray())
                child.RemoveFromParent();
        }

        Camera2D.Instance.ResetForNewSession();
        SceneManager.Instance.ClearTriggers();

        // BUG #4 fix: limpar partículas em voo antes de soltar referência,
        // evitando que o ParticleSystem2D antigo continue rodando.
        _particles?.ClearAll();
        _particles = null;

        // Sprint 2+3+Final: limpar buttons, pools, float texts e resetar timer
        _buttons.Clear();
        _draggables.Clear();
        _activeDrag = null;
        _hands.Clear();
        HideUpgrades();
        _autozoomEnabled = false;
        foreach (var ft in _floatTexts) ft.Lbl.RemoveFromParent();
        _floatTexts.Clear();
        foreach (var pool in _pools.Values) pool.Clear();
        _pools.Clear();
        _poolIdSeq   = 0;
        _components.ClearRuntimeState();
        ClearSceneScripts();
        ScreenEffects.Instance.Reset();
        ShaderManager.ClearScreenShader();
        _totalTime   = 0f;
    }

    /// Alias explícito para scripts antigos.
    public void WorldClear() => ClearAll();

    /// <summary>Remove apenas objetos da camada UI (Ui2D), mantendo o mundo.</summary>
    public void UiClear()
    {
        if (_app.Ui2D is null) return;

        foreach (var child in _app.Ui2D.Children.ToArray())
            child.RemoveFromParent();

        _buttons.RemoveAll(b => IsUiObject(b.Obj));
        for (int i = _floatTexts.Count - 1; i >= 0; i--)
        {
            if (!IsUiObject(_floatTexts[i].Lbl)) continue;
            _floatTexts[i].Lbl.RemoveFromParent();
            _floatTexts.RemoveAt(i);
        }
    }

    /// <summary>Primitivas imediatas em espaço de tela — use dentro de aegis_draw_ui().</summary>
}
