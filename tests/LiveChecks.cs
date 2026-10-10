using System;
using System.Diagnostics;
using System.IO;
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
                Console.WriteLine("LIVE PACKAGE " + first.Version + " " + first.Commit + " " + first.DllSize + " " + first.DllHash);
                var cached = installer.CheckCacheAsync(Console.WriteLine, CancellationToken.None).GetAwaiter().GetResult();
                if (cached.Source != "缓存命中") throw new Exception("Live cache reuse failed");
            }
            for (int i = 1; i <= 3; i++)
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
