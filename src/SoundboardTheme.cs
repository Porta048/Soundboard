using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal sealed class DarkStripRenderer : ToolStripProfessionalRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(Color.FromArgb(32, 32, 32));
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        using (var brush = new SolidBrush(e.Item.Selected ? Color.FromArgb(65, 65, 65) : Color.FromArgb(32, 32, 32)))
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Color.FromArgb(205, 205, 205) : Color.Gray;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
}

internal static class AudioArtwork
{
    public static Bitmap Create(string name, int size)
    {
        var image = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(image))
        using (var brush = new SolidBrush(Color.FromArgb(4, 101, 182)))
        using (var pen = new Pen(brush, 2))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.ScaleTransform(size / 24f, size / 24f);
            switch (name)
            {
                case "play":
                    graphics.FillPolygon(brush, new[] { new PointF(5, 4), new PointF(21, 12), new PointF(5, 20) });
                    break;
                case "pause":
                    graphics.FillRectangle(brush, 5, 4, 5, 16);
                    graphics.FillRectangle(brush, 14, 4, 5, 16);
                    break;
                case "stop":
                    graphics.FillRectangle(brush, 4, 4, 16, 16);
                    break;
                case "previous":
                    graphics.FillRectangle(brush, 3, 4, 3, 16);
                    graphics.FillPolygon(brush, new[] { new PointF(20, 4), new PointF(7, 12), new PointF(20, 20) });
                    break;
                case "next":
                    graphics.FillPolygon(brush, new[] { new PointF(4, 4), new PointF(17, 12), new PointF(4, 20) });
                    graphics.FillRectangle(brush, 18, 4, 3, 16);
                    break;
                case "headphones":
                    graphics.DrawArc(pen, 4, 4, 16, 17, 180, 180);
                    graphics.FillRectangle(brush, 3, 12, 5, 9);
                    graphics.FillRectangle(brush, 16, 12, 5, 9);
                    break;
                case "microphone":
                    graphics.FillEllipse(brush, 9, 3, 6, 12);
                    graphics.DrawArc(pen, 6, 8, 12, 11, 0, 180);
                    graphics.DrawLine(pen, 12, 18, 12, 22);
                    graphics.DrawLine(pen, 8, 22, 16, 22);
                    break;
                case "home":
                    graphics.FillPolygon(brush, new[] { new PointF(1, 11), new PointF(12, 0), new PointF(23, 11), new PointF(20, 14), new PointF(18, 12), new PointF(18, 23), new PointF(14, 23), new PointF(14, 16), new PointF(10, 16), new PointF(10, 23), new PointF(6, 23), new PointF(6, 12), new PointF(4, 14) });
                    break;
            }
        }
        return image;
    }
}

internal sealed class AudioSlider : Control
{
    private int sliderValue;
    public bool ShowThumb { get; set; }
    public event EventHandler ValueChanged;

    public int Value
    {
        get { return sliderValue; }
        set
        {
            var next = Math.Max(0, Math.Min(1000, value));
            if (sliderValue == next) return;
            sliderValue = next;
            Invalidate();
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }
    }

    public AudioSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(32, 32, 32);
        AccessibleRole = AccessibleRole.Slider;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var track = new Rectangle(4, Height / 2 - 4, Math.Max(1, Width - 8), 7);
        using (var pen = new Pen(Color.FromArgb(4, 101, 182)))
        using (var brush = new SolidBrush(Color.FromArgb(4, 101, 182)))
        {
            e.Graphics.DrawRectangle(pen, track);
            var position = track.Left + track.Width * Value / 1000;
            e.Graphics.FillRectangle(brush, track.Left, track.Top + 1, position - track.Left, track.Height - 1);
            if (ShowThumb) e.Graphics.FillRectangle(brush, position - 4, track.Top - 4, 9, track.Height + 8);
        }
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        Capture = true;
        Value = (e.X - 4) * 1000 / Math.Max(1, Width - 8);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Capture && e.Button == MouseButtons.Left) Value = (e.X - 4) * 1000 / Math.Max(1, Width - 8);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        Capture = false;
        base.OnMouseUp(e);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Home || keyData == Keys.End || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) Value -= 20;
        else if (e.KeyCode == Keys.Right) Value += 20;
        else if (e.KeyCode == Keys.Home) Value = 0;
        else if (e.KeyCode == Keys.End) Value = 1000;
        else return;
        e.Handled = true;
    }
}
