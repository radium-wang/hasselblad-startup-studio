using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
namespace StartupStudio;

public sealed class CameraPreview : Control
{
    private StartupImage? image; private WriteableBitmap? bitmap;
    public StartupImage? Image { get => image; set { bitmap?.Dispose(); image = value; bitmap = value == null ? null : Images.Bitmap(value); InvalidateVisual(); } }
    public static Rect CenterRect(StartupImage image) => new((1024 - image.Width) / 2, (768 - image.Height) / 2, image.Width, image.Height);
    public override void Render(DrawingContext context)
    {
        var k = Math.Min(Bounds.Width / 1024, Bounds.Height / 768); var canvas = new Rect((Bounds.Width - 1024 * k) / 2, (Bounds.Height - 768 * k) / 2, 1024 * k, 768 * k);
        context.FillRectangle(Brushes.Black, canvas);
        if (image == null || bitmap == null) return;
        var r = CenterRect(image); var target = new Rect(canvas.X + r.X * k, canvas.Y + r.Y * k, r.Width * k, r.Height * k);
        context.DrawImage(bitmap, new Rect(0, 0, image.Width, image.Height), target);
    }
}
public sealed class CropEditor : Control
{
    public CropEditor() { Focusable = true; }
    private PhotoDocument? image; private WriteableBitmap? bitmap; private Rect crop;
    private enum DragKind { None, Move, TopLeft, TopRight, BottomLeft, BottomRight, Left, Top, Right, Bottom, Draw }
    private Point dragStart; private Rect startRect; private DragKind kind; private bool dragging, active, drawNewSelection;
    private IPointer? captured;
    public bool Active
    {
        get => active;
        set { active = value; if (!value) { CancelDrag(); DrawNewSelection = false; } InvalidateVisual(); }
    }
    public Action? ToolChanged { get; set; }
    public bool DrawNewSelection
    {
        get => drawNewSelection;
        set { if (drawNewSelection == value) return; drawNewSelection = value; Cursor = new Cursor(value ? StandardCursorType.Cross : StandardCursorType.Arrow); ToolChanged?.Invoke(); }
    }
    public Action<Rect>? Changed { get; set; }
    public Rect Crop
    {
        get => crop;
        set
        {
            if (image == null) crop = default;
            else
            {
                var x = Math.Clamp(Math.Round(value.X), 0, image.Width - 1); var y = Math.Clamp(Math.Round(value.Y), 0, image.Height - 1);
                crop = new(x, y, Math.Clamp(Math.Round(value.Width), 1, image.Width - x), Math.Clamp(Math.Round(value.Height), 1, image.Height - y));
            }
            InvalidateVisual(); Changed?.Invoke(crop);
        }
    }
    public PhotoDocument? Image { get => image; set { CancelDrag(); DrawNewSelection = false; bitmap?.Dispose(); image = value; bitmap = value == null ? null : Images.Bitmap(value); Crop = value?.FullImage ?? default; } }
    public Rect ImageViewport
    {
        get { if (image == null) return default; var k = Math.Min(Math.Max(0, Bounds.Width - 24) / image.Width, Math.Max(0, Bounds.Height - 24) / image.Height); return new((Bounds.Width - image.Width * k) / 2, (Bounds.Height - image.Height * k) / 2, image.Width * k, image.Height * k); }
    }
    private Rect Frame => ImageViewport;
    private Rect View(Rect r) { var f = Frame; var k = f.Width / image!.Width; return new(f.X + r.X * k, f.Y + r.Y * k, r.Width * k, r.Height * k); }
    private Point Pixel(Point point) { var f = Frame; var k = f.Width / image!.Width; return new(Math.Clamp((point.X - f.X) / k, 0, image.Width), Math.Clamp((point.Y - f.Y) / k, 0, image.Height)); }
    private DragKind Hit(Point point)
    {
        var r = View(crop);
        var corners = new[] { (r.TopLeft, DragKind.TopLeft), (r.TopRight, DragKind.TopRight), (r.BottomLeft, DragKind.BottomLeft), (r.BottomRight, DragKind.BottomRight) };
        double Distance(Point p) => Math.Pow(point.X - p.X, 2) + Math.Pow(point.Y - p.Y, 2);
        var nearest = corners.OrderBy(c => Distance(c.Item1)).First();
        if (Distance(nearest.Item1) <= 144) return nearest.Item2;
        // Hit the whole edge, not just its visible midpoint handle.
        if (point.Y >= r.Top && point.Y <= r.Bottom)
        {
            if (Math.Abs(point.X - r.Left) <= 8) return DragKind.Left;
            if (Math.Abs(point.X - r.Right) <= 8) return DragKind.Right;
        }
        if (point.X >= r.Left && point.X <= r.Right)
        {
            if (Math.Abs(point.Y - r.Top) <= 8) return DragKind.Top;
            if (Math.Abs(point.Y - r.Bottom) <= 8) return DragKind.Bottom;
        }
        return r.Contains(point) ? DragKind.Move : DragKind.None;
    }
    private void SetCursor(Point point)
    {
        var hit = Hit(point);
        Cursor = new Cursor(hit switch
        {
            DragKind.Left or DragKind.Right => StandardCursorType.SizeWestEast,
            DragKind.Top or DragKind.Bottom => StandardCursorType.SizeNorthSouth,
            DragKind.TopLeft or DragKind.BottomRight => StandardCursorType.TopLeftCorner,
            DragKind.TopRight or DragKind.BottomLeft => StandardCursorType.TopRightCorner,
            _ when DrawNewSelection => StandardCursorType.Cross,
            DragKind.Move => StandardCursorType.SizeAll,
            _ => StandardCursorType.Arrow
        });
    }
    private void CancelDrag()
    {
        var pointer = captured; captured = null; dragging = false; pointer?.Capture(null);
    }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse("#111315")), new Rect(Bounds.Size));
        if (image == null || bitmap == null) return; var f = Frame; context.DrawImage(bitmap, new Rect(bitmap.Size), f);
        if (!Active) return;
        var r = View(crop); var shade = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        context.FillRectangle(shade, new Rect(f.X, f.Y, f.Width, Math.Max(0, r.Top - f.Top)));
        context.FillRectangle(shade, new Rect(f.X, r.Bottom, f.Width, Math.Max(0, f.Bottom - r.Bottom)));
        context.FillRectangle(shade, new Rect(f.X, r.Top, Math.Max(0, r.Left - f.Left), r.Height));
        context.FillRectangle(shade, new Rect(r.Right, r.Top, Math.Max(0, f.Right - r.Right), r.Height));
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#DF702B")), 1.5), r);
        foreach (var p in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight }) context.FillRectangle(Brushes.White, new Rect(p.X - 4, p.Y - 4, 8, 8));
        foreach (var p in new[] { new Point(r.Center.X, r.Top), new Point(r.Center.X, r.Bottom) }) context.FillRectangle(Brushes.White, new Rect(p.X - 7, p.Y - 3, 14, 6));
        foreach (var p in new[] { new Point(r.Left, r.Center.Y), new Point(r.Right, r.Center.Y) }) context.FillRectangle(Brushes.White, new Rect(p.X - 3, p.Y - 7, 6, 14));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (!Active || image == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Frame.Width <= 0) return;
        var point = e.GetPosition(this); kind = Hit(point);
        // Edges and corners remain adjustable even while the draw tool is armed.
        if (DrawNewSelection && Frame.Contains(point) && kind is DragKind.None or DragKind.Move) kind = DragKind.Draw;
        if (kind == DragKind.None) return;
        dragStart = Pixel(point); startRect = crop; dragging = true; captured = e.Pointer;
        Focus(); e.Pointer.Capture(this); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); if (!Active || image == null || Frame.Width <= 0) return;
        if (!dragging) { SetCursor(e.GetPosition(this)); return; }
        var p = Pixel(e.GetPosition(this));
        if (kind == DragKind.Move) Crop = new(Math.Clamp(startRect.X + p.X - dragStart.X, 0, image.Width - startRect.Width), Math.Clamp(startRect.Y + p.Y - dragStart.Y, 0, image.Height - startRect.Height), startRect.Width, startRect.Height);
        else if (kind is DragKind.Left or DragKind.Top or DragKind.Right or DragKind.Bottom)
        {
            // Keep the opposite edge and the other dimension fixed; crossing
            // the opposite edge clamps to one pixel instead of flipping sides.
            var x = kind == DragKind.Left ? Math.Clamp(p.X, 0, startRect.Right - 1) : startRect.Left;
            var y = kind == DragKind.Top ? Math.Clamp(p.Y, 0, startRect.Bottom - 1) : startRect.Top;
            var right = kind == DragKind.Right ? Math.Clamp(p.X, startRect.Left + 1, image.Width) : startRect.Right;
            var bottom = kind == DragKind.Bottom ? Math.Clamp(p.Y, startRect.Top + 1, image.Height) : startRect.Bottom;
            Crop = new(x, y, right - x, bottom - y);
        }
        else
        {
            var anchor = kind switch { DragKind.TopLeft => startRect.BottomRight, DragKind.TopRight => startRect.BottomLeft, DragKind.BottomLeft => startRect.TopRight, DragKind.BottomRight => startRect.TopLeft, _ => dragStart };
            if (kind == DragKind.Draw && (Math.Abs(p.X - anchor.X) < 1 || Math.Abs(p.Y - anchor.Y) < 1)) return;
            var x = Math.Clamp(Math.Min(anchor.X, p.X), 0, image.Width - 1); var y = Math.Clamp(Math.Min(anchor.Y, p.Y), 0, image.Height - 1);
            Crop = new(x, y, Math.Clamp(Math.Abs(p.X - anchor.X), 1, image.Width - x), Math.Clamp(Math.Abs(p.Y - anchor.Y), 1, image.Height - y));
        }
        e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); if (!dragging) return; var drawn = kind == DragKind.Draw; CancelDrag(); if (drawn) DrawNewSelection = false; SetCursor(e.GetPosition(this)); Changed?.Invoke(crop); e.Handled = true; }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); captured = null; dragging = false; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (e.Key != Key.Escape || (!dragging && !DrawNewSelection)) return;
        var restore = dragging; var previous = startRect; CancelDrag(); DrawNewSelection = false; if (restore) Crop = previous; e.Handled = true;
    }
    public void ZoomSelection(double factor)
    {
        if (!Active || image == null) return;
        var width = Math.Clamp(crop.Width * factor, 1, image.Width); var height = Math.Clamp(crop.Height * factor, 1, image.Height);
        Crop = new(Math.Clamp(crop.Center.X - width / 2, 0, image.Width - width), Math.Clamp(crop.Center.Y - height / 2, 0, image.Height - height), width, height);
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e); if (!Active || image == null) return;
        ZoomSelection(Math.Pow(1.12, -Math.Clamp(e.Delta.Y, -5, 5))); e.Handled = true;
    }
}
