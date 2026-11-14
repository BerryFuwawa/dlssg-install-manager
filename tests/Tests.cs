using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using DlssgManager;

sealed class FakeNetwork : HttpMessageHandler
{
    public string Sha = new string('a', 40);
    public bool Offline, BadDll, BadIni, FailDownload, CancelDownload, MissingRuntime, Truncated;
    public string ExtraFile;
    public int Downloads, Checks;
    public string ReleaseJson;
    public byte[] UpdateBytes;
    public string UpdateJson;
    public static byte[] Dll()
    {
        var bytes = new byte[8192];
        using (var writer = new BinaryWriter(new MemoryStream(bytes)))
        {
            writer.Write((ushort)0x5A4D); writer.BaseStream.Position = 0x3C; writer.Write(128);
            writer.BaseStream.Position = 128; writer.Write(0x4550); writer.Write((ushort)0x8664);
            writer.BaseStream.Position = 150; writer.Write((ushort)0x2000);
        }
        return bytes;
    }
    Dictionary<string, byte[]> Payload()
    {
        var files = new Dictionary<string, byte[]> {
            { "README.md", Encoding.UTF8.GetBytes("# DLSSG for SM86 (Proxy) 0.4.2\n") },
            { "version.dll", BadDll ? new byte[1024] : Dll() },
            { "dlssg_sm86.ini", Encoding.UTF8.GetBytes(BadIni ? "[bad]\nfoo=bar" : "[Runtime]\nMode=Bundled\n[FrameGeneration]\nEngine=DLSS\nMaxGeneratedFrames=3\n[DLSSG]\nOptimized=1\nPreset=A\n[Logging]\nLevel=1\n") }
        };
        foreach (string name in Data.Required.Where(name => name.StartsWith("dlssg_sm86/"))) files.Add(name, Dll());
        files.Add("dlssg_sm86/xefg/xefg_kernels.dll", Dll());
        if (MissingRuntime) files.Remove("dlssg_sm86/runtime/nvngx_dlssg.dll");
        if (ExtraFile != null) files.Add(ExtraFile, Dll());
        return files;
    }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string url = request.RequestUri.AbsoluteUri;
        if (Offline) throw new HttpRequestException("simulated offline");
        if (url.EndsWith("releases/latest")) return Reply(ReleaseJson == null ? HttpStatusCode.NotFound : HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson ?? ""));
        if (url.Contains("BerryFuwawa") && url.EndsWith("update-manifest.json")) return Reply(HttpStatusCode.OK, Encoding.UTF8.GetBytes(UpdateJson ?? "{}"));
        if (url.Contains("BerryFuwawa") && url.EndsWith(".exe")) return Reply(HttpStatusCode.OK, UpdateBytes ?? new byte[0]);
        if (url.EndsWith("commits/HEAD")) { Checks++; return Reply(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{\"sha\":\"" + Sha + "\"}")); }
        if (url.Contains("/git/trees/"))
        {
            if (!url.Contains("/" + Sha + "?")) throw new Exception("Mixed commit tree");
            var tree = Payload().Select(file => new { path = file.Key, type = "blob", size = file.Value.Length }).ToList();
            tree.Add(new { path = "archive/old.dll", type = "blob", size = 8192 });
            tree.Add(new { path = "alternatives/dxgi.dll", type = "blob", size = 8192 });
            tree.Add(new { path = "dlssg_sm86/logs/example.log", type = "blob", size = 123 });
            return Reply(HttpStatusCode.OK, Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new { truncated = Truncated, tree = tree })));
        }
        Downloads++;
        if (!url.Contains("/" + Sha + "/")) throw new Exception("Mixed commit download");
        if (CancelDownload) throw new OperationCanceledException();
        if (FailDownload) return Reply(HttpStatusCode.NotFound, new byte[0]);
        string name = url.Substring(url.IndexOf("/" + Sha + "/", StringComparison.Ordinal) + Sha.Length + 2);
        byte[] body;
        return Payload().TryGetValue(name, out body) ? Reply(HttpStatusCode.OK, body) : Reply(HttpStatusCode.NotFound, new byte[0]);
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
            DownloadProgress finalProgress = null;
            Package first = await installer.CheckCacheAsync(Log, None, progress => finalProgress = progress);
            Check(first.Source == "在线更新" && net.Downloads == 7, "first complete package download excludes archive, alternative proxies and logs");
            Check(Data.Hash(Path.Combine(installer.CacheRoot, "version.dll")) == first.Files.Single(file => file.Name == "version.dll").Hash, "cache DLL hash");
            Check(first.Version == "0.4.2" && first.Commit == net.Sha && first.Schema == 3, "same commit package and README version");
            Check(finalProgress.FileNumber == 7 && finalProgress.CompletedBytes == first.Files.Sum(file => file.Size) && finalProgress.TotalBytes == finalProgress.CompletedBytes, "file and byte progress reaches complete package size");
            Check(first.Files.Single(file => file.Name == "version.dll").Size < 1024 * 1024, "valid proxy smaller than one MiB accepted");
            Package hit = await installer.CheckCacheAsync(Log, None);
            Check(hit.Source == "缓存命中" && net.Downloads == 7, "cache reuse without payload redownload");
            net.Sha = new string('b', 40);
            Package updated = await installer.CheckCacheAsync(Log, None);
            Check(updated.Commit == net.Sha && net.Downloads == 14, "remote SHA change refreshes cache");
            net.Offline = true;
            Check((await installer.CheckCacheAsync(Log, None)).Source == "离线缓存", "offline verified cache allowed");
            string dll = Path.Combine(installer.CacheRoot, "version.dll"); File.AppendAllText(dll, "tamper");
            await Fails(() => installer.CheckCacheAsync(Log, None), "offline corrupt cache rejected");
            net.Offline = false;
            await installer.CheckCacheAsync(Log, None);
            Check(net.Downloads == 21, "tampered cache redownloaded");
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
            Check(b.Schema == 2 && b.Files.Count == 6 && Data.Required.All(name => File.Exists(Data.FileAt(gameDir, name))), "complete payload installed with per-file backup records");
            string logFile = Path.Combine(gameDir, "dlssg_sm86", "logs", "game.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)); File.WriteAllText(logFile, "game session");
            string ini = Path.Combine(gameDir, "dlssg_sm86.ini"); string iniContent = File.ReadAllText(ini);
            File.AppendAllText(ini, "; changed");
            Fails(() => installer.Restore(b, Log), "modified installed file not silently deleted");
            Check(Data.Hash(originalDll) == b.Files[0].InstalledHash, "restore preflight writes no target on failure");
            File.WriteAllText(ini, iniContent, new UTF8Encoding(false));
            probe.Running = true; Fails(() => installer.Restore(b, Log), "restore requires game exit"); probe.Running = false;
            installer.Restore(b, Log);
            Check(File.ReadAllText(originalDll) == "original plugin" && !File.Exists(ini), "restore copies originals and removes only installed additions");
            Check(File.ReadAllText(logFile) == "game session" && !File.Exists(Data.FileAt(gameDir, "dlssg_sm86/runtime/nvngx_dlssg.dll")), "restore removes installed payload and preserves runtime logs");
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
            Backup config = installer.Backups(exe).First(record => record.State == "已修改日志等级");
            Check(config.Files.Count == 1 && config.Files[0].Name == "dlssg_sm86.ini" && Directory.GetFiles(Path.Combine(config.Directory, "原文件备份"), "*", SearchOption.AllDirectories).Length == 1, "log-level change backs up INI only");
            Check(installer.InstallationStatus(exe).Contains(Data.Hash(ini)), "status reports actual installed hashes");
            probe.Running = true; Fails(() => installer.SetLogLevel(exe, 1, Log), "log level change blocked while game runs"); probe.Running = false;
            using (var locked = new FileStream(Path.Combine(installer.Root, ".operation.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await Fails(() => installer.CheckCacheAsync(Log, None), "second manager cannot mutate cache concurrently");
        }
        foreach (string mode in new[] { "offline", "bad-dll", "bad-ini", "404", "cancel", "missing-runtime", "truncated-tree" })
        {
            var network = new FakeNetwork { Offline = mode == "offline", BadDll = mode == "bad-dll", BadIni = mode == "bad-ini", FailDownload = mode == "404", CancelDownload = mode == "cancel", MissingRuntime = mode == "missing-runtime", Truncated = mode == "truncated-tree" };
            using (var upstream = new Upstream(network))
            {
                var installer = new Installer(upstream, new FakeProbe(), Path.Combine(root, mode));
                await Fails(() => installer.CheckCacheAsync(Log, None), "no cache safe failure: " + mode);
                Check(!Directory.Exists(installer.CacheRoot) && !Directory.Exists(installer.BackupRoot) && Directory.GetDirectories(installer.Root, ".cache-stage-*").Length == 0, "no partial official cache: " + mode);
            }
        }
        await PayloadScenarios(root);
        foreach (string name in new[] { "NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA GeForce RTX 4070 Ti SUPER", "GeForce RTX 4090" })
            Check(ProcessProbe.GraphicsSupport(name).Contains("实验性支持"), "RTX 40 experimental label: " + name);
        Check(!ProcessProbe.GraphicsSupport("NVIDIA RTX 4000").Contains("40 系"), "workstation RTX 4000 not mistaken for GeForce RTX 40");
        Check(!GpuDetector.Eligible("LockApp", @"C:\Games\LockApp.exe", 95), "LockApp excluded even at high load");
        Check(!GpuDetector.Eligible("game", null, 80), "unknown process path rejected");
        Check(!GpuDetector.Eligible("game", @"C:\Games\game.exe", Double.NaN), "NaN GPU sample rejected");
        Check(!GpuDetector.Eligible("game", @"C:\Games\game.exe", Double.PositiveInfinity), "infinite GPU sample rejected");
        Check(!GpuDetector.Eligible("game", @"C:\Games\game.exe", 0), "zero GPU sample rejected");
        Check(GpuDetector.Eligible("game", @"C:\Games\game.exe", 70), "valid GPU sample accepted");
        Check(!GuiUpdater.IsNewer("v2.1.0") && GuiUpdater.IsNewer("v2.1.1") && !GuiUpdater.IsNewer("invalid"), "GUI version comparison");
        var updateNet = new FakeNetwork();
        using (var updater = new GuiUpdater(updateNet))
        {
            Check(await updater.Check(None) == null, "no release is not a fake update");
            updateNet.ReleaseJson = "{\"tag_name\":\"v2.1.1\",\"body\":\"new\",\"assets\":[{\"name\":\"DLSSG-Install-Manager.exe\",\"browser_download_url\":\"https://github.com/BerryFuwawa/dlssg-install-manager/releases/download/v2.1.1/DLSSG-Install-Manager.exe\"},{\"name\":\"update-manifest.json\",\"browser_download_url\":\"https://github.com/BerryFuwawa/dlssg-install-manager/releases/download/v2.1.1/update-manifest.json\"}]}";
            var release = await updater.Check(None);
            Check(release.Version == "2.1.1", "new GUI release detected for red-light UI");
            release.Version = "2.1.0"; release.ExeUrl = release.ExeUrl.Replace("v2.1.1/", "v2.1.0/"); release.ManifestUrl = release.ManifestUrl.Replace("v2.1.1/", "v2.1.0/");
            updateNet.UpdateBytes = new byte[20000];
            updateNet.UpdateJson = "{\"Schema\":1,\"Version\":\"2.1.0\",\"File\":\"DLSSG-Install-Manager.exe\",\"Size\":20000,\"Sha256\":\"" + new string('0',64) + "\"}";
            await Fails(() => updater.Download(release, root, Log, None), "bad GUI download hash never reaches replacement");
            Check(Directory.GetDirectories(Path.Combine(root, "程序更新")).Length == 0, "failed GUI update stage cleaned");
            string fixtureNew = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", "new", "DLSSG-Install-Manager.exe");
            updateNet.UpdateBytes = File.ReadAllBytes(fixtureNew);
            updateNet.UpdateJson = "{\"Schema\":1,\"Version\":\"2.1.0\",\"File\":\"DLSSG-Install-Manager.exe\",\"Size\":" + updateNet.UpdateBytes.Length + ",\"Sha256\":\"" + Data.Hash(fixtureNew) + "\"}";
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
            Check(File.Exists(oldExe + ".updated-marker") && Data.Hash(oldExe) == Data.Hash(fixtureNew), "GUI 2.0.0 to 2.1.0 replacement and restart end-to-end");
            Check(Directory.GetFiles(oldDirectory, "*.previous-*.exe").Length == 1, "GUI update retains old executable backup");
            updateNet.ReleaseJson = updateNet.ReleaseJson.Replace("https://github.com/BerryFuwawa", "https://example.org/BerryFuwawa");
            await Fails(() => updater.Check(None), "foreign update download URL rejected");
        }
        using (var current = System.Diagnostics.Process.GetCurrentProcess())
            Check(!String.IsNullOrWhiteSpace(ProcessProbe.ImagePath(current.Id)), "native process path API live test");
    }
    static async Task PayloadScenarios(string root)
    {
        string game = NewDirectory(root, "payload-game"), exe = Path.Combine(game, "game.exe"); File.WriteAllText(exe, "fixture");
        string dll = Path.Combine(game, "version.dll"), ini = Path.Combine(game, "dlssg_sm86.ini");
        File.WriteAllText(dll, "original root"); File.WriteAllText(Path.Combine(game, "winmm.dll"), "other plugin");
        string log = Path.Combine(NewDirectory(NewDirectory(game, "dlssg_sm86"), "logs"), "game.log"); File.WriteAllText(log, "session");
        var net = new FakeNetwork { ExtraFile = "dlssg_sm86/old-pack.dll" };
        using (var upstream = new Upstream(net))
        {
            var installer = new Installer(upstream, new FakeProbe(), Path.Combine(root, "payload-documents"));
            Backup original = await installer.InstallAsync(exe, Log, None);
            net.Sha = new string('e', 40); net.ExtraFile = "dlssg_sm86/new-pack.dll";
            Backup update = await installer.InstallAsync(exe, Log, None);
            Check(!File.Exists(Data.FileAt(game, "dlssg_sm86/old-pack.dll")) && File.Exists(Data.FileAt(game, net.ExtraFile)), "update removes obsolete payload and deploys new payload");
            Check(update.Files.Any(file => file.Name == "dlssg_sm86/old-pack.dll" && file.Existed && !file.InstalledExists), "removed payload stored in backup with absent installation state");
            string added = Data.FileAt(game, net.ExtraFile); File.AppendAllText(added, "changed");
            Fails(() => installer.Restore(update, Log), "modified newly installed payload blocks restore");
            File.Copy(Data.FileAt(installer.CacheRoot, net.ExtraFile), added, true);
            installer.Restore(update, Log);
            Check(File.Exists(Data.FileAt(game, "dlssg_sm86/old-pack.dll")) && !File.Exists(added), "restore update reinstates removed payload and removes new payload");
            string userFile = Path.Combine(game, "dlssg_sm86", "notes.txt"); File.WriteAllText(userFile, "keep");
            installer.Restore(original, Log);
            Check(File.ReadAllText(dll) == "original root" && !File.Exists(ini), "restore initial full installation returns original root files");
            Check(File.ReadAllText(log) == "session" && File.ReadAllText(userFile) == "keep", "restore leaves runtime logs and unrecorded user files untouched");
            Check(File.ReadAllText(Path.Combine(game, "winmm.dll")) == "other plugin", "unknown root proxy never deleted");
            string blocked = Data.FileAt(game, "dlssg_sm86/runtime/nvngx_dlssg.dll"); Directory.CreateDirectory(blocked);
            await Fails(() => installer.InstallAsync(exe, Log, None), "payload write failure reported");
            Check(File.ReadAllText(dll) == "original root" && File.ReadAllText(userFile) == "keep" && !File.Exists(Data.FileAt(game, "dlssg_sm86/backend/sm86_backend.dll")), "payload failure rolls back root replacements, removals and earlier payload writes");
            await installer.InstallAsync(exe, Log, None);
            string old = NewDirectory(installer.BackupRoot, "schema-one");
            var legacy = new Backup { Schema = 1, GameExe = exe, TargetDirectory = game, CreatedAt = "2026-01-01", State = "已安装", Version = "0.3.5", Files = new List<FileRecord> {
                new FileRecord { Name = "version.dll", InstalledHash = Data.Hash(dll) }, new FileRecord { Name = "dlssg_sm86.ini", InstalledHash = Data.Hash(ini) } } };
            Data.Json(Path.Combine(old, "manifest.json"), legacy);
            installer.Restore(installer.Backups(exe).Single(record => record.Schema == 1), Log);
            Check(!File.Exists(dll) && !File.Exists(ini) && File.Exists(Data.FileAt(game, "dlssg_sm86/backend/sm86_backend.dll")), "v2.0 schema-one backup restores only its two original paths");
        }
        using (var upstream = new Upstream(new FakeNetwork { MissingRuntime = true }))
        {
            var installer = new Installer(upstream, new FakeProbe(), Path.Combine(root, "missing-component-documents"));
            File.WriteAllText(dll, "do not change");
            await Fails(() => installer.InstallAsync(exe, Log, None), "missing component prevents deployment");
            Check(File.ReadAllText(dll) == "do not change" && !Directory.Exists(installer.BackupRoot), "incomplete package creates no backup and changes no game files");
        }
        var oldNet = new FakeNetwork { Offline = true };
        using (var upstream = new Upstream(oldNet))
        {
            var installer = new Installer(upstream, new FakeProbe(), Path.Combine(root, "old-cache-documents"));
            Directory.CreateDirectory(installer.CacheRoot);
            File.WriteAllText(Path.Combine(installer.CacheRoot, "version.dll"), "old cached DLL");
            Data.Json(Path.Combine(installer.CacheRoot, "缓存清单.json"), new { Schema = 2 });
            await Fails(() => installer.InstallAsync(exe, Log, None), "old two-file cache cannot serve complete installation offline");
            Check(File.ReadAllText(Path.Combine(installer.CacheRoot, "version.dll")) == "old cached DLL" && File.ReadAllText(dll) == "do not change", "failed legacy-cache upgrade preserves old cache and game");
            oldNet.Offline = false;
            Check((await installer.CheckCacheAsync(Log, None)).Schema == 3, "legacy cache rebuilt online as full package");
            string hash = Data.Hash(Path.Combine(installer.CacheRoot, "version.dll")); oldNet.Sha = new string('f', 40);
            using (var cancellation = new CancellationTokenSource())
                await Fails(() => installer.CheckCacheAsync(Log, cancellation.Token, progress => cancellation.Cancel()), "cancellation during real download progress does not commit partial cache");
            Check(Data.Hash(Path.Combine(installer.CacheRoot, "version.dll")) == hash && Directory.GetDirectories(installer.Root, ".cache-stage-*").Length == 0, "cancelled refresh retains valid full cache and removes staging");
        }
    }
    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "dlssg-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { Run(root).GetAwaiter().GetResult(); Console.WriteLine("TOTAL " + passed + " passed"); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { if (Data.Under(root, Path.GetTempPath())) Directory.Delete(root, true); }
    }
}
