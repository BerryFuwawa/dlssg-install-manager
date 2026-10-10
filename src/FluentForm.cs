using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DlssgManager
{
    // Custom Fluent chrome keeps the same visual language on Windows 10 and 11.
    public class FluentForm : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        protected Panel Surface;
        DpiLayout dpiLayout;
        bool changingDpi;
        int layoutDpi = 96;
        protected event EventHandler DpiLayoutChanged;
        internal int LayoutDpi { get { return layoutDpi; } }
        protected int Px(int value) { return (int)Math.Round(value * layoutDpi / 96.0, MidpointRounding.AwayFromZero); }
        RoundButton maximize;
        protected void CreateFluentChrome()
        {
            FormBorderStyle = FormBorderStyle.None;
            Padding = new Padding(1);
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            Surface = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, AutoScroll = true };
            var title = new Panel { Dock = DockStyle.Top, Height = 43, BackColor = Theme.Background };
            Controls.Add(Surface); Controls.Add(title); Surface.BringToFront();
            var image = new PictureBox { Location = new Point(18, 13), Size = new Size(18, 18), SizeMode = PictureBoxSizeMode.Zoom };
            if (Icon != null) image.Image = Icon.ToBitmap();
            title.Controls.Add(image);
            var label = Theme.Label("DLSS 帧生成安装管理工具", 9, Theme.Ink, false);
            label.SetBounds(45, 0, 480, 43); title.Controls.Add(label);
            Action toggle = delegate { MaximizedBounds = Screen.FromControl(this).WorkingArea; WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; };
            var minimize = Caption("\uE921", delegate { WindowState = FormWindowState.Minimized; });
            maximize = Caption("\uE922", delegate { toggle(); });
            var close = Caption("\uE8BB", delegate { Close(); }); close.Danger = true;
            close.AccessibleName = "关闭"; maximize.AccessibleName = "最大化或还原"; minimize.AccessibleName = "最小化";
            title.Controls.AddRange(new Control[] { minimize, maximize, close });
            EventHandler placeCaption = delegate { close.Left = title.Width - Px(48); maximize.Left = title.Width - Px(94); minimize.Left = title.Width - Px(140); };
            title.Resize += placeCaption; DpiLayoutChanged += placeCaption;
            MouseEventHandler drag = delegate(object sender, MouseEventArgs e) {
                if (e.Button != MouseButtons.Left) return;
                if (e.Clicks == 2) { toggle(); return; }
                ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
            };
            title.MouseDown += drag; label.MouseDown += drag; image.MouseDown += drag;
            Resize += delegate { UpdateCorners(); maximize.Text = WindowState == FormWindowState.Maximized ? "\uE923" : "\uE922"; };
            HandleCreated += delegate { int rounded = 2; try { DwmSetWindowAttribute(Handle, 33, ref rounded, 4); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { } UpdateCorners(); };
        }
        protected void InitializeDpi()
        {
            dpiLayout = new DpiLayout(this);
            int dpi = DpiAwareness.ForWindow(Handle);
            Rectangle workArea = Screen.FromControl(this).WorkingArea;
            int width = (int)Math.Round(1160 * dpi / 96.0), height = (int)Math.Round(780 * dpi / 96.0);
            var initial = new Rectangle(workArea.Left + (workArea.Width - width) / 2, workArea.Top + (workArea.Height - height) / 2, width, height);
            ApplyDpi(dpi, initial, workArea);
        }
        void ApplyDpi(int dpi, Rectangle bounds, Rectangle workArea)
        {
            if (dpiLayout == null || changingDpi || dpi < 48 || dpi > 768) return;
            changingDpi = true;
            try { layoutDpi = dpi; dpiLayout.Apply(dpi, bounds, workArea); }
            finally { changingDpi = false; }
        }
        internal void SetContentMinimum(Size size) { Surface.AutoScrollMinSize = size; }
        internal void RefreshDpiLayout()
        {
            if (DpiLayoutChanged != null) DpiLayoutChanged(this, EventArgs.Empty);
            UpdateCorners(); Invalidate(true);
        }
        internal void ApplyDpiForTest(int dpi, Size workArea)
        {
            ApplyDpi(dpi, new Rectangle(0, 0, (int)Math.Round(1160 * dpi / 96.0), (int)Math.Round(780 * dpi / 96.0)), new Rectangle(Point.Empty, workArea));
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && dpiLayout != null) { dpiLayout.Dispose(); dpiLayout = null; }
        }
        RoundButton Caption(string glyph, EventHandler click)
        {
            var button = new RoundButton { Text = glyph, Size = new Size(46, 34), Top = 4, Fill = Theme.Background, TextColor = Theme.Ink,
                Font = new Font("Segoe MDL2 Assets", 12, FontStyle.Regular, GraphicsUnit.Pixel), TabStop = false };
            button.Click += click; return button;
        }
        void UpdateCorners()
        {
            Region old = Region;
            if (WindowState == FormWindowState.Maximized) Region = null;
            else if (Width > 0 && Height > 0) using (var path = Theme.Round(new Rectangle(0, 0, Width, Height), Px(12))) Region = new Region(path);
            if (old != null) old.Dispose();
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.FromArgb(207, 215, 227)))
            using (var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), WindowState == FormWindowState.Maximized ? 1 : Px(12))) e.Graphics.DrawPath(pen, path);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0 && dpiLayout != null)
            {
                int dpi = (int)(m.WParam.ToInt64() & 0xFFFF);
                var rect = (NativeRect)Marshal.PtrToStructure(m.LParam, typeof(NativeRect));
                Rectangle bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                ApplyDpi(dpi, bounds, Screen.FromRectangle(bounds).WorkingArea);
                m.Result = IntPtr.Zero; return;
            }
            base.WndProc(ref m);
            if (m.Msg != 0x84 || WindowState != FormWindowState.Normal) return;
            long position = m.LParam.ToInt64();
            Point p = PointToClient(new Point(unchecked((short)(position & 0xFFFF)), unchecked((short)((position >> 16) & 0xFFFF))));
            int edge = Px(7);
            bool left = p.X < edge, right = p.X >= Width - edge, top = p.Y < edge, bottom = p.Y >= Height - edge;
            if (top && left) m.Result = new IntPtr(13);
            else if (top && right) m.Result = new IntPtr(14);
            else if (bottom && left) m.Result = new IntPtr(16);
            else if (bottom && right) m.Result = new IntPtr(17);
            else if (left) m.Result = new IntPtr(10);
            else if (right) m.Result = new IntPtr(11);
            else if (top) m.Result = new IntPtr(12);
            else if (bottom) m.Result = new IntPtr(15);
        }
        [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    }
}
