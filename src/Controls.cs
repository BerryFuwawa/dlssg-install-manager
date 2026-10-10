using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DlssgManager
{
    public static class Theme
    {
        public static readonly Color Background = Color.FromArgb(246, 248, 252);
        public static readonly Color Sidebar = Color.FromArgb(239, 243, 249);
        public static readonly Color Ink = Color.FromArgb(29, 40, 65);
        public static readonly Color Muted = Color.FromArgb(108, 120, 143);
        public static readonly Color Accent = Color.FromArgb(0, 103, 192);
        public static readonly Color Line = Color.FromArgb(228, 233, 243);
        public static Font Font(float points, bool bold)
        {
            return new Font("Microsoft YaHei UI", points * 96f / 72f, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        }
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure(); return path;
        }
        public static Label Label(string text, float size, Color color, bool bold)
        {
            return new Label { Text = text, AutoSize = false, Font = Font(size, bold),
                ForeColor = color, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        }
    }
    public sealed class RoundButton : Button
    {
        public Color Fill = Theme.Accent;
        public Color TextColor = Color.White;
        public string Glyph;
        public bool LeftAligned, Selected, Danger;
        public float DrawingScale = 1;
        int Px(int value) { return (int)Math.Round(value * DrawingScale, MidpointRounding.AwayFromZero); }
        public bool Dot;
        public Color DotColor = Color.FromArgb(231, 69, 85);
        bool hover, pressed;
        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            Font = Theme.Font(9.5f, true);
            Cursor = Cursors.Hand; Height = 42; TabStop = true;
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? Theme.Background : Parent.BackColor);
            Color color = !Enabled ? Color.FromArgb(228, 233, 242) : Danger && hover ? Color.FromArgb(196, 43, 28) : pressed ? ControlPaint.Dark(Fill, .10f) : hover ? ControlPaint.Light(Fill, .10f) : Fill;
            Rectangle r = new Rectangle(0, pressed ? Px(2) : 0, Width - 1, Height - (pressed ? Px(3) : 1));
            using (var path = Theme.Round(r, Px(12)))
            using (var brush = new SolidBrush(color)) e.Graphics.FillPath(brush, path);
            Rectangle text = r; if (Dot) { text.X += Px(14); text.Width -= Px(14); }
            if (LeftAligned) { text.X = Px(49); text.Width -= Px(54); }
            Color foreground = !Enabled ? Theme.Muted : Danger && hover ? Color.White : TextColor;
            TextRenderer.DrawText(e.Graphics, Text, Font, text, foreground,
                (LeftAligned ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (!String.IsNullOrEmpty(Glyph))
                using (var font = new Font("Segoe MDL2 Assets", 16 * DrawingScale, FontStyle.Regular, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(e.Graphics, Glyph, font, new Rectangle(Px(17), 0, Px(23), Height), foreground, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            if (Selected)
                using (var path = Theme.Round(new Rectangle(Px(1), Height / 2 - Px(10), Px(3), Px(20)), 1.5f * DrawingScale))
                using (var brush = new SolidBrush(Theme.Accent)) e.Graphics.FillPath(brush, path);
            if (Dot)
                using (var b = new SolidBrush(DotColor)) e.Graphics.FillEllipse(b, Px(14), Height / 2 - Px(4), Px(8), Px(8));
            if (Focused && ShowFocusCues)
                using (var path = Theme.Round(new Rectangle(Px(3), Px(3), Width - Px(7), Height - Px(7)), Px(10)))
                using (var pen = new Pen(TextColor) { DashStyle = DashStyle.Dot }) e.Graphics.DrawPath(pen, path);
        }
    }
    public sealed class Card : Panel
    {
        public Color Surface = Color.White;
        public float DrawingScale = 1;
        public Card() { DoubleBuffered = true; Padding = new Padding(22); BackColor = Color.White; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Theme.Background : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), 20 * DrawingScale))
            using (var fill = new SolidBrush(Surface)) e.Graphics.FillPath(fill, path);
        }
    }
    public sealed class RoundProgress : Control
    {
        int current;
        public int Value { get { return current; } set { current = Math.Max(0, Math.Min(10, value)); Invalidate(); } }
        public RoundProgress() { DoubleBuffered = true; BackColor = Color.White; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(new Rectangle(0, 0, Width, Height), Height / 2f))
            using (var brush = new SolidBrush(Theme.Line)) e.Graphics.FillPath(brush, path);
            if (current > 0)
                using (var path = Theme.Round(new Rectangle(0, 0, Width * current / 10, Height), Height / 2f))
                using (var brush = new SolidBrush(Theme.Accent)) e.Graphics.FillPath(brush, path);
        }
    }
}
