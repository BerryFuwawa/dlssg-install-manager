using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DlssgManager;

sealed class FakeNetwork : HttpMessageHandler
{
    public string Sha = new string('a', 40);
    public bool Offline, BadDll, BadIni, FailDownload, CancelDownload;
    public int Downloads, Checks;
    public string ReleaseJson;
    public byte[] UpdateBytes;
    public string UpdateJson;
    public static byte[] Dll()
    {
        var bytes = new byte[1024 * 1024];
        using (var writer = new BinaryWriter(new MemoryStream(bytes)))
        {
            writer.Write((ushort)0x5A4D); writer.BaseStream.Position = 0x3C; writer.Write(128);
            writer.BaseStream.Position = 128; writer.Write(0x4550); writer.Write((ushort)0x8664);
            writer.BaseStream.Position = 150; writer.Write((ushort)0x2000);
        }
        return bytes;
    }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string url = request.RequestUri.AbsoluteUri;
        if (Offline) throw new HttpRequestException("simulated offline");
        if (url.EndsWith("releases/latest")) return Reply(ReleaseJson == null ? HttpStatusCode.NotFound : HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson ?? ""));
        if (url.Contains("BerryFuwawa") && url.EndsWith("update-manifest.json")) return Reply(HttpStatusCode.OK, Encoding.UTF8.GetBytes(UpdateJson ?? "{}"));
        if (url.Contains("BerryFuwawa") && url.EndsWith(".exe")) return Reply(HttpStatusCode.OK, UpdateBytes ?? new byte[0]);
        if (url.EndsWith("commits/HEAD")) { Checks++; return Reply(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{\"sha\":\"" + Sha + "\"}")); }
        Downloads++;
        if (!url.Contains("/" + Sha + "/")) throw new Exception("Mixed commit download");
        if (CancelDownload) throw new OperationCanceledException();
        if (FailDownload) return Reply(HttpStatusCode.NotFound, new byte[0]);
        if (url.EndsWith("version.dll")) return Reply(HttpStatusCode.OK, BadDll ? new byte[1024] : Dll());
        if (url.EndsWith("dlssg_sm86.ini")) return Reply(HttpStatusCode.OK, Encoding.UTF8.GetBytes(BadIni ? "[bad]\nfoo=bar" : "[Runtime]\nMode=Bundled\n[FrameGeneration]\nMaxGeneratedFrames=3\nOptimized=1\n[Logging]\nLevel=1\n"));
        return Reply(HttpStatusCode.OK, Encoding.UTF8.GetBytes("# DLSSG for SM86 (Proxy) 0.3.5\n"));
    }
    static Task<HttpResponseMessage> Reply(HttpStatusCode status, byte[] body)
    {
        return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
    }
}
sealed class FakeProbe : IProcessProbe
{
    public bool Running;
    public int Calls, RestartAt;
    public bool IsRunning(string path) { Calls++; return Running || (RestartAt > 0 && Calls >= RestartAt); }
}
static class Tests
{
    static int passed;
    static readonly Action<string> Log = delegate { };
    static readonly CancellationToken None = CancellationToken.None;
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        passed++; Console.WriteLine("PASS " + name);
    }
    static async Task Fails(Func<Task> action, string name)
    {
        bool failed = false; try { await action(); } catch { failed = true; } Check(failed, name);
    }
    static void Fails(Action action, string name)
    {
        bool failed = false; try { action(); } catch { failed = true; } Check(failed, name);
    }
    static string NewDirectory(string parent, string name)
    {
        string path = Path.Combine(parent, name); Directory.CreateDirectory(path); return path;
    }
    static async Task Run(string root)
    {
        var net = new FakeNetwork(); var probe = new FakeProbe();
        using (var upstream = new Upstream(net))
        {
            var installer = new Installer(upstream, probe, Path.Combine(root, "documents"));
            Package first = await installer.CheckCacheAsync(Log, None);
            Check(first.Source == "在线更新" && net.Downloads == 3, "first cache download");
            Check(Data.Hash(Path.Combine(installer.CacheRoot, "version.dll")) == first.DllHash, "cache DLL hash");
            Check(first.Version == "0.3.5" && first.Commit == net.Sha, "same commit package and README version");
            Package hit = await installer.CheckCacheAsync(Log, None);
            Check(hit.Source == "缓存命中" && net.Downloads == 3, "cache reuse without DLL redownload");
            net.Sha = new string('b', 40);
            Package updated = await installer.CheckCacheAsync(Log, None);
            Check(updated.Commit == net.Sha && net.Downloads == 6, "remote SHA change refreshes cache");
            net.Offline = true;
            Check((await installer.CheckCacheAsync(Log, None)).Source == "离线缓存", "offline verified cache allowed");
            string dll = Path.Combine(installer.CacheRoot, "version.dll"); File.AppendAllText(dll, "tamper");
            await Fails(() => installer.CheckCacheAsync(Log, None), "offline corrupt cache rejected");
            net.Offline = false;
            await installer.CheckCacheAsync(Log, None);
            Check(net.Downloads == 9, "tampered cache redownloaded");
            net.Sha = new string('c', 40); net.FailDownload = true;
            Check((await installer.CheckCacheAsync(Log, None)).Source == "离线缓存", "failed refresh preserves previous cache");
            net.FailDownload = false;
            string gameDir = NewDirectory(root, "game"); string exe = Path.Combine(gameDir, "game.exe"); File.WriteAllText(exe, "fake game");
            string originalDll = Path.Combine(gameDir, "version.dll"); File.WriteAllText(originalDll, "original plugin");
            probe.Running = true; int beforeDownloads = net.Downloads;
            await Fails(() => installer.InstallAsync(exe, Log, None), "running game blocks before downloads or backups");
            Check(net.Downloads == beforeDownloads && !Directory.Exists(installer.BackupRoot), "running check causes no backup write");
            probe.Running = false;
            Backup b = await installer.InstallAsync(exe, Log, None);
            Check(b.State == "已安装" && File.Exists(Path.Combine(b.Directory, "manifest.json")), "central backup created before install");
            Check(File.ReadAllText(Path.Combine(b.Directory, "原文件备份", "version.dll")) == "original plugin", "original file backup verified");
            Check(Data.Hash(originalDll) == b.Files.Single(f => f.Name == "version.dll").InstalledHash, "deployed DLL hash");
            Check(!Directory.Exists(Path.Combine(gameDir, "DLSS帧生成备份")), "new backups never written into game directory");
            Check(installer.Backups(exe).Count == 1, "central backup discovery");
            string ini = Path.Combine(gameDir, "dlssg_sm86.ini"); string iniContent = File.ReadAllText(ini);
            File.AppendAllText(ini, "; changed");
            Fails(() => installer.Restore(b, Log), "modified installed file not silently deleted");
            Check(Data.Hash(originalDll) == b.Files[0].InstalledHash, "restore preflight writes no target on failure");
            File.WriteAllText(ini, iniContent, new UTF8Encoding(false));
            probe.Running = true; Fails(() => installer.Restore(b, Log), "restore requires game exit"); probe.Running = false;
            installer.Restore(b, Log);
            Check(File.ReadAllText(originalDll) == "original plugin" && !File.Exists(ini), "restore copies originals and removes only installed additions");
            Check(Directory.GetDirectories(b.Directory, "恢复前现状_*").Length >= 1, "pre-restore snapshot kept");
            Backup second = await installer.InstallAsync(exe, Log, None);
            string backupFile = Path.Combine(second.Directory, "原文件备份", "version.dll"); File.AppendAllText(backupFile, "bad");
            Fails(() => installer.Restore(second, Log), "tampered original backup rejected");
            string installedHash = Data.Hash(originalDll);
            Backup selected = installer.Backups(exe).First(x => x.Directory == second.Directory);
            second.TargetDirectory = NewDirectory(root, "other-target"); Data.Json(Path.Combine(second.Directory, "manifest.json"), second);
            Fails(() => installer.Restore(selected, Log), "manifest target changed after selection rejected");
            Check(Data.Hash(originalDll) == installedHash, "changed manifest does not modify game");

            probe.Calls = 0; probe.RestartAt = 2;
            int backupCount = Directory.GetDirectories(installer.BackupRoot).Length;
            await Fails(() => installer.InstallAsync(exe, Log, None), "game restarting during download blocked");
            Check(Directory.GetDirectories(installer.BackupRoot).Length == backupCount, "restart after download creates no backup");
            probe.Calls = 0; probe.RestartAt = 3;
            await Fails(() => installer.InstallAsync(exe, Log, None), "game restarting after backup blocked");
            Check(Data.Hash(originalDll) == installedHash, "restart after backup causes no replacement");
            probe.RestartAt = 0;

            // Simulate a write failure on the second file; first DLL must roll back.
            if (File.Exists(ini)) File.Delete(ini);
            Directory.CreateDirectory(ini); File.WriteAllText(originalDll, "rollback baseline");
            await Fails(() => installer.InstallAsync(exe, Log, None), "second-file deployment failure reported");
            Check(File.ReadAllText(originalDll) == "rollback baseline", "partial deployment automatically rolls back first file");
            Directory.Delete(ini);

            string legacy = NewDirectory(NewDirectory(gameDir, "DLSS帧生成备份"), "备份_旧版");
            var legacyFiles = new[] { new Dictionary<string, object> { {"名称","version.dll"},{"原路径",originalDll},{"安装前存在",false},{"备份相对路径",null},{"安装前SHA256",null},{"安装后SHA256",installedHash} },
                new Dictionary<string, object> { {"名称","dlssg_sm86.ini"},{"原路径",ini},{"安装前存在",false},{"备份相对路径",null},{"安装前SHA256",null},{"安装后SHA256",new string('A',64)} } };
            Data.Json(Path.Combine(legacy,"恢复清单.json"), new Dictionary<string,object> { {"部署时间","2026-01-01T10:00:00+08:00"},{"游戏主程序",exe},{"目标目录",gameDir},{"项目版本","0.3.2"},{"来源提交",new string('d',40)},{"文件",legacyFiles} });
            Check(installer.Backups(exe).Any(x => x.Legacy), "legacy Chinese manifest discovery");
            legacyFiles[0]["安装后SHA256"] = Data.Hash(originalDll);
            Data.Json(Path.Combine(legacy,"恢复清单.json"), new Dictionary<string,object> { {"部署时间","2026-01-01T10:00:00+08:00"},{"游戏主程序",exe},{"目标目录",gameDir},{"项目版本","0.3.2"},{"来源提交",new string('d',40)},{"文件",legacyFiles} });
            installer.Restore(installer.Backups(exe).First(x => x.Legacy), Log);
            Check(!File.Exists(originalDll), "legacy backup restore with guarded deletion");
            await installer.InstallAsync(exe, Log, None);
            installer.SetLogLevel(exe, 3, Log);
            Check(File.ReadAllText(ini).Contains("Level=3"), "log level changes after backup");
            Check(installer.InstallationStatus(exe).Contains(Data.Hash(ini)), "status reports actual installed hashes");
            probe.Running = true; Fails(() => installer.SetLogLevel(exe, 1, Log), "log level change blocked while game runs"); probe.Running = false;
            using (var locked = new FileStream(Path.Combine(installer.Root, ".operation.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await Fails(() => installer.CheckCacheAsync(Log, None), "second manager cannot mutate cache concurrently");
        }
        foreach (string mode in new[] { "offline", "bad-dll", "bad-ini", "404", "cancel" })
        {
            var network = new FakeNetwork { Offline = mode == "offline", BadDll = mode == "bad-dll", BadIni = mode == "bad-ini", FailDownload = mode == "404", CancelDownload = mode == "cancel" };
            using (var upstream = new Upstream(network))
            {
                var installer = new Installer(upstream, new FakeProbe(), Path.Combine(root, mode));
                await Fails(() => installer.CheckCacheAsync(Log, None), "no cache safe failure: " + mode);
                Check(!Directory.Exists(installer.CacheRoot) && !Directory.Exists(installer.BackupRoot) && Directory.GetDirectories(installer.Root, ".cache-stage-*").Length == 0, "no partial official cache: " + mode);
            }
        }
        Check(!GpuDetector.Eligible("LockApp", @"C:\Games\LockApp.exe", 95), "LockApp excluded even at high load");
        Check(!GpuDetector.Eligible("game", null, 80), "unknown process path rejected");
        Check(!GpuDetector.Eligible("game", @"C:\Games\game.exe", Double.NaN), "NaN GPU sample rejected");
        Check(!GpuDetector.Eligible("game", @"C:\Games\game.exe", Double.PositiveInfinity), "infinite GPU sample rejected");
        Check(!GpuDetector.Eligible("game", @"C:\Games\game.exe", 0), "zero GPU sample rejected");
        Check(GpuDetector.Eligible("game", @"C:\Games\game.exe", 70), "valid GPU sample accepted");
        Check(!GuiUpdater.IsNewer("v2.0.0") && GuiUpdater.IsNewer("v2.0.1") && !GuiUpdater.IsNewer("invalid"), "GUI version comparison");
        var updateNet = new FakeNetwork();
        using (var updater = new GuiUpdater(updateNet))
        {
            Check(await updater.Check(None) == null, "no release is not a fake update");
            updateNet.ReleaseJson = "{\"tag_name\":\"v2.0.1\",\"body\":\"new\",\"assets\":[{\"name\":\"DLSSG-Install-Manager.exe\",\"browser_download_url\":\"https://github.com/BerryFuwawa/dlssg-install-manager/releases/download/v2.0.1/DLSSG-Install-Manager.exe\"},{\"name\":\"update-manifest.json\",\"browser_download_url\":\"https://github.com/BerryFuwawa/dlssg-install-manager/releases/download/v2.0.1/update-manifest.json\"}]}";
            var release = await updater.Check(None);
            Check(release.Version == "2.0.1", "new GUI release detected for red-light UI");
            updateNet.UpdateBytes = new byte[20000];
            updateNet.UpdateJson = "{\"Schema\":1,\"Version\":\"2.0.1\",\"File\":\"DLSSG-Install-Manager.exe\",\"Size\":20000,\"Sha256\":\"" + new string('0',64) + "\"}";
            await Fails(() => updater.Download(release, root, Log, None), "bad GUI download hash never reaches replacement");
            Check(Directory.GetDirectories(Path.Combine(root, "程序更新")).Length == 0, "failed GUI update stage cleaned");
            string fixtureNew = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", "new", "DLSSG-Install-Manager.exe");
            updateNet.UpdateBytes = File.ReadAllBytes(fixtureNew);
            updateNet.UpdateJson = "{\"Schema\":1,\"Version\":\"2.0.1\",\"File\":\"DLSSG-Install-Manager.exe\",\"Size\":" + updateNet.UpdateBytes.Length + ",\"Sha256\":\"" + Data.Hash(fixtureNew) + "\"}";
            string stage = await updater.Download(release, root, Log, None);
            Check(File.Exists(stage), "valid GUI assembly downloaded and version verified");
            string oldDirectory = NewDirectory(root, "updater-old");
            string oldExe = Path.Combine(oldDirectory, "DLSSG-Install-Manager.exe");
            File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", "old", "DLSSG-Install-Manager.exe"), oldExe);
            using (var parent = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(oldExe, "\"" + stage + "\"") { UseShellExecute = false, CreateNoWindow = true }))
            {
                if (!parent.WaitForExit(10000)) throw new Exception("Update fixture did not exit");
                Check(parent.ExitCode == 0, "update helper handshake before old process exits");
            }
            for (int i = 0; i < 200 && !File.Exists(oldExe + ".updated-marker"); i++) await Task.Delay(50);
            Check(File.Exists(oldExe + ".updated-marker") && Data.Hash(oldExe) == Data.Hash(fixtureNew), "GUI replacement and restart end-to-end");
            Check(Directory.GetFiles(oldDirectory, "*.previous-*.exe").Length == 1, "GUI update retains old executable backup");
            updateNet.ReleaseJson = updateNet.ReleaseJson.Replace("https://github.com/BerryFuwawa", "https://example.org/BerryFuwawa");
            await Fails(() => updater.Check(None), "foreign update download URL rejected");
        }
        using (var current = System.Diagnostics.Process.GetCurrentProcess())
            Check(!String.IsNullOrWhiteSpace(ProcessProbe.ImagePath(current.Id)), "native process path API live test");
    }
    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "dlssg-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { Run(root).GetAwaiter().GetResult(); Console.WriteLine("TOTAL " + passed + " passed"); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { if (Data.Under(root, Path.GetTempPath())) Directory.Delete(root, true); }
    }
}
