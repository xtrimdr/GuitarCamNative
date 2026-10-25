using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace GuitarCam.App.Controls;

/// <summary>
/// Drag to reorder a chain of effect slots (the wide mixer strip, the Canal tab), animated like the timeline's track
/// reorder: the dragged card follows the pointer, the others glide out of its way to make room where it would land
/// (<see cref="SlideEase"/>, the timeline's), and on release it glides into that room. Only once everything has
/// settled does <see cref="Moved"/> get the slot id and its new index, so the chain rebuilt in the new order looks
/// exactly like what was on screen. The cards move by their own <see cref="TranslateTransform"/> (made once per drag,
/// changed in place every frame: no garbage while it animates). A press that never moves past <see cref="Threshold"/>
/// is left alone.
/// </summary>
public sealed class SlotDrag(double gap)
{
    /// <summary>How far (px) the pointer moves before a press on a handle becomes a drag.</summary>
    const double Threshold = 4;
    /// <summary>Share of the remaining distance a card covers per 16 ms frame (the timeline's: about 150 ms to settle).</summary>
    const double SlideEase = 0.3;

    readonly List<(Control Card, string Id)> items = [];
    TranslateTransform[] shift = [];
    double[] target = [];
    DispatcherTimer? timer;
    int from = -1, landing = -1;
    double startY, pointerDy;
    bool dragging, settling;

    /// <summary>Once the drop has settled: the slot id, the slot it lands next to, and whether it goes after that one.</summary>
    public event Action<string, string, bool>? Moved;

    /// <summary>Makes <paramref name="handle"/> drag <paramref name="card"/> (add the card to the chain's panel, in order). Returns the card.</summary>
    public Control Add(Control card, string id, Control handle)
    {
        int index = items.Count;
        items.Add((card, id));
        handle.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        handle.PointerPressed += (_, e) =>
        {
            if (e.Handled || settling || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed || card.Parent is not Visual host) return;
            e.Handled = true;
            from = index;
            landing = index;
            dragging = false;
            startY = e.GetPosition(host).Y;
            e.Pointer.Capture(handle);
        };
        handle.PointerMoved += (_, e) =>
        {
            if (from != index || settling || card.Parent is not Visual host) return;
            double dy = e.GetPosition(host).Y - startY;
            if (!dragging && Math.Abs(dy) < Threshold) return;
            if (!dragging) Lift();
            pointerDy = dy;
            shift[from].Y = dy;
            int k = LandingFor(items[from].Card.Bounds.Center.Y + dy);
            if (k != landing)
            {
                landing = k;
                MakeRoom();
            }
        };
        handle.PointerReleased += (_, e) =>
        {
            if (from != index || settling) return;
            if (dragging)
            {
                // glide into the room made for it; the capture goes after, so losing it doesn't cancel the drop
                settling = true;
                target[from] = LandingOffset();
                timer!.Start();
            }
            else from = -1;
            e.Pointer.Capture(null);
        };
        handle.PointerCaptureLost += (_, _) =>
        {
            if (from != index || settling) return;
            if (!dragging) { from = -1; return; }
            // cancelled (Esc, another window): everything glides back
            settling = true;
            landing = from;
            Array.Clear(target);
            timer!.Start();
        };
        return card;
    }

    void Lift()
    {
        dragging = true;
        shift = new TranslateTransform[items.Count];
        target = new double[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            shift[i] = new TranslateTransform();
            items[i].Card.RenderTransform = shift[i];
        }
        var card = items[from].Card;
        card.ZIndex = 1;
        card.Opacity = 0.85;
        timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Step());
    }

    /// <summary>Where the dragged card would end (0..count-1) with its center at <paramref name="y"/>: before every other card whose center is lower.</summary>
    int LandingFor(double y)
    {
        int k = 0;
        for (int i = 0; i < items.Count; i++)
            if (i != from && items[i].Card.Bounds.Center.Y < y) k++;
        return k;
    }

    /// <summary>The others slide by the dragged card's height (and the gap) to open its room at <see cref="landing"/>.</summary>
    void MakeRoom()
    {
        double h = items[from].Card.Bounds.Height + gap;
        for (int i = 0; i < items.Count; i++)
            if (i != from) target[i] = from < i && i <= landing ? -h : landing <= i && i < from ? h : 0;
        timer!.Start();
    }

    /// <summary>How far the dragged card moves from its place to its room.</summary>
    double LandingOffset()
    {
        if (landing == from) return 0;
        var me = items[from].Card.Bounds;
        return landing > from ? items[landing].Card.Bounds.Bottom - me.Bottom : items[landing].Card.Bounds.Top - me.Top;
    }

    void Step()
    {
        bool still = true;
        for (int i = 0; i < items.Count; i++)
        {
            // while dragging, the grabbed card is the pointer's
            if (i == from && !settling) continue;
            double y = shift[i].Y, t = target[i];
            y += (t - y) * SlideEase;
            if (Math.Abs(t - y) < 0.5) y = t;
            else still = false;
            shift[i].Y = y;
        }
        if (!still || !settling) { if (still) timer!.Stop(); return; }
        timer!.Stop();
        Drop();
    }

    void Drop()
    {
        int f = from, k = landing;
        from = landing = -1;
        dragging = settling = false;
        bool moved = k >= 0 && k != f;
        // the chain is rebuilt in the new order right here, so the cards that glided never show their old places again
        if (moved) Moved?.Invoke(items[f].Id, items[k].Id, k > f);
        foreach (var (card, _) in items)
        {
            card.RenderTransform = null;
            card.ZIndex = 0;
            card.Opacity = 1;
        }
    }
}

/// <summary>The handle a slot is dragged by: two columns of three dots, Faint, the accent under the pointer.</summary>
public sealed class GripDots : Control
{
    bool hover;

    public GripDots()
    {
        Width = 12;
        Height = 16;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); hover = true; InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); hover = false; InvalidateVisual(); }

    public override void Render(DrawingContext ctx)
    {
        // transparent, so the whole box takes the pointer and not just the dots
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var dot = hover ? Palette.Accent : Palette.Faint;
        double x0 = Bounds.Width / 2 - 3, y0 = Bounds.Height / 2 - 5;
        for (int c = 0; c < 2; c++)
            for (int r = 0; r < 3; r++)
                ctx.FillRectangle(dot, new Rect(x0 + c * 4, y0 + r * 4, 2, 2));
    }
}
