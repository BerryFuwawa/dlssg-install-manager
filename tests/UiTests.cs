using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DlssgManager;
static class UiTests
{
    static int count;
    [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] static extern bool AreDpiAwarenessContextsEqual(IntPtr left, IntPtr right);
    static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value); }
    static void Call(object value, string method, params object[] args) { value.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(value, args); }
    static void Check(bool good, string name) { if (!good) throw new Exception(name); Console.WriteLine("UI PASS " + name); count++; }
    static bool ButtonsFit(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is RoundButton && !parent.ClientRectangle.Contains(child.Bounds)) return false;
            if (!ButtonsFit(child)) return false;
        }
        return true;
    }
    [STAThread]
    static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.EnableVisualStyles();
        try
        {
            using (var form = new MainForm(true))
            {
                form.Opacity = 0; form.Show(); Application.DoEvents();
                Check(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(form.Handle), new IntPtr(-4)), "embedded manifest enables native PerMonitorV2");
                Check(form.LayoutDpi == DpiAwareness.ForWindow(form.Handle), "startup DPI matches native monitor");
                var nav = Field<List<RoundButton>>(form, "navigation");
                string[] pages = { "home", "backupsPage", "cachePage", "settingsPage", "logsPage" };
                for (int i = 0; i < nav.Count; i++) { nav[i].PerformClick(); Application.DoEvents(); Check(Field<Panel>(form, pages[i]).Visible, "navigation page " + i); }
                nav[0].PerformClick();
                Check(!Field<RoundButton>(form, "install").Enabled, "install disabled without game");
                Call(form, "SelectGame", @"D:\Games\ExampleGame\game.exe");
                Check(Field<RoundButton>(form, "verify").Enabled && !Field<RoundButton>(form, "install").Enabled, "manual selection requires separate exit check");
                form.DisplayUpdate(new GuiUpdate { Version = "2.0.1" });
                var update = Field<RoundButton>(form, "update");
                Check(update.Dot && update.Text.Contains("2.0.1"), "bottom-right red update light");
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(args[0], System.Drawing.Imaging.ImageFormat.Png);
                }
                form.DisplayUpdate(null); Check(!update.Dot, "red update light clears at current version");
                RoundButton repository = Field<RoundButton>(form, "repositoryLink");
                Check(repository.Text == "GitHub 仓库 ↗" && Convert.ToString(repository.Tag) == GuiUpdater.Repository, "sidebar opens owner repository instead of upstream");
                foreach (int dpi in new[] { 96, 120, 144, 168, 192, 240, 288 })
                {
                    form.ApplyDpiForTest(dpi, new Size(3840, 2400)); Application.DoEvents();
                    float scale = dpi / 96f;
                    var detect = Field<RoundButton>(form, "detect"); var grid = Field<DataGridView>(form, "candidates");
                    Console.WriteLine("DPI " + dpi + " actual=" + form.LayoutDpi + " font=" + detect.Font.Size + " button=" + detect.Bounds);
                    Check(Math.Abs(detect.Font.Size - 9.5f * 96f / 72f * scale) < .02 && detect.Height == (int)Math.Round(42 * scale, MidpointRounding.AwayFromZero), "font and button scale at " + dpi);
                    Check(Math.Abs(detect.Left - 22 * scale) <= 1 && Math.Abs(detect.Top - 94 * scale) <= 1 && Math.Abs(detect.Width - 180 * scale) <= 1, "logical button geometry at " + dpi);
                    Check(grid.ColumnHeadersHeight == (int)Math.Round(36 * scale) && Math.Abs(grid.Columns[0].Width - 175 * scale) <= 1 && Math.Abs(grid.RowTemplate.Height - 35 * scale) <= 1, "grid scales at " + dpi);
                    Check(Math.Abs(detect.DrawingScale - scale) < .001 && Math.Abs(nav[0].DrawingScale - scale) < .001, "rounded drawing and glyph scale at " + dpi);
                    Check(detect.Parent.ClientRectangle.Contains(detect.Bounds) && Field<RoundButton>(form, "install").Parent.ClientRectangle.Contains(Field<RoundButton>(form, "install").Bounds), "primary actions remain inside cards at " + dpi);
                    bool everyPageFits = true;
                    for (int page = 0; page < nav.Count; page++)
                    {
                        Console.WriteLine("[DEBUG-ci-dpi] enter dpi=" + dpi + " page=" + page);
                        nav[page].PerformClick(); Application.DoEvents();
                        everyPageFits &= ButtonsFit(Field<Panel>(form, pages[page]));
                        if (dpi == 144 && page == 2)
                            using (var bitmap = new Bitmap(form.Width, form.Height))
                            {
                                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                                bitmap.Save(Path.Combine(Path.GetDirectoryName(args[0]), "cache-dpi-144.png"), System.Drawing.Imaging.ImageFormat.Png);
                            }
                    }
                    Check(everyPageFits, "all five page actions fit at " + dpi);
                    nav[0].PerformClick(); Application.DoEvents();
                    if (dpi == 96 || dpi == 144 || dpi == 192)
                    {
                        using (var bitmap = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                            bitmap.Save(Path.Combine(Path.GetDirectoryName(args[0]), "dpi-" + dpi + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                }
                // Exercise the actual WM_DPICHANGED handler, not only the layout seam.
                IntPtr nativeRect = Marshal.AllocHGlobal(16);
                try
                {
                    Marshal.Copy(new[] { 0, 0, 1740, 1170 }, 0, nativeRect, 4);
                    Message message = Message.Create(form.Handle, 0x02E0, new IntPtr(144 | (144 << 16)), nativeRect);
                    typeof(FluentForm).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { message });
                    Check(form.LayoutDpi == 144 && Field<RoundButton>(form, "detect").Height == 63, "native WM_DPICHANGED updates complete layout");
                }
                finally { Marshal.FreeHGlobal(nativeRect); }
                for (int i = 0; i < 5; i++) foreach (int dpi in new[] { 120, 192, 96 }) form.ApplyDpiForTest(dpi, new Size(3840, 2400));
                var baseline = Field<RoundButton>(form, "detect");
                Check(baseline.Bounds == new Rectangle(22, 94, 180, 42) && Math.Abs(baseline.Font.Size - 9.5f * 96f / 72f) < .02, "repeated monitor DPI changes cause no rounding drift");
                form.ApplyDpiForTest(192, new Size(1920, 1040)); Application.DoEvents();
                var surface = (Panel)typeof(FluentForm).GetField("Surface", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                Check(form.Width <= 1920 && form.Height <= 1040 && surface.HorizontalScroll.Visible && surface.VerticalScroll.Visible, "200 percent on 1080p fits screen and enables scrolling");
                surface.ScrollControlIntoView(update); Application.DoEvents();
                Check(surface.ClientRectangle.Contains(new Rectangle(surface.PointToClient(update.PointToScreen(Point.Empty)), update.Size)), "GUI update button reachable in compact high-DPI window");
                surface.ScrollControlIntoView(repository); Application.DoEvents();
                Check(surface.ClientRectangle.Contains(new Rectangle(surface.PointToClient(repository.PointToScreen(Point.Empty)), repository.Size)), "repository button reachable in compact high-DPI window");
                form.Close();
            }
            Console.WriteLine("UI TOTAL " + count + " passed"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
