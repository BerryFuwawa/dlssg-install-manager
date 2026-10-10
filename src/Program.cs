using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[assembly: AssemblyTitle("DLSSG Install Manager")]
[assembly: AssemblyProduct("DLSSG Install Manager")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]
[assembly: AssemblyCopyright("Copyright © 2026 DLSSG Install Manager contributors")]

namespace DlssgManager
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) {
                MessageBox.Show("操作失败：" + e.Exception.Message, "DLSSG", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            try
            {
                bool smoke = args.Length == 2 && args[0] == "--smoke-test";
                using (var form = new MainForm(smoke))
                {
                    if (smoke)
                    {
                        form.Shown += delegate {
                            form.BeginInvoke(new Action(delegate {
                                using (var bitmap = new Bitmap(form.Width, form.Height))
                                {
                                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                                    bitmap.Save(Path.GetFullPath(args[1]), System.Drawing.Imaging.ImageFormat.Png);
                                }
                                form.Close();
                            }));
                        };
                    }
                    Application.Run(form);
                }
            }
            catch (Exception e) { MessageBox.Show(e.Message, "无法启动 DLSSG", MessageBoxButtons.OK, MessageBoxIcon.Error); Environment.ExitCode = 1; }
        }
    }
}
