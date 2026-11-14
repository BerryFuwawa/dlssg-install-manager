using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DlssgManager
{
    public sealed class Package
    {
        public int Schema { get; set; }
        public string Commit { get; set; }
        public string Version { get; set; }
        public List<PackageFile> Files { get; set; }
        public string CachedAt { get; set; }
        public string Source { get; set; }
        public string Directory { get; set; }
    }
    public sealed class PackageFile
    {
        public string Name { get; set; }
        public long Size { get; set; }
        public string Hash { get; set; }
    }
    public sealed class DownloadProgress
    {
        public string File;
        public int FileNumber, FileCount;
        public long CompletedBytes, TotalBytes;
    }
    public sealed class FileRecord
    {
        public string Name { get; set; }
        public bool Existed { get; set; }
        public string BeforeHash { get; set; }
        public string InstalledHash { get; set; }
        public bool InstalledExists { get; set; }
    }
    public sealed class Backup
    {
        public int Schema { get; set; }
        public string CreatedAt { get; set; }
        public string GameExe { get; set; }
        public string TargetDirectory { get; set; }
        public string Commit { get; set; }
        public string Version { get; set; }
        public string State { get; set; }
        public List<FileRecord> Files { get; set; }
        public string Directory { get; set; }
        public bool Legacy { get; set; }
    }
    public interface IProcessProbe { bool IsRunning(string exe); }
    public static class Data
    {
        public static readonly string[] Names = { "version.dll", "dlssg_sm86.ini" };
        public static readonly string[] Required = { "version.dll", "dlssg_sm86.ini", "dlssg_sm86/backend/sm86_backend.dll", "dlssg_sm86/kernels/fg_kernels.dll", "dlssg_sm86/runtime/nvngx_dlssg.dll" };
        public static bool Managed(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name)) return false;
            name = name.Replace('\\', '/');
            if (name.Split('/').Any(part => part == "" || part == "." || part == ".." || part.Contains(':'))) return false;
            return Names.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                (name.StartsWith("dlssg_sm86/", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("dlssg_sm86/logs", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("dlssg_sm86/logs/", StringComparison.OrdinalIgnoreCase));
        }
        public static string FileAt(string root, string name)
        {
            if (!Managed(name) && name != "README.md") throw new IOException("文件不在管理范围内：" + name);
            return Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
        }
        public static void Copy(string source, string destination, bool overwrite)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(source, destination, overwrite);
        }
        public static List<string> PayloadFiles(string target)
        {
            string payload = Path.Combine(target, "dlssg_sm86");
            if (!Directory.Exists(payload)) return new List<string>();
            return Directory.GetFiles(payload, "*", SearchOption.AllDirectories)
                .Select(path => path.Substring(target.Length + 1).Replace('\\', '/')).Where(Managed).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }
        public static void RemoveEmptyPayloadDirectories(string target)
        {
            string payload = Path.Combine(target, "dlssg_sm86");
            if (!Directory.Exists(payload)) return;
            foreach (string directory in Directory.GetDirectories(payload, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            {
                string name = directory.Substring(target.Length + 1).Replace('\\', '/');
                if (Managed(name) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            if (!Directory.EnumerateFileSystemEntries(payload).Any()) Directory.Delete(payload);
        }
        public static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
        public static bool IsHash(string value) { return Regex.IsMatch(value ?? "", "^[a-fA-F0-9]{64}$"); }
        public static string Full(string path) { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        public static bool Under(string path, string root)
        {
            return Full(path).StartsWith(Full(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        public static void Json(string path, object value)
        {
            string tmp = path + ".new-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        public static T Read<T>(string path) { return new JavaScriptSerializer().Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)); }
        public static void DeleteTaskDirectory(string path, string parent)
        {
            if (!Under(path, parent)) throw new IOException("拒绝清理范围外的目录。");
            if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
        }
        public static void ValidateDll(string path)
        {
            if (new FileInfo(path).Length < 64) throw new IOException("DLL 缺少完整 PE 标头。");
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (r.ReadUInt16() != 0x5A4D) throw new IOException("下载文件不是 Windows DLL（MZ 标头无效）。");
                r.BaseStream.Position = 0x3C;
                int offset = r.ReadInt32();
                if (offset < 64 || offset > r.BaseStream.Length - 24) throw new IOException("PE 标头偏移无效。");
                r.BaseStream.Position = offset;
                if (r.ReadUInt32() != 0x4550 || r.ReadUInt16() != 0x8664) throw new IOException("下载文件不是 x64 DLL。");
                r.BaseStream.Position = offset + 22;
                if ((r.ReadUInt16() & 0x2000) == 0) throw new IOException("文件缺少 DLL 标志。");
            }
        }
        public static void ValidateIni(string path)
        {
            string section = "";
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                Match s = Regex.Match(line, @"^\[([^\]]+)\]\s*(?:;.*)?$");
                if (s.Success) { section = s.Groups[1].Value.Trim(); continue; }
                Match pair = Regex.Match(line, @"^([^=]+?)\s*=\s*([^;]*)(?:;.*)?$");
                if (!pair.Success) throw new IOException("官方 INI 格式无效。");
                keys[section + "." + pair.Groups[1].Value.Trim()] = pair.Groups[2].Value.Trim();
            }
            string n;
            if (!keys.TryGetValue("FrameGeneration.MaxGeneratedFrames", out n) || !Regex.IsMatch(n, "^[1-5]$"))
                throw new IOException("官方 INI 缺少有效的帧生成配置。");
            if (keys.TryGetValue("FrameGeneration.Optimized", out n) && !Regex.IsMatch(n, "^[0-3]$"))
                throw new IOException("官方 INI 的 Optimized 值无效。");
            if (keys.TryGetValue("DLSSG.Optimized", out n) && !Regex.IsMatch(n, "^[0-3]$")) throw new IOException("DLSSG.Optimized 值无效。");
            if (keys.TryGetValue("DLSSG.Preset", out n) && !Regex.IsMatch(n, "^[ABC]$", RegexOptions.IgnoreCase)) throw new IOException("DLSSG.Preset 值无效。");
            string engine;
            if (keys.TryGetValue("FrameGeneration.Engine", out engine))
            {
                if (!engine.Equals("DLSS", StringComparison.OrdinalIgnoreCase) && !engine.Equals("XeSS", StringComparison.OrdinalIgnoreCase)) throw new IOException("帧生成引擎无效。");
                if (engine.Equals("XeSS", StringComparison.OrdinalIgnoreCase) && Int32.Parse(keys["FrameGeneration.MaxGeneratedFrames"]) > 3) throw new IOException("XeSS 帧生成最多支持 4X。");
            }
            if (keys.TryGetValue("Runtime.Mode", out n) && !n.Equals("Bundled", StringComparison.OrdinalIgnoreCase))
                throw new IOException("当前工具只自动安装官方随包运行库配置。");
        }
        public static string Version(string readme)
        {
            Match m = Regex.Match(readme, @"^\s*#\s*DLSSG[^\r\n]*?\b(\d+\.\d+\.\d+)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(readme, @"DLSSG\s+Native\s+(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);
            if (!m.Success) throw new IOException("无法识别上游项目版本，未进行安装。");
            return m.Groups[1].Value;
        }
    }
    public sealed class Upstream : IDisposable
    {
        public const string Home = "https://github.com/sdli1995/dlssg_for_sm86";
        readonly HttpClient http;
        public Upstream() : this(new HttpClientHandler()) { }
        public Upstream(HttpMessageHandler handler)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            http = new HttpClient(handler);
            http.Timeout = TimeSpan.FromMinutes(3);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DlssgInstallManager/" + GuiUpdater.Current);
        }
        public async Task<string> Commit(CancellationToken token)
        {
            // HEAD resolves the default branch without assuming its name.
            string json = await Text("https://api.github.com/repos/sdli1995/dlssg_for_sm86/commits/HEAD", token);
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            string sha = d.ContainsKey("sha") ? Convert.ToString(d["sha"]) : "";
            if (!Regex.IsMatch(sha, "^[0-9a-fA-F]{40}$")) throw new IOException("GitHub 返回无效提交编号。");
            return sha;
        }
        async Task<string> Text(string url, CancellationToken token)
        {
            using (var r = await http.GetAsync(url, token))
            {
                if (!r.IsSuccessStatusCode) throw new IOException("HTTP " + (int)r.StatusCode + "：" + url);
                return await r.Content.ReadAsStringAsync();
            }
        }
        public async Task<List<PackageFile>> Files(string sha, CancellationToken token)
        {
            string json = await Text("https://api.github.com/repos/sdli1995/dlssg_for_sm86/git/trees/" + sha + "?recursive=1", token);
            var tree = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            if (tree.ContainsKey("truncated") && Convert.ToBoolean(tree["truncated"])) throw new IOException("上游文件列表被截断，未准备不完整的安装包。");
            var files = new List<PackageFile>();
            foreach (Dictionary<string, object> entry in (System.Collections.IEnumerable)tree["tree"])
            {
                string name = Convert.ToString(entry["path"]);
                if (Convert.ToString(entry["type"]) == "blob" && (name == "README.md" || Data.Managed(name)))
                    files.Add(new PackageFile { Name = name, Size = Convert.ToInt64(entry["size"]) });
            }
            if (!Data.Required.Concat(new[] { "README.md" }).All(name => files.Any(file => file.Name == name))) throw new IOException("上游缺少完整 0.4.x 安装包的必需组件。");
            return files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        public async Task Download(string sha, string file, string target, CancellationToken token, Action<long> progress = null)
        {
            string url = "https://raw.githubusercontent.com/sdli1995/dlssg_for_sm86/" + sha + "/" + file;
            using (var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                if (!r.IsSuccessStatusCode) throw new IOException("HTTP " + (int)r.StatusCode + "：" + url);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (var input = await r.Content.ReadAsStreamAsync())
                using (var output = File.Create(target))
                {
                    byte[] buffer = new byte[81920]; int count; long received = 0; var clock = Stopwatch.StartNew();
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, count, token); received += count;
                        if (progress != null && clock.ElapsedMilliseconds >= 200) { progress(received); clock.Restart(); }
                    }
                    if (progress != null) progress(received);
                }
            }
        }
        public void Dispose() { http.Dispose(); }
    }
    public sealed class Installer
    {
        public readonly string Root;
        public string CacheRoot { get { return Path.Combine(Root, "缓存"); } }
        public string BackupRoot { get { return Path.Combine(Root, "备份"); } }
        readonly Upstream upstream;
        readonly IProcessProbe processes;
        public Installer(Upstream upstream, IProcessProbe processes, string root)
        {
            this.upstream = upstream; this.processes = processes;
            Root = Data.Full(root);
        }
        public bool IsRunning(string exe) { return processes.IsRunning(Data.Full(exe)); }
        public string InstallationStatus(string exe)
        {
            string target = ValidateTarget(exe);
            var text = new StringBuilder("程序：" + exe + "\r\n部署目录：" + target + "\r\n\r\n");
            foreach (string name in Data.Required)
            {
                string path = Path.Combine(target, name);
                text.Append(name + (File.Exists(path) ? "\r\nSHA256：" + Data.Hash(path) : "：未安装") + "\r\n\r\n");
            }
            text.Append("载荷文件：" + Data.PayloadFiles(target).Count + " 个（不含日志）\r\n\r\n");
            Backup latest = Backups(exe).FirstOrDefault(b => Data.Full(b.TargetDirectory).Equals(target, StringComparison.OrdinalIgnoreCase));
            if (latest != null) text.Append("最近记录：" + latest.CreatedAt + "\r\n记录的上游版本：" + latest.Version + "\r\n来源提交：" + latest.Commit + "\r\n\r\n");
            text.Append("记录不代表游戏兼容性；文件存在也不代表帧生成已生效。请查看游戏内设置和上游日志。");
            return text.ToString();
        }
        public void SetLogLevel(string exe, int level, Action<string> log)
        {
            if (level < 0 || level > 3) throw new ArgumentOutOfRangeException("level");
            string target = ValidateTarget(exe);
            using (OperationLock())
            {
                if (IsRunning(exe)) throw new IOException("请退出游戏后再修改日志等级。");
                string ini = Path.Combine(target, "dlssg_sm86.ini"), dll = Path.Combine(target, "version.dll");
                if (!File.Exists(ini) || !File.Exists(dll)) throw new IOException("请先安装 DLL 与 INI，再修改日志等级。");
                string content = File.ReadAllText(ini, Encoding.UTF8);
                var section = Regex.Match(content, @"(?ms)^\[Logging\][^\r\n]*\r?\n(?<body>.*?)(?=^\[|\z)");
                if (!section.Success || !Regex.IsMatch(section.Groups["body"].Value, @"(?m)^\s*Level\s*="))
                    throw new IOException("当前 INI 缺少 Logging.Level，请参阅上游说明手动配置。");
                string body = Regex.Replace(section.Groups["body"].Value, @"(?m)^\s*Level\s*=[^\r\n]*", "Level=" + level);
                string modified = content.Substring(0, section.Groups["body"].Index) + body + content.Substring(section.Groups["body"].Index + section.Groups["body"].Length);
                string stage = Path.Combine(Root, ".config-stage-" + Guid.NewGuid().ToString("N") + ".ini");
                try
                {
                    File.WriteAllText(stage, modified, new UTF8Encoding(false)); Data.ValidateIni(stage);
                    Backup previous = Backups(exe).FirstOrDefault(record => Data.Full(record.TargetDirectory).Equals(target, StringComparison.OrdinalIgnoreCase));
                    string hash = Data.Hash(stage);
                    Backup b = CreateBackup(exe, target, new[] { new PackageFile { Name = "dlssg_sm86.ini", Hash = hash } },
                        previous == null ? "未知（仅配置变更）" : previous.Version, previous == null ? "" : previous.Commit, false);
                    if (IsRunning(exe)) throw new IOException("游戏重新启动，未修改配置。");
                    try
                    {
                        File.Copy(stage, ini, true);
                        if (Data.Hash(ini) != hash) throw new IOException("配置写入校验失败。");
                        b.State = "已修改日志等级"; SaveBackup(b); log("日志等级已设置为 " + level + "；原配置已备份，重启游戏后生效。");
                    }
                    catch { RestoreFiles(b, false); throw; }
                }
                finally { if (File.Exists(stage)) File.Delete(stage); }
            }
        }
        public string CacheStatus()
        {
            Package p = ValidCache();
            return p == null ? "暂无完整包缓存 · 旧版缓存需联网重建" : p.Version + " / " + p.Commit.Substring(0, 12) + " · " + p.Files.Count + " 个文件（完整包已校验）";
        }
        FileStream OperationLock()
        {
            Directory.CreateDirectory(Root);
            try { return new FileStream(Path.Combine(Root, ".operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new IOException("另一个窗口正在安装、更新缓存或恢复，请稍后再试。"); }
        }
        Package ValidCache()
        {
            try
            {
                Package p = Data.Read<Package>(Path.Combine(CacheRoot, "缓存清单.json"));
                if (p == null || p.Schema != 3 || !Regex.IsMatch(p.Commit ?? "", "^[a-fA-F0-9]{40}$") || p.Files == null ||
                    p.Files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.Files.Count ||
                    !Data.Required.Concat(new[] { "README.md" }).All(name => p.Files.Any(file => file.Name == name))) return null;
                foreach (PackageFile file in p.Files)
                {
                    string path = Data.FileAt(CacheRoot, file.Name);
                    if (!Data.IsHash(file.Hash) || new FileInfo(path).Length != file.Size || Data.Hash(path) != file.Hash) return null;
                }
                p.Directory = CacheRoot;
                return p;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
        public async Task<Package> CheckCacheAsync(Action<string> log, CancellationToken token, Action<DownloadProgress> progress = null)
        {
            using (OperationLock()) return await PreparePackage(log, token, progress);
        }
        async Task<Package> PreparePackage(Action<string> log, CancellationToken token, Action<DownloadProgress> progress)
        {
            RecoverCacheSwap(log);
            Package cached = ValidCache();
            string sha;
            try { sha = await upstream.Commit(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                if (cached == null) throw new IOException("无法检查更新，且没有有效缓存。\r\n" + e.Message, e);
                cached.Source = "离线缓存";
                log("更新检查失败，使用已校验离线缓存：" + e.Message);
                return cached;
            }
            if (cached != null && cached.Commit.Equals(sha, StringComparison.OrdinalIgnoreCase))
            {
                cached.Source = "缓存命中"; log("缓存命中，来源提交未变化，无需重复下载 DLL。"); return cached;
            }
            log(cached == null ? "建立官方文件缓存……" : "检测到新提交，正在更新缓存……");
            string stage = Path.Combine(Root, ".cache-stage-" + Guid.NewGuid().ToString("N"));
            string old = Path.Combine(Root, ".cache-rollback");
            Directory.CreateDirectory(stage);
            bool oldMoved = false, newMoved = false;
            try
            {
                List<PackageFile> files = await upstream.Files(sha, token);
                long total = files.Sum(file => file.Size), completed = 0;
                for (int i = 0; i < files.Count; i++)
                {
                    PackageFile file = files[i]; long start = completed; int number = i + 1;
                    string path = Data.FileAt(stage, file.Name);
                    log("下载 " + number + "/" + files.Count + " · " + file.Name);
                    await upstream.Download(sha, file.Name, path, token, received => {
                        if (progress != null) progress(new DownloadProgress { File = file.Name, FileNumber = number, FileCount = files.Count, CompletedBytes = start + received, TotalBytes = total });
                    });
                    if (new FileInfo(path).Length != file.Size) throw new IOException("下载文件大小与提交列表不符：" + file.Name);
                    if (file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) Data.ValidateDll(path);
                    file.Hash = Data.Hash(path); completed += file.Size;
                }
                Data.ValidateIni(Path.Combine(stage, "dlssg_sm86.ini"));
                Package p = new Package { Schema = 3, Commit = sha, Version = Data.Version(File.ReadAllText(Path.Combine(stage, "README.md"), Encoding.UTF8)), Files = files,
                    CachedAt = DateTimeOffset.Now.ToString("o"), Source = "在线更新", Directory = CacheRoot };
                Data.Json(Path.Combine(stage, "缓存清单.json"), p);
                token.ThrowIfCancellationRequested();
                if (Directory.Exists(CacheRoot)) { Directory.Move(CacheRoot, old); oldMoved = true; }
                Directory.Move(stage, CacheRoot); newMoved = true;
                if (oldMoved) { try { Data.DeleteTaskDirectory(old, Root); } catch (IOException) { log("旧缓存稍后清理，不影响本次使用。"); } }
                log("缓存准备完成：上游 " + p.Version + " / " + sha.Substring(0, 12));
                return p;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                if (newMoved && Directory.Exists(CacheRoot)) Data.DeleteTaskDirectory(CacheRoot, Root);
                if (oldMoved && Directory.Exists(old)) Directory.Move(old, CacheRoot);
                if (cached == null) throw;
                cached.Source = "离线缓存"; log("更新未完成，已恢复并使用有效旧缓存：" + e.Message); return cached;
            }
            finally { if (Directory.Exists(stage)) Data.DeleteTaskDirectory(stage, Root); }
        }
        void RecoverCacheSwap(Action<string> log)
        {
            string old = Path.Combine(Root, ".cache-rollback");
            if (!Directory.Exists(old)) return;
            if (!Directory.Exists(CacheRoot) || ValidCache() == null)
            {
                if (Directory.Exists(CacheRoot)) Data.DeleteTaskDirectory(CacheRoot, Root);
                Directory.Move(old, CacheRoot); log("已恢复上次中断的缓存事务。");
            }
            else Data.DeleteTaskDirectory(old, Root);
        }
        static string ValidateTarget(string exe)
        {
            if (String.IsNullOrWhiteSpace(exe) || !File.Exists(exe) || !Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("请先选择真实的游戏渲染 EXE。");
            return Path.GetDirectoryName(Data.Full(exe));
        }
        public async Task<Backup> InstallAsync(string exe, Action<string> log, CancellationToken token, Action<DownloadProgress> progress = null)
        {
            string target = ValidateTarget(exe);
            using (OperationLock())
            {
                if (IsRunning(exe)) throw new IOException("游戏仍在运行，请退出后重新检查。");
                Package package = await PreparePackage(log, token, progress);
                token.ThrowIfCancellationRequested();
                if (IsRunning(exe)) throw new IOException("下载期间游戏重新启动，安装已停止。");
                Backup b = CreateBackup(exe, target, package.Files.Where(file => Data.Managed(file.Name)), package.Version, package.Commit, true);
                log("安装前备份已完成并校验：" + b.Directory);
                bool started = false;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (IsRunning(exe)) throw new IOException("游戏重新启动，未进行文件替换。");
                    started = true;
                    foreach (FileRecord file in b.Files)
                    {
                        string destination = Data.FileAt(target, file.Name);
                        if (file.InstalledExists)
                        {
                            Data.Copy(Data.FileAt(CacheRoot, file.Name), destination, true);
                            if (Data.Hash(destination) != file.InstalledHash) throw new IOException("部署校验失败：" + file.Name);
                        }
                        else if (File.Exists(destination)) File.Delete(destination);
                    }
                    Data.RemoveEmptyPayloadDirectories(target);
                    b.State = "已安装"; SaveBackup(b);
                    log("完整包部署完成：代理、INI 与配套载荷已校验，运行日志已保留。");
                    return b;
                }
                catch
                {
                    if (started)
                    {
                        try { RestoreFiles(b, false); b.State = "安装失败，已回滚"; SaveBackup(b); log("已自动回滚到安装前状态。"); }
                        catch (Exception e) { b.State = "回滚未完成：" + e.Message; SaveBackup(b); log(b.State + "；请使用恢复说明。"); }
                    }
                    throw;
                }
            }
        }
        Backup CreateBackup(string exe, string target, IEnumerable<PackageFile> desired, string version, string commit, bool includePayload)
        {
            var incoming = desired.ToDictionary(file => file.Name, StringComparer.OrdinalIgnoreCase);
            var names = new List<string>(incoming.Keys);
            if (includePayload) names.AddRange(Data.PayloadFiles(target));
            Directory.CreateDirectory(BackupRoot);
            string dir = Path.Combine(BackupRoot, "备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(dir, "原文件备份"));
            Backup b = new Backup { Schema = 2, CreatedAt = DateTimeOffset.Now.ToString("o"), GameExe = Data.Full(exe),
                TargetDirectory = target, Commit = commit, Version = version, State = "已备份", Directory = dir, Files = new List<FileRecord>() };
            foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name == "version.dll" ? 0 : name == "dlssg_sm86.ini" ? 1 : 2).ThenBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                string path = Data.FileAt(target, name); PackageFile next;
                bool installed = incoming.TryGetValue(name, out next);
                var r = new FileRecord { Name = name, Existed = File.Exists(path), BeforeHash = "",
                    InstalledExists = installed, InstalledHash = installed ? next.Hash : null };
                if (r.Existed)
                {
                    r.BeforeHash = Data.Hash(path);
                    string copy = Data.FileAt(Path.Combine(dir, "原文件备份"), name);
                    Data.Copy(path, copy, false);
                    if (Data.Hash(copy) != r.BeforeHash) throw new IOException("原文件备份校验失败：" + name);
                }
                b.Files.Add(r);
            }
            SaveBackup(b);
            return b;
        }
        void SaveBackup(Backup b)
        {
            Data.Json(Path.Combine(b.Directory, "manifest.json"), b);
            string note = "DLSS 帧生成恢复说明\r\n\r\n时间：" + b.CreatedAt + "\r\n游戏：" + b.GameExe +
                "\r\n目标目录：" + b.TargetDirectory + "\r\n来源提交：" + b.Commit + "\r\n上游版本：" + b.Version +
                "\r\n状态：" + b.State + "\r\n备份目录：" + b.Directory +
                "\r\n\r\n在 GUI 的“备份与恢复”中选择此恢复点；恢复前完全退出游戏。\r\n";
            foreach (FileRecord r in b.Files)
                note += "\r\n" + r.Name + "：" + (r.Existed ? "用 原文件备份\\" + r.Name + " 覆盖回目标目录；原哈希 " + r.BeforeHash : "安装前不存在，恢复时移除仍属于本次安装的文件") + "\r\n安装后：" + (r.InstalledExists ? r.InstalledHash : "文件已移除") + "\r\n";
            note += "\r\n本恢复点仅处理上面列出的文件；游戏运行日志不会删除。\r\n";
            File.WriteAllText(Path.Combine(b.Directory, "恢复说明.txt"), note, new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(Root, "最近一次恢复说明.txt"), note, new UTF8Encoding(true));
            try { File.WriteAllText(Path.Combine(b.TargetDirectory, "DLSS帧生成_最近一次恢复说明.txt"), note, new UTF8Encoding(true)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        public List<Backup> Backups(string exe)
        {
            var roots = new List<string> { BackupRoot };
            if (!String.IsNullOrWhiteSpace(exe))
            {
                var directory = new DirectoryInfo(Path.GetDirectoryName(Data.Full(exe)));
                for (int i = 0; i < 6 && directory != null && directory.Parent != null; i++, directory = directory.Parent)
                    roots.Add(Path.Combine(directory.FullName, "DLSS帧生成备份"));
            }
            var result = new List<Backup>();
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root)) continue;
                foreach (string directory in Directory.GetDirectories(root))
                {
                    try
                    {
                        string json = Path.Combine(directory, "manifest.json");
                        Backup b;
                        if (File.Exists(json)) b = ReadBackup(json);
                        else
                        {
                            json = Path.Combine(directory, "恢复清单.json");
                            if (!File.Exists(json)) continue;
                            b = ReadLegacy(json);
                        }
                        b.Directory = directory;
                        ValidateBackup(b);
                        result.Add(b);
                    }
                    catch { /* Invalid manifests are never offered for restore. */ }
                }
            }
            return result.OrderByDescending(x => x.CreatedAt).ToList();
        }
        static Backup ReadLegacy(string path)
        {
            var d = Data.Read<Dictionary<string, object>>(path);
            var b = new Backup { Schema = 1, Legacy = true, State = "旧版备份", Files = new List<FileRecord>(),
                CreatedAt = Convert.ToString(d["部署时间"]), GameExe = Convert.ToString(d["游戏主程序"]),
                TargetDirectory = Convert.ToString(d["目标目录"]), Version = Convert.ToString(d["项目版本"]), Commit = Convert.ToString(d["来源提交"]) };
            foreach (var r in (System.Collections.IEnumerable)d["文件"])
            {
                var f = (Dictionary<string, object>)r;
                string name = Convert.ToString(f["名称"]);
                if (!Data.Names.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                    !Data.Full(Convert.ToString(f["原路径"])).Equals(Data.Full(Path.Combine(b.TargetDirectory, name)), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("旧清单包含越界路径。");
                bool existed = Convert.ToBoolean(f["安装前存在"]);
                if (existed && !Convert.ToString(f["备份相对路径"]).Equals(Path.Combine("原文件备份", name), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("旧清单包含无效备份路径。");
                b.Files.Add(new FileRecord { Name = name, Existed = existed, InstalledExists = true, BeforeHash = Convert.ToString(f["安装前SHA256"]), InstalledHash = Convert.ToString(f["安装后SHA256"]) });
            }
            return b;
        }
        static Backup ReadBackup(string path)
        {
            Backup backup = Data.Read<Backup>(path);
            if (backup != null && backup.Schema == 1 && backup.Files != null) foreach (FileRecord file in backup.Files) file.InstalledExists = true;
            return backup;
        }
        static void ValidateBackup(Backup b)
        {
            if (b == null || (b.Schema != 1 && b.Schema != 2) || String.IsNullOrWhiteSpace(b.TargetDirectory) ||
                !Path.IsPathRooted(b.TargetDirectory) || b.Files == null || b.Files.Count == 0 || (b.Schema == 1 && b.Files.Count != 2) ||
                b.Files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != b.Files.Count)
                throw new IOException("备份清单格式无效。");
            foreach (FileRecord r in b.Files)
            {
                if (!(b.Schema == 1 ? Data.Names.Contains(r.Name, StringComparer.OrdinalIgnoreCase) : Data.Managed(r.Name)) ||
                    (r.Existed && !Data.IsHash(r.BeforeHash)) || (b.Schema == 2 && r.InstalledExists && !Data.IsHash(r.InstalledHash)))
                    throw new IOException("备份清单包含不受支持的文件或无效哈希。");
            }
        }
        public void Restore(Backup selected, Action<string> log)
        {
            using (OperationLock())
            {
                // Re-read to avoid using stale or modified manifest contents.
                string directory = Data.Full(selected.Directory);
                string modern = Path.Combine(directory, "manifest.json");
                Backup b = selected.Legacy ? ReadLegacy(Path.Combine(directory, "恢复清单.json")) : ReadBackup(modern);
                b.Directory = directory; ValidateBackup(b);
                if (!Data.Full(b.TargetDirectory).Equals(Data.Full(selected.TargetDirectory), StringComparison.OrdinalIgnoreCase) ||
                    !Data.Full(b.GameExe).Equals(Data.Full(selected.GameExe), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("备份清单在确认后发生变化，请重新刷新并选择。");
                if (!Directory.Exists(b.TargetDirectory)) throw new IOException("游戏目录已不存在。");
                if (IsRunning(b.GameExe)) throw new IOException("游戏仍在运行，无法恢复。");
                CheckRestore(b, true);
                string before = Path.Combine(directory, "恢复前现状_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(before);
                foreach (FileRecord r in b.Files)
                {
                    string current = Data.FileAt(b.TargetDirectory, r.Name);
                    if (File.Exists(current)) Data.Copy(current, Data.FileAt(before, r.Name), false);
                }
                if (IsRunning(b.GameExe)) throw new IOException("游戏重新启动，未执行恢复。");
                try { ApplyRestore(b); log("恢复完成，游戏日志已保留。恢复前现状保存在：" + before); }
                catch
                {
                    foreach (FileRecord r in b.Files)
                    {
                        string snapshot = Data.FileAt(before, r.Name), current = Data.FileAt(b.TargetDirectory, r.Name);
                        if (File.Exists(snapshot)) Data.Copy(snapshot, current, true);
                        else if (File.Exists(current)) File.Delete(current);
                    }
                    throw;
                }
            }
        }
        static void RestoreFiles(Backup b, bool checkInstalled)
        {
            CheckRestore(b, checkInstalled); ApplyRestore(b);
        }
        static void CheckRestore(Backup b, bool checkInstalled)
        {
            ValidateBackup(b);
            foreach (FileRecord r in b.Files)
            {
                string current = Data.FileAt(b.TargetDirectory, r.Name);
                string original = Data.FileAt(Path.Combine(b.Directory, "原文件备份"), r.Name);
                if (r.Existed && (!File.Exists(original) || Data.Hash(original) != r.BeforeHash))
                    throw new IOException("原备份损坏，未执行恢复：" + r.Name);
                if (checkInstalled && !r.Existed && File.Exists(current) &&
                    (!r.InstalledExists || !Data.IsHash(r.InstalledHash) || Data.Hash(current) != r.InstalledHash))
                    throw new IOException("安装后文件已被其他工具修改，请先按恢复说明手工处理：" + r.Name);
            }
        }
        static void ApplyRestore(Backup b)
        {
            foreach (FileRecord r in b.Files)
            {
                string current = Data.FileAt(b.TargetDirectory, r.Name);
                if (r.Existed)
                {
                    Data.Copy(Data.FileAt(Path.Combine(b.Directory, "原文件备份"), r.Name), current, true);
                    if (Data.Hash(current) != r.BeforeHash) throw new IOException("恢复后校验失败：" + r.Name);
                }
                else if (File.Exists(current)) File.Delete(current);
            }
            Data.RemoveEmptyPayloadDirectories(b.TargetDirectory);
        }
    }
}
