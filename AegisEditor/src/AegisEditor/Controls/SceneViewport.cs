using System.ComponentModel;
using AegisEditor.Shared.Models;
using AegisEditor.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AegisEditor.Views;
using SkiaSharp;

namespace AegisEditor.Controls;

/// <summary>
/// SkiaSharp draws directly into an Avalonia <see cref="WriteableBitmap"/> framebuffer.
/// </summary>
#pragma warning disable CS0618 // Avalonia 11 drag/drop compatibility until DataTransfer is adopted across the editor.
public sealed class SceneViewport : Control
{
    private enum InteractionMode { None, Pan, Move, MoveX, MoveY, Scale, Rotate, BoxSelect }

    private WriteableBitmap? _bitmap;
    private InteractionMode _mode;
    private Point _lastPanPoint;
    private Point _boxStartWorld;
    private Point _boxCurrentWorld;
    private float _rotationStartAngle;
    private List<EntityTransform> _transformStart = [];
    private readonly Dictionary<string, Vector> _dragOffsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SKBitmap?> _textureCache = new(StringComparer.OrdinalIgnoreCase);

    public SceneViewport()
    {
        Focusable = true;
        ContextMenu = BuildContextMenu();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private ContextMenu BuildContextMenu()
    {
        var delete = new MenuItem { Header = "Delete Entity" };
        delete.Click += (_, _) =>
        {
            if (DataContext is ViewportViewModel viewport)
                viewport.RequestDeleteSelected();
        };

        var snap = new MenuItem { Header = "Toggle Snap" };
        snap.Click += (_, _) =>
        {
            if (DataContext is ViewportViewModel viewport)
                viewport.ToggleSnapCommand.Execute(null);
        };

        var grid = new MenuItem { Header = "Toggle Grid" };
        grid.Click += (_, _) =>
        {
            if (DataContext is ViewportViewModel viewport)
                viewport.ToggleGridCommand.Execute(null);
        };

        var reset = new MenuItem { Header = "Reset View" };
        reset.Click += (_, _) =>
        {
            if (DataContext is ViewportViewModel viewport)
                viewport.ResetViewCommand.Execute(null);
        };

        return new ContextMenu { Items = { delete, new Separator(), snap, grid, reset } };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == DataContextProperty)
        {
            if (change.OldValue is ViewportViewModel oldVm)
                oldVm.PropertyChanged -= Viewport_PropertyChanged;

            if (change.NewValue is ViewportViewModel newVm)
                newVm.PropertyChanged += Viewport_PropertyChanged;

            InvalidateVisual();
        }

        base.OnPropertyChanged(change);
    }

    private void Viewport_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewportViewModel.Entities)
            or nameof(ViewportViewModel.Tilemaps)
            or nameof(ViewportViewModel.PaintTick)
            or nameof(ViewportViewModel.SelectedEntity)
            or nameof(ViewportViewModel.HoveredEntity)
            or nameof(ViewportViewModel.Zoom)
            or nameof(ViewportViewModel.PanX)
            or nameof(ViewportViewModel.PanY)
            or nameof(ViewportViewModel.SnapEnabled)
            or nameof(ViewportViewModel.ShowGrid)
            or nameof(ViewportViewModel.GridSize))
            InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (DataContext is not ViewportViewModel viewport) return;
        Focus();

        var point = e.GetCurrentPoint(this);
        var screen = e.GetPosition(this);
        var world = ScreenToWorld(screen, viewport);

        if (point.Properties.IsRightButtonPressed)
        {
            var contextHit = HitTestEntity(viewport, world);
            if (contextHit is not null)
            {
                if (!viewport.SelectedEntities.Contains(contextHit))
                    viewport.SelectOnly(contextHit);
                ContextMenu?.Open(this);
                e.Handled = true;
                return;
            }

            BeginPan(e, screen);
            return;
        }

        if (point.Properties.IsMiddleButtonPressed)
        {
            BeginPan(e, screen);
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        var key = e.KeyModifiers;
        var gizmo = HitGizmo(viewport, world);
        if (gizmo is not InteractionMode.None)
        {
            BeginTransform(viewport, e, world, gizmo);
            return;
        }

        var hit = HitTestEntity(viewport, world);
        if (hit is not null)
        {
            if (key.HasFlag(KeyModifiers.Control))
                viewport.ToggleSelection(hit);
            else if (!viewport.SelectedEntities.Contains(hit))
                viewport.SelectOnly(hit);

            BeginTransform(viewport, e, world, HitGizmo(viewport, world));
            return;
        }

        if (!key.HasFlag(KeyModifiers.Control))
            viewport.SelectOnly(null);

        _mode = InteractionMode.BoxSelect;
        _boxStartWorld = world;
        _boxCurrentWorld = world;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (DataContext is not ViewportViewModel viewport) return;

        var screen = e.GetPosition(this);
        var world = ScreenToWorld(screen, viewport);
        viewport.HoveredEntity = HitTestEntity(viewport, world);

        switch (_mode)
        {
            case InteractionMode.Pan:
                var delta = screen - _lastPanPoint;
                viewport.PanX += (float)delta.X;
                viewport.PanY += (float)delta.Y;
                _lastPanPoint = screen;
                e.Handled = true;
                return;

            case InteractionMode.Move:
            case InteractionMode.MoveX:
            case InteractionMode.MoveY:
                MoveSelection(viewport, world, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                return;

            case InteractionMode.Scale:
                ScaleSelection(viewport, world, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                return;

            case InteractionMode.Rotate:
                RotateSelection(viewport, world, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                return;

            case InteractionMode.BoxSelect:
                _boxCurrentWorld = world;
                viewport.NotifyRedraw();
                e.Handled = true;
                return;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (DataContext is ViewportViewModel viewport)
            EndInteraction(viewport, e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (DataContext is not ViewportViewModel viewport) return;

        var mouse = e.GetPosition(this);
        var before = ScreenToWorld(mouse, viewport);
        var factor = e.Delta.Y > 0 ? 1.1f : 0.9f;
        viewport.Zoom = Math.Clamp(viewport.Zoom * factor, 0.2f, 6f);
        viewport.PanX = (float)(mouse.X - before.X * viewport.Zoom);
        viewport.PanY = (float)(mouse.Y - before.Y * viewport.Zoom);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not ViewportViewModel viewport) return;

        if (e.Key is Key.Delete or Key.Back)
        {
            viewport.RequestDeleteSelected();
            e.Handled = true;
        }
    }

    private void BeginPan(PointerEventArgs e, Point screen)
    {
        _mode = InteractionMode.Pan;
        _lastPanPoint = screen;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void BeginTransform(ViewportViewModel viewport, PointerEventArgs e, Point world, InteractionMode mode)
    {
        _mode = mode == InteractionMode.None ? InteractionMode.Move : mode;
        _transformStart = viewport.SelectedEntities.Select(EntityTransform.From).ToList();
        _dragOffsets.Clear();
        foreach (var entity in viewport.SelectedEntities)
            _dragOffsets[entity.Id] = new Vector(world.X - entity.X, world.Y - entity.Y);

        if (_mode == InteractionMode.Rotate && viewport.SelectedEntity is not null)
        {
            var rect = EntityRect(viewport, viewport.SelectedEntity);
            _rotationStartAngle = MathF.Atan2((float)(world.Y - rect.CenterY), (float)(world.X - rect.CenterX));
        }

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void EndInteraction(ViewportViewModel viewport, PointerEventArgs e)
    {
        if (_mode == InteractionMode.BoxSelect)
        {
            var box = SelectionBox();
            viewport.SelectArea(entity =>
            {
                var rect = EntityRect(viewport, entity);
                return Intersects(box, new Rect(rect.Left, rect.Top, rect.Width, rect.Height));
            });
        }
        else if (_mode is InteractionMode.Move or InteractionMode.MoveX or InteractionMode.MoveY or InteractionMode.Scale or InteractionMode.Rotate)
        {
            var after = viewport.SelectedEntities.Select(EntityTransform.From).ToArray();
            viewport.RequestTransformCommitted(_transformStart, after);
        }

        _mode = InteractionMode.None;
        _transformStart = [];
        _dragOffsets.Clear();
        e.Pointer.Capture(null);
        viewport.NotifyRedraw();
    }

    private void MoveSelection(ViewportViewModel viewport, Point world, bool bypassSnap)
    {
        foreach (var entity in viewport.SelectedEntities)
        {
            if (!_dragOffsets.TryGetValue(entity.Id, out var offset))
                continue;

            var newX = viewport.Snap((float)(world.X - offset.X), bypassSnap);
            var newY = viewport.Snap((float)(world.Y - offset.Y), bypassSnap);
            if (_mode != InteractionMode.MoveY)
                entity.X = newX;
            if (_mode != InteractionMode.MoveX)
                entity.Y = newY;
        }
    }

    private void ScaleSelection(ViewportViewModel viewport, Point world, bool bypassSnap)
    {
        foreach (var entity in viewport.SelectedEntities)
        {
            var rect = EntityRect(viewport, entity);
            var targetW = MathF.Max(4f, (float)(world.X - entity.X));
            var targetH = MathF.Max(4f, (float)(world.Y - entity.Y));
            if (!bypassSnap)
            {
                targetW = MathF.Max(4f, viewport.Snap(targetW));
                targetH = MathF.Max(4f, viewport.Snap(targetH));
            }

            entity.ScaleX = MathF.Max(0.05f, targetW / MathF.Max(1f, (float)rect.BaseWidth));
            entity.ScaleY = MathF.Max(0.05f, targetH / MathF.Max(1f, (float)rect.BaseHeight));
        }
    }

    private void RotateSelection(ViewportViewModel viewport, Point world, bool bypassSnap)
    {
        var primary = viewport.SelectedEntity;
        if (primary is null)
            return;

        var rect = EntityRect(viewport, primary);
        var currentAngle = MathF.Atan2((float)(world.Y - rect.CenterY), (float)(world.X - rect.CenterX));
        var delta = currentAngle - _rotationStartAngle;
        const float snapStep = MathF.PI / 12f;

        foreach (var entity in viewport.SelectedEntities)
        {
            var start = _transformStart.FirstOrDefault(t => t.Id.Equals(entity.Id, StringComparison.Ordinal));
            if (start is null)
                continue;

            var rotation = start.Rotation + delta;
            if (!bypassSnap)
                rotation = MathF.Round(rotation / snapStep) * snapStep;
            entity.Rotation = rotation;
        }
    }

    private InteractionMode HitGizmo(ViewportViewModel viewport, Point world)
    {
        var entity = viewport.SelectedEntity;
        if (entity is null) return InteractionMode.None;

        var rect = EntityRect(viewport, entity);
        var rotateHandle = new Rect(rect.CenterX - 10, rect.Top - 48, 20, 20);
        if (rotateHandle.Contains(world))
            return InteractionMode.Rotate;

        var scaleHandle = new Rect(rect.Right - 8, rect.Bottom - 8, 16, 16);
        if (scaleHandle.Contains(world))
            return InteractionMode.Scale;

        var moveX = new Rect(rect.CenterX, rect.CenterY - 8, 56, 16);
        if (moveX.Contains(world))
            return InteractionMode.MoveX;

        var moveY = new Rect(rect.CenterX - 8, rect.CenterY - 56, 16, 56);
        if (moveY.Contains(world))
            return InteractionMode.MoveY;

        return InteractionMode.None;
    }

    public sealed override void Render(DrawingContext context)
    {
        base.Render(context);

        var vw = Bounds.Width;
        var vh = Bounds.Height;
        var topLevel = TopLevel.GetTopLevel(this);
        var scale = topLevel?.RenderScaling ?? 1.0;
        if (vw <= 4 || vh <= 4) return;

        var pixelW = Math.Max(8, (int)Math.Ceiling(vw * scale));
        var pixelH = Math.Max(8, (int)Math.Ceiling(vh * scale));
        var needed = new PixelSize(pixelW, pixelH);

        if (_bitmap is null || _bitmap.PixelSize != needed)
        {
            _bitmap?.Dispose();
            var dpi = new Vector(96d * scale, 96d * scale);
            _bitmap = new WriteableBitmap(needed, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        if (DataContext is not ViewportViewModel viewport)
            return;

        using (var framebuffer = _bitmap.Lock())
        {
            var info = new SKImageInfo(pixelW, pixelH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, framebuffer.Address, framebuffer.RowBytes);
            DrawWorld(surface.Canvas!, pixelW, pixelH, viewport);
            surface.Flush();
        }

        context.DrawImage(_bitmap, new Rect(default, Bounds.Size));
    }

    private void DrawWorld(SKCanvas canvas, float w, float h, ViewportViewModel viewport)
    {
        canvas.Clear(SKColor.Parse("#071018"));
        canvas.Save();
        canvas.Translate(viewport.PanX, viewport.PanY);
        canvas.Scale(viewport.Zoom);

        if (viewport.ShowGrid)
            DrawGrid(canvas, w, h, viewport);

        var spritePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        var selected = viewport.SelectedEntities.ToHashSet();
        foreach (var entity in viewport.Entities)
        {
            spritePaint.Color = ReferenceEquals(entity, viewport.HoveredEntity)
                ? SKColor.Parse("#56b6ff")
                : PickColor(entity.Type);

            var texture = TryGetTexture(viewport.ProjectRoot, entity);
            var rect = EntityRect(viewport, entity, texture);
            canvas.Save();
            canvas.RotateRadians(entity.Rotation, rect.MidX, rect.MidY);

            if (texture is not null)
                canvas.DrawBitmap(texture, rect);
            else
                DrawEntityPlaceholder(canvas, entity, rect, spritePaint);

            if (selected.Contains(entity))
                DrawSelection(canvas, rect, viewport.Zoom, ReferenceEquals(entity, viewport.SelectedEntity));

            canvas.Restore();
        }

        if (_mode == InteractionMode.BoxSelect)
            DrawSelectionBox(canvas, viewport.Zoom);

        canvas.Restore();
    }

    private void DrawGrid(SKCanvas canvas, float w, float h, ViewportViewModel viewport)
    {
        using var gridPaint = new SKPaint
        {
            Color = SKColor.Parse("#102232"),
            StrokeWidth = MathF.Max(1f / viewport.Zoom, 0.25f),
            IsStroke = true,
            IsAntialias = false,
        };

        var step = Math.Max(4, viewport.GridSize);
        var left = -viewport.PanX / viewport.Zoom;
        var top = -viewport.PanY / viewport.Zoom;
        var right = left + w / viewport.Zoom;
        var bottom = top + h / viewport.Zoom;
        var startX = MathF.Floor(left / step) * step;
        var startY = MathF.Floor(top / step) * step;
        for (var x = startX; x < right; x += step)
            canvas.DrawLine(x, top, x, bottom, gridPaint);
        for (var y = startY; y < bottom; y += step)
            canvas.DrawLine(left, y, right, y, gridPaint);
    }

    private static void DrawSelection(SKCanvas canvas, SKRect rect, float zoom, bool primary)
    {
        using var selectionPaint = new SKPaint
        {
            Color = primary ? SKColor.Parse("#7dd3fc") : SKColor.Parse("#38bdf8"),
            StrokeWidth = MathF.Max(primary ? 2f / zoom : 1.25f / zoom, 0.5f),
            IsStroke = true,
            IsAntialias = true,
        };
        canvas.DrawRect(rect.InflateCopy(4, 4), selectionPaint);
        if (primary)
        {
            DrawMoveGizmo(canvas, rect, zoom);
            DrawScaleGizmo(canvas, rect, zoom);
            DrawRotateGizmo(canvas, rect, zoom);
        }
    }

    private static void DrawMoveGizmo(SKCanvas canvas, SKRect rect, float zoom)
    {
        var stroke = MathF.Max(2f / zoom, 0.5f);
        var len = 42f;
        var cx = rect.MidX;
        var cy = rect.MidY;
        using var xPaint = new SKPaint { Color = SKColor.Parse("#ef4444"), StrokeWidth = stroke, IsStroke = true, IsAntialias = true };
        using var yPaint = new SKPaint { Color = SKColor.Parse("#22c55e"), StrokeWidth = stroke, IsStroke = true, IsAntialias = true };
        canvas.DrawLine(cx, cy, cx + len, cy, xPaint);
        canvas.DrawLine(cx + len, cy, cx + len - 8, cy - 5, xPaint);
        canvas.DrawLine(cx + len, cy, cx + len - 8, cy + 5, xPaint);
        canvas.DrawLine(cx, cy, cx, cy - len, yPaint);
        canvas.DrawLine(cx, cy - len, cx - 5, cy - len + 8, yPaint);
        canvas.DrawLine(cx, cy - len, cx + 5, cy - len + 8, yPaint);
    }

    private static void DrawScaleGizmo(SKCanvas canvas, SKRect rect, float zoom)
    {
        var size = MathF.Max(10f / zoom, 4f);
        using var paint = new SKPaint { Color = SKColor.Parse("#facc15"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRect(new SKRect(rect.Right - size, rect.Bottom - size, rect.Right + size, rect.Bottom + size), paint);
    }

    private static void DrawRotateGizmo(SKCanvas canvas, SKRect rect, float zoom)
    {
        var stroke = MathF.Max(1.5f / zoom, 0.5f);
        var radius = MathF.Max(8f / zoom, 4f);
        var cx = rect.MidX;
        var handleY = rect.Top - 38f;
        using var line = new SKPaint { Color = SKColor.Parse("#fb923c"), StrokeWidth = stroke, IsStroke = true, IsAntialias = true };
        using var fill = new SKPaint { Color = SKColor.Parse("#fb923c"), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawLine(cx, rect.Top, cx, handleY, line);
        canvas.DrawCircle(cx, handleY, radius, fill);
    }

    private void DrawSelectionBox(SKCanvas canvas, float zoom)
    {
        var box = SelectionBox();
        using var fill = new SKPaint { Color = new SKColor(125, 211, 252, 36), Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { Color = SKColor.Parse("#7dd3fc"), Style = SKPaintStyle.Stroke, StrokeWidth = MathF.Max(1.5f / zoom, 0.5f) };
        canvas.DrawRect(new SKRect((float)box.Left, (float)box.Top, (float)box.Right, (float)box.Bottom), fill);
        canvas.DrawRect(new SKRect((float)box.Left, (float)box.Top, (float)box.Right, (float)box.Bottom), stroke);
    }

    private SKBitmap? TryGetTexture(string projectRoot, SceneEntityDto entity)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(entity.TexturePath))
            return null;

        var path = Path.Combine(projectRoot, "res", entity.TexturePath.Replace('/', Path.DirectorySeparatorChar));
        if (_textureCache.TryGetValue(path, out var cached))
            return cached;

        try
        {
            if (!File.Exists(path))
            {
                _textureCache[path] = null;
                return null;
            }

            using var stream = File.OpenRead(path);
            var bitmap = SKBitmap.Decode(stream);
            _textureCache[path] = bitmap;
            return bitmap;
        }
        catch
        {
            _textureCache[path] = null;
            return null;
        }
    }

    private static void DrawEntityPlaceholder(SKCanvas canvas, SceneEntityDto entity, SKRect rect, SKPaint paint)
    {
        var type = entity.Type.ToUpperInvariant();
        if (type == "CAMERA")
        {
            paint.Color = SKColor.Parse("#8b5cf6");
            canvas.DrawRoundRect(rect, 3, 3, paint);
            using var lens = new SKPaint { Color = SKColor.Parse("#d8b4fe"), IsAntialias = true };
            canvas.DrawCircle(rect.MidX, rect.MidY, Math.Min(rect.Width, rect.Height) * 0.22f, lens);
            return;
        }

        if (type == "EMPTY")
        {
            using var cross = new SKPaint { Color = SKColor.Parse("#9ccbea"), StrokeWidth = 2, IsStroke = true, IsAntialias = true };
            canvas.DrawLine(rect.Left, rect.MidY, rect.Right, rect.MidY, cross);
            canvas.DrawLine(rect.MidX, rect.Top, rect.MidX, rect.Bottom, cross);
            return;
        }

        canvas.DrawRoundRect(rect, 4, 4, paint);
        using var shine = new SKPaint { Color = SKColor.Parse("#a7d8ff"), IsAntialias = true };
        canvas.DrawRect(new SKRect(rect.Left + 5, rect.Top + 5, rect.Right - 5, rect.Top + 10), shine);
    }

    private static Point ScreenToWorld(Point point, ViewportViewModel viewport)
        => new((point.X - viewport.PanX) / viewport.Zoom, (point.Y - viewport.PanY) / viewport.Zoom);

    private Rect SelectionBox()
    {
        var left = Math.Min(_boxStartWorld.X, _boxCurrentWorld.X);
        var top = Math.Min(_boxStartWorld.Y, _boxCurrentWorld.Y);
        var right = Math.Max(_boxStartWorld.X, _boxCurrentWorld.X);
        var bottom = Math.Max(_boxStartWorld.Y, _boxCurrentWorld.Y);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static bool Intersects(Rect a, Rect b)
        => a.Left <= b.Right
           && a.Right >= b.Left
           && a.Top <= b.Bottom
           && a.Bottom >= b.Top;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(AssetBrowserPanel.DragTexturePathFormat) || e.Data.Contains(DataFormats.Text)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ViewportViewModel viewport) return;

        var raw = e.Data.Contains(AssetBrowserPanel.DragTexturePathFormat)
            ? e.Data.Get(AssetBrowserPanel.DragTexturePathFormat)?.ToString()
            : e.Data.GetText();
        if (string.IsNullOrWhiteSpace(raw)) return;

        var pos = ScreenToWorld(e.GetPosition(this), viewport);
        viewport.RequestSpriteDrop(raw, (float)Math.Round(pos.X, 1), (float)Math.Round(pos.Y, 1));
        e.Handled = true;
    }

    private SceneEntityDto? HitTestEntity(ViewportViewModel viewport, Point point)
    {
        var entities = viewport.Entities;
        for (var i = entities.Count - 1; i >= 0; i--)
        {
            var rect = EntityRect(viewport, entities[i]);
            if (rect.Contains(point))
                return entities[i];
        }

        return null;
    }

    private EntityRectInfo EntityRect(ViewportViewModel viewport, SceneEntityDto entity)
        => EntityRect(viewport, entity, TryGetTexture(viewport.ProjectRoot, entity));

    private static EntityRectInfo EntityRect(ViewportViewModel viewport, SceneEntityDto entity, SKBitmap? texture)
    {
        var baseW = texture?.Width ?? (entity.Type.Equals("Camera", StringComparison.OrdinalIgnoreCase) ? 36f : entity.Type.Equals("Empty", StringComparison.OrdinalIgnoreCase) ? 28f : 48f);
        var baseH = texture?.Height ?? baseW;
        var width = MathF.Max(8f, baseW * entity.ScaleX);
        var height = MathF.Max(8f, baseH * entity.ScaleY);
        return new EntityRectInfo(entity.X, entity.Y, width, height, baseW, baseH);
    }

    private static SKColor PickColor(string type)
        => type.ToUpperInvariant() switch
        {
            "TILEMAP" => SKColors.DarkSlateGray,
            "CAMERA" => SKColors.MediumPurple,
            "EMPTY" => SKColor.Parse("#9ccbea"),
            "SPRITE" or _ => SKColor.Parse("#2d9cff"),
        };
}
#pragma warning restore CS0618

internal readonly record struct EntityRectInfo(double Left, double Top, double Width, double Height, double BaseWidth, double BaseHeight)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
    public double CenterX => Left + Width * 0.5;
    public double CenterY => Top + Height * 0.5;
    public float MidX => (float)CenterX;
    public float MidY => (float)CenterY;

    public bool Contains(Point point)
        => point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;

    public SKRect InflateCopy(float x, float y)
        => new((float)Left - x, (float)Top - y, (float)Right + x, (float)Bottom + y);

    public static implicit operator SKRect(EntityRectInfo rect)
        => new((float)rect.Left, (float)rect.Top, (float)rect.Right, (float)rect.Bottom);
}

internal static class SkRectExtensions
{
    public static SKRect InflateCopy(this SKRect rect, float x, float y)
    {
        rect.Inflate(x, y);
        return rect;
    }
}
