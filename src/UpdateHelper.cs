using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

static class UpdateHelper
{
    static string Hash(string path)
    {
        using (var sha = SHA256.Create()) using (var s = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "");
    }
    [STAThread]
    static void Main(string[] args)
    {
        string current = null, backup = null;
        bool replaced = false;
        string sibling = null;
        try
        {
            if (args.Length != 4) throw new IOException("更新参数无效。");
            current = Path.GetFullPath(args[1]);
            string stage = Path.GetFullPath(args[2]);
            string helperDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!Path.GetDirectoryName(stage).Equals(helperDir, StringComparison.OrdinalIgnoreCase) || stage.Equals(current, StringComparison.OrdinalIgnoreCase))
                throw new IOException("更新暂存路径无效。");
            using (var parent = Process.GetProcessById(Int32.Parse(args[0])))
            {
                if (!parent.MainModule.FileName.Equals(current, StringComparison.OrdinalIgnoreCase)) throw new IOException("更新目标与正在运行的程序不匹配。");
                File.WriteAllText(stage + ".ready", "ready");
                if (!parent.WaitForExit(60000)) throw new IOException("旧程序尚未退出，更新已取消。");
            }
            if (Hash(stage) != args[3] || AssemblyName.GetAssemblyName(stage).Name != "DLSSG-Install-Manager")
                throw new IOException("更新文件二次校验失败。");
            backup = current + ".previous-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".exe";
            // Staging may be on a different drive; replacement must stay on the target volume.
            sibling = current + ".update-" + Guid.NewGuid().ToString("N") + ".exe";
            File.Copy(stage, sibling, false);
            if (Hash(sibling) != args[3]) throw new IOException("更新文件跨盘复制校验失败。");
            for (int i = 0; ; i++)
            {
                try { File.Replace(sibling, current, backup); replaced = true; break; }
                catch (IOException) { if (i >= 20) throw; Thread.Sleep(250); }
            }
            Process.Start(new ProcessStartInfo(current) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(current) });
        }
        catch (Exception e)
        {
            if (replaced && backup != null && File.Exists(backup))
            {
                try { File.Copy(backup, current, true); } catch { }
            }
            MessageBox.Show("GUI 自动更新未完成，原程序和数据保留。\r\n" + e.Message +
                "\r\n可到 GitHub Releases 手动下载更新。", "DLSSG 更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            try { if (sibling != null && File.Exists(sibling)) File.Delete(sibling); } catch { }
        }
    }
}
