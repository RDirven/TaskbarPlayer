using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace TaskbarPlayer;

/// <summary>Minimal flat slider (0..1) used for volume and track position.</summary>
internal sealed class SliderBar : Control
{
    private float _value;
    private bool _hover;

    public SliderBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Cursor = Cursors.Hand;
    }

    /// <summary>Raised while the user changes the value (dragging, wheel).</summary>
    public event EventHandler? ValueChanging;

    /// <summary>Raised when the user finishes changing the value.</summary>
    public event EventHandler? ValueCommitted;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float Value
    {
        get => _value;
        set
        {
            float v = Math.Clamp(value, 0f, 1f);
            if (v != _value)
            {
                _value = v;
                Invalidate();
            }
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsDragging { get; private set; }

    /// <summary>Change per mouse-wheel notch; 0 disables the wheel.</summary>
    [DefaultValue(0f)]
    public float WheelStep { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color TrackColor { get; set; } = Color.FromArgb(80, 80, 80);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; } = Color.FromArgb(225, 225, 225);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HoverFillColor { get; set; } = Color.FromArgb(76, 194, 255);

    private float DpiScale => DeviceDpi / 96f;
    private float KnobRadius => 6f * DpiScale;
    private float Pad => KnobRadius + 1;

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        IsDragging = true;
        Capture = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsDragging) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!IsDragging) return;
        SetFromX(e.X);
        IsDragging = false;
        Capture = false;
        Invalidate();
        ValueCommitted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (WheelStep <= 0 || !Enabled) return;
        Value += Math.Sign(e.Delta) * WheelStep;
        ValueChanging?.Invoke(this, EventArgs.Empty);
        ValueCommitted?.Invoke(this, EventArgs.Empty);
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }

    private void SetFromX(int x)
    {
        float width = Width - 2 * Pad;
        if (width <= 0) return;
        Value = (x - Pad) / width;
        ValueChanging?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float thickness = 4f * DpiScale;
        float width = Width - 2 * Pad;
        float cy = Height / 2f;
        bool active = Enabled && (_hover || IsDragging);

        using (var track = new SolidBrush(TrackColor))
        {
            FillPill(g, track, new RectangleF(Pad, cy - thickness / 2, width, thickness));
        }

        if (!Enabled) return;

        float filled = width * _value;
        if (filled > 0)
        {
            using var fill = new SolidBrush(active ? HoverFillColor : FillColor);
            FillPill(g, fill, new RectangleF(Pad, cy - thickness / 2, filled, thickness));
        }

        if (active)
        {
            float r = KnobRadius;
            g.FillEllipse(Brushes.White, Pad + filled - r, cy - r, 2 * r, 2 * r);
        }
    }

    private static void FillPill(Graphics g, Brush brush, RectangleF r)
    {
        float d = r.Height;
        if (r.Width <= d)
        {
            g.FillEllipse(brush, r.X, r.Y, Math.Max(r.Width, 1), d);
            return;
        }
        using var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
