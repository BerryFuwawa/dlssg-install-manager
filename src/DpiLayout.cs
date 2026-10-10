using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DlssgManager
{
    public static class DpiAwareness
    {
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        public static int ForWindow(IntPtr window)
        {
            try { uint value = GetDpiForWindow(window); if (value > 0) return (int)value; }
            catch (EntryPointNotFoundException) { }
            using (Graphics graphics = Graphics.FromHwnd(window)) return (int)Math.Round(graphics.DpiX);
        }
    }
    // Keep a 96-DPI design snapshot, not incrementally rounded pixels. Repeated
    // monitor changes therefore cannot accumulate font, margin or column drift.
    sealed class DpiLayout : IDisposable
    {
        sealed class FontSpec
        {
            public string Family;
            public float Pixels;
            public FontStyle Style;
            public FontSpec(Font font) { Family = font.FontFamily.Name; Pixels = font.Unit == GraphicsUnit.Pixel ? font.Size : font.SizeInPoints * 96f / 72f; Style = font.Style; }
            public Font Create(float scale) { return new Font(Family, Pixels * scale, Style, GraphicsUnit.Pixel); }
        }
        sealed class Metric
        {
            public Control Control;
            public Rectangle Bounds;
            public Padding Padding, Margin;
            public Size Minimum, Maximum;
            public FontSpec Font;
            public int HeaderHeight, RowHeight, RowHeaderWidth;
            public FontSpec HeaderFont, CellFont;
            public Padding CellPadding;
            public int[] ColumnWidths, ColumnMinimums;
            public float[] AbsoluteRows, AbsoluteColumns;
        }
        readonly List<Metric> metrics = new List<Metric>();
        readonly List<Font> allocatedFonts = new List<Font>();
        readonly FluentForm form;
        public DpiLayout(FluentForm form)
        {
            this.form = form;
            Capture(form);
        }
        void Capture(Control control)
        {
            var metric = new Metric { Control = control, Bounds = control.Bounds, Padding = control.Padding, Margin = control.Margin,
                Minimum = control.MinimumSize, Maximum = control.MaximumSize, Font = new FontSpec(control.Font) };
            var grid = control as DataGridView;
            if (grid != null)
            {
                metric.HeaderHeight = grid.ColumnHeadersHeight; metric.RowHeight = grid.RowTemplate.Height; metric.RowHeaderWidth = grid.RowHeadersWidth;
                metric.HeaderFont = new FontSpec(grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font);
                metric.CellFont = new FontSpec(grid.DefaultCellStyle.Font ?? grid.Font); metric.CellPadding = grid.DefaultCellStyle.Padding;
                metric.ColumnWidths = grid.Columns.Cast<DataGridViewColumn>().Select(c => c.Width).ToArray();
                metric.ColumnMinimums = grid.Columns.Cast<DataGridViewColumn>().Select(c => c.MinimumWidth).ToArray();
            }
            var table = control as TableLayoutPanel;
            if (table != null)
            {
                metric.AbsoluteRows = table.RowStyles.Cast<RowStyle>().Select(s => s.SizeType == SizeType.Absolute ? s.Height : -1).ToArray();
                metric.AbsoluteColumns = table.ColumnStyles.Cast<ColumnStyle>().Select(s => s.SizeType == SizeType.Absolute ? s.Width : -1).ToArray();
            }
            metrics.Add(metric);
            // Scrollbars are system-DPI controls, not part of the design snapshot.
            foreach (Control child in control.Controls) if (!(child is ScrollBar)) Capture(child);
        }
        static int Pixel(int value, float scale) { return (int)Math.Round(value * scale, MidpointRounding.AwayFromZero); }
        static Size Scale(Size value, float scale) { return new Size(Pixel(value.Width, scale), Pixel(value.Height, scale)); }
        static Rectangle Scale(Rectangle value, float scale) { return new Rectangle(Pixel(value.X, scale), Pixel(value.Y, scale), Pixel(value.Width, scale), Pixel(value.Height, scale)); }
        static Padding Scale(Padding value, float scale) { return new Padding(Pixel(value.Left, scale), Pixel(value.Top, scale), Pixel(value.Right, scale), Pixel(value.Bottom, scale)); }
        Font NewFont(FontSpec spec, float scale)
        {
            Font font = spec.Create(scale); allocatedFonts.Add(font); return font;
        }
        public void Apply(int dpi, Rectangle bounds, Rectangle workArea)
        {
            float scale = dpi / 96f;
            Font[] previousFonts = allocatedFonts.ToArray(); allocatedFonts.Clear();
            foreach (Metric metric in metrics) metric.Control.SuspendLayout();
            try
            {
                form.MinimumSize = Size.Empty;
                // Reset scroll offsets before calculating the new display rectangle.
                foreach (Metric metric in metrics)
                {
                    var scroll = metric.Control as ScrollableControl;
                    if (scroll != null && scroll.AutoScroll) scroll.AutoScrollPosition = Point.Empty;
                }
                foreach (Metric metric in metrics)
                {
                    Control control = metric.Control;
                    control.Font = NewFont(metric.Font, scale);
                    control.Padding = Scale(metric.Padding, scale); control.Margin = Scale(metric.Margin, scale);
                    if (control != form)
                    {
                        control.MinimumSize = Scale(metric.Minimum, scale); control.MaximumSize = Scale(metric.Maximum, scale);
                        control.Bounds = Scale(metric.Bounds, scale);
                    }
                    var button = control as RoundButton; if (button != null) button.DrawingScale = scale;
                    var card = control as Card; if (card != null) card.DrawingScale = scale;
                    var grid = control as DataGridView;
                    if (grid != null)
                    {
                        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
                        grid.ColumnHeadersHeight = Pixel(metric.HeaderHeight, scale);
                        grid.RowTemplate.Height = Pixel(metric.RowHeight, scale); grid.RowHeadersWidth = Pixel(metric.RowHeaderWidth, scale);
                        grid.ColumnHeadersDefaultCellStyle.Font = NewFont(metric.HeaderFont, scale);
                        grid.DefaultCellStyle.Font = NewFont(metric.CellFont, scale); grid.DefaultCellStyle.Padding = Scale(metric.CellPadding, scale);
                        for (int i = 0; i < grid.Columns.Count; i++)
                        {
                            grid.Columns[i].MinimumWidth = Pixel(metric.ColumnMinimums[i], scale);
                            if (grid.Columns[i].AutoSizeMode != DataGridViewAutoSizeColumnMode.Fill) grid.Columns[i].Width = Pixel(metric.ColumnWidths[i], scale);
                        }
                        foreach (DataGridViewRow row in grid.Rows) row.Height = grid.RowTemplate.Height;
                    }
                    var table = control as TableLayoutPanel;
                    if (table != null)
                    {
                        for (int i = 0; i < metric.AbsoluteRows.Length; i++) if (metric.AbsoluteRows[i] >= 0) table.RowStyles[i].Height = metric.AbsoluteRows[i] * scale;
                        for (int i = 0; i < metric.AbsoluteColumns.Length; i++) if (metric.AbsoluteColumns[i] >= 0) table.ColumnStyles[i].Width = metric.AbsoluteColumns[i] * scale;
                    }
                }
                form.SetContentMinimum(new Size(Pixel(1060, scale), Pixel(690, scale)));
                form.MinimumSize = new Size(Math.Min(Pixel(850, scale), workArea.Width), Math.Min(Pixel(580, scale), workArea.Height));
                if (form.WindowState == FormWindowState.Normal)
                {
                    bounds.Width = Math.Max(form.MinimumSize.Width, Math.Min(bounds.Width, workArea.Width));
                    bounds.Height = Math.Max(form.MinimumSize.Height, Math.Min(bounds.Height, workArea.Height));
                    bounds.X = Math.Max(workArea.Left, Math.Min(bounds.X, workArea.Right - bounds.Width));
                    bounds.Y = Math.Max(workArea.Top, Math.Min(bounds.Y, workArea.Bottom - bounds.Height));
                    form.Bounds = bounds;
                }
            }
            finally
            {
                foreach (Metric metric in metrics) metric.Control.ResumeLayout(true);
                form.RefreshDpiLayout();
                foreach (Font font in previousFonts) font.Dispose();
            }
        }
        public void Dispose() { foreach (Font font in allocatedFonts) font.Dispose(); allocatedFonts.Clear(); }
    }
}
