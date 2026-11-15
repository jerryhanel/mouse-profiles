using System.Drawing.Drawing2D;

namespace MouseProfiles;

/// <summary>
/// The see-through preset banner. It is click-through and never takes focus, so it can pop up over a
/// game or a document without disturbing it, then fades away.
/// </summary>
sealed class OverlayForm : Form
{
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;

    readonly System.Windows.Forms.Timer timer = new() { Interval = 30 };
    readonly Font titleFont = new("Segoe UI Semibold", 28f, FontStyle.Regular, GraphicsUnit.Point);
    readonly Font subFont = new("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Point);
    string title = "", sub = "";
    double targetOpacity = 0.75;
    DateTime fadeStart;
    int holdMs = 1500;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(20, 22, 28);
        DoubleBuffered = true;
        Opacity = 0;
        timer.Tick += (_, _) => Tick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    public void ShowPreset(string name, string detail, OverlayConfig cfg)
    {
        title = name;
        sub = detail;
        targetOpacity = cfg.Opacity;
        holdMs = cfg.DurationMs;

        using (var g = CreateGraphics())
        {
            var t = g.MeasureString(title, titleFont);
            var s = g.MeasureString(sub, subFont);
            Size = new Size((int)Math.Max(t.Width, s.Width) + 80, (int)(t.Height + s.Height) + 50);
        }
        using (var path = Rounded(new Rectangle(Point.Empty, Size), 22))
            Region = new Region(path);

        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        int y = cfg.Position switch
        {
            "Top" => area.Top + 60,
            "Bottom" => area.Bottom - Height - 60,
            _ => area.Top + (area.Height - Height) / 2,
        };
        Location = new Point(area.Left + (area.Width - Width) / 2, y);

        Opacity = targetOpacity;
        if (!Visible) Show();
        Invalidate();
        fadeStart = DateTime.UtcNow.AddMilliseconds(holdMs);
        timer.Start();
    }

    void Tick()
    {
        var late = (DateTime.UtcNow - fadeStart).TotalMilliseconds;
        if (late <= 0) return;
        double fade = 1 - late / 400.0;
        if (fade <= 0) { timer.Stop(); Hide(); return; }
        Opacity = targetOpacity * fade;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var accent = new Pen(Color.FromArgb(90, 160, 255), 3);
        e.Graphics.DrawLine(accent, 40, 12, Width - 40, 12);
        var fmt = new StringFormat { Alignment = StringAlignment.Center };
        var t = e.Graphics.MeasureString(title, titleFont);
        e.Graphics.DrawString(title, titleFont, Brushes.White, new RectangleF(0, 22, Width, t.Height), fmt);
        e.Graphics.DrawString(sub, subFont, Brushes.LightGray, new RectangleF(0, 22 + t.Height, Width, 30), fmt);
    }

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); titleFont.Dispose(); subFont.Dispose(); }
        base.Dispose(disposing);
    }
}
