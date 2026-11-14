using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DlssgManager;
static class LiveChecks
{
    static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        if (Directory.Exists(root)) throw new IOException("Live test root must be new.");
        try
        {
            using (var upstream = new Upstream())
            {
                var installer = new Installer(upstream, new ProcessProbe(), root);
                var first = installer.CheckCacheAsync(Console.WriteLine, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine("LIVE PACKAGE " + first.Version + " " + first.Commit + " files=" + first.Files.Count + " bytes=" + first.Files.Sum(file => file.Size));
                var cached = installer.CheckCacheAsync(Console.WriteLine, CancellationToken.None).GetAwaiter().GetResult();
                if (cached.Source != "缓存命中") throw new Exception("Live cache reuse failed");
                string game = Path.Combine(root, "isolated-game"); Directory.CreateDirectory(game);
                string exe = Path.Combine(game, "__dlssg_fixture_game__.exe"); File.WriteAllText(exe, "isolated fixture");
                File.WriteAllText(Path.Combine(game, "version.dll"), "original plugin");
                string obsolete = Path.Combine(game, "dlssg_sm86", "obsolete.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(obsolete)); File.WriteAllText(obsolete, "original payload");
                var backup = installer.InstallAsync(exe, Console.WriteLine, CancellationToken.None).GetAwaiter().GetResult();
                if (File.Exists(obsolete) || first.Files.Where(file => Data.Managed(file.Name)).Any(file => Data.Hash(Data.FileAt(game, file.Name)) != file.Hash)) throw new Exception("Live full deployment mismatch");
                string log = Path.Combine(game, "dlssg_sm86", "logs", "session.log");
                Directory.CreateDirectory(Path.GetDirectoryName(log)); File.WriteAllText(log, "retained session");
                installer.SetLogLevel(exe, 2, Console.WriteLine);
                var config = installer.Backups(exe).First(record => record.State == "已修改日志等级");
                if (config.Files.Count != 1) throw new Exception("Configuration backup scope mismatch");
                installer.Restore(config, Console.WriteLine);
                installer.Restore(backup, Console.WriteLine);
                if (File.ReadAllText(Path.Combine(game, "version.dll")) != "original plugin" || File.Exists(Path.Combine(game, "dlssg_sm86.ini")) || File.ReadAllText(obsolete) != "original payload" || File.ReadAllText(log) != "retained session") throw new Exception("Live restore mismatch");
                Console.WriteLine("LIVE complete deployment, INI-only backup, restore and log preservation passed.");
            }
            for (int i = 1; args.Length > 1 && args[1] == "--gpu" && i <= 3; i++)
            {
                int samples = 0; double previous = 0; var clock = Stopwatch.StartNew();
                try
                {
                    var result = GpuDetector.Sample((n, elapsed) => {
                        Console.WriteLine(String.Format("GPU RUN {0} SAMPLE {1}: {2:F3}s interval {3:F3}s", i, n, elapsed, elapsed - previous));
                        if (elapsed - previous < .90 || elapsed - previous > 1.20) throw new Exception("GPU interval outside tolerance");
                        previous = elapsed; samples++;
                    }, CancellationToken.None);
                    Console.WriteLine("GPU valid candidates: " + result.Count);
                }
                catch (IOException e) { Console.WriteLine("GPU safe no-target result: " + e.Message); }
                if (samples != 10 || clock.Elapsed.TotalSeconds < 9.9) throw new Exception("GPU timing failed");
            }
            Console.WriteLine("LIVE CHECKS PASSED"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally
        {
            if (Path.GetFileName(root).StartsWith("live-check-", StringComparison.Ordinal) && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
