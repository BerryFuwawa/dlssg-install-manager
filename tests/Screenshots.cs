using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using DlssgManager;

// Documentation renderer: no downloads, deployment, restore or settings writes.
static class Screenshots
{
    static T Field<T>(object value, string name)
    {
        return (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value);
    }
    static void Save(MainForm form, string directory, string name)
    {
        Application.DoEvents();
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(directory, name), ImageFormat.Png);
        }
        Console.WriteLine("Rendered " + name);
    }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("Expected screenshot output directory.");
            Directory.CreateDirectory(args[0]);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new MainForm(true))
            {
                form.Opacity = 0;
                form.Show(); Application.DoEvents();
                form.ApplyDpiForTest(144, new Size(3840, 2400));
                Field<Label>(form, "graphics").Text = "文档预览模式 · 正常启动时会读取实际显卡信息。";
                Field<Label>(form, "cacheState").Text = "首次使用示意 · 尚未建立缓存";
                var navigation = Field<List<RoundButton>>(form, "navigation");
                string[] names = { "01-install.png", "02-backups.png", "03-cache.png", "04-settings.png", "05-logs.png" };
                for (int i = 0; i < names.Length; i++)
                {
                    navigation[i].PerformClick(); Save(form, args[0], names[i]);
                }
                navigation[0].PerformClick();
                form.DisplayUpdate(new GuiUpdate { Version = "2.0.1" });
                Save(form, args[0], "06-update-demo.png");
                form.Close();
            }
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
