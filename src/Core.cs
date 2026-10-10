using System;
using System.Collections.Generic;
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
        public string DllHash { get; set; }
        public string IniHash { get; set; }
        public long DllSize { get; set; }
        public string CachedAt { get; set; }
        public string Source { get; set; }
        public string Directory { get; set; }
    }
    public sealed class FileRecord
    {
        public string Name { get; set; }
        public bool Existed { get; set; }
        public string BeforeHash { get; set; }
        public string InstalledHash { get; set; }
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
            if (new FileInfo(path).Length < 1024 * 1024) throw new IOException("DLL 大小异常。");
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
            if (!keys.TryGetValue("FrameGeneration.MaxGeneratedFrames", out n) || !Regex.IsMatch(n, "^[0-5]$"))
                throw new IOException("官方 INI 缺少有效的帧生成配置。");
            if (keys.TryGetValue("FrameGeneration.Optimized", out n) && !Regex.IsMatch(n, "^[0-3]$"))
                throw new IOException("官方 INI 的 Optimized 值无效。");
            if (keys.TryGetValue("Runtime.Mode", out n) && !n.Equals("Bundled", StringComparison.OrdinalIgnoreCase))
                throw new IOException("当前工具只自动安装官方内嵌运行库配置。");
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
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DlssgInstallManager/2.0.0");
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
        public async Task Download(string sha, string file, string target, CancellationToken token)
        {
            string url = "https://raw.githubusercontent.com/sdli1995/dlssg_for_sm86/" + sha + "/" + file;
            using (var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                if (!r.IsSuccessStatusCode) throw new IOException("HTTP " + (int)r.StatusCode + "：" + url);
                using (var input = await r.Content.ReadAsStreamAsync())
                using (var output = File.Create(target))
                    await input.CopyToAsync(output, 81920, token);
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
            foreach (string name in Data.Names)
            {
                string path = Path.Combine(target, name);
                text.Append(name + (File.Exists(path) ? "\r\nSHA256：" + Data.Hash(path) : "：未安装") + "\r\n\r\n");
            }
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
                    var package = new Package { DllHash = Data.Hash(dll), IniHash = Data.Hash(stage), Version = previous == null ? "未知（仅配置变更）" : previous.Version, Commit = previous == null ? "" : previous.Commit };
                    Backup b = CreateBackup(exe, target, package);
                    if (IsRunning(exe)) throw new IOException("游戏重新启动，未修改配置。");
                    try
                    {
                        File.Copy(stage, ini, true);
                        if (Data.Hash(ini) != package.IniHash) throw new IOException("配置写入校验失败。");
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
            return p == null ? "暂无有效缓存" : p.Version + " / " + p.Commit.Substring(0, 12) + "（SHA256 已校验）";
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
                string dll = Path.Combine(CacheRoot, "version.dll"), ini = Path.Combine(CacheRoot, "dlssg_sm86.ini");
                if (p.Schema != 2 || !Regex.IsMatch(p.Commit ?? "", "^[a-fA-F0-9]{40}$") ||
                    !Data.IsHash(p.DllHash) || !Data.IsHash(p.IniHash) ||
                    !File.Exists(Path.Combine(CacheRoot, "README.md")) ||
                    new FileInfo(dll).Length != p.DllSize || Data.Hash(dll) != p.DllHash || Data.Hash(ini) != p.IniHash)
                    return null;
                Data.ValidateDll(dll); Data.ValidateIni(ini);
                if (Data.Version(File.ReadAllText(Path.Combine(CacheRoot, "README.md"), Encoding.UTF8)) != p.Version) return null;
                p.Directory = CacheRoot;
                return p;
            }
            catch { return null; }
        }
        public async Task<Package> CheckCacheAsync(Action<string> log, CancellationToken token)
        {
            using (OperationLock()) return await PreparePackage(log, token);
        }
        async Task<Package> PreparePackage(Action<string> log, CancellationToken token)
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
                foreach (string file in new[] { "README.md", "dlssg_sm86.ini", "version.dll" })
                {
                    log("下载 " + file);
                    await upstream.Download(sha, file, Path.Combine(stage, file), token);
                }
                string dll = Path.Combine(stage, "version.dll"), ini = Path.Combine(stage, "dlssg_sm86.ini");
                Data.ValidateDll(dll); Data.ValidateIni(ini);
                Package p = new Package { Schema = 2, Commit = sha, Version = Data.Version(File.ReadAllText(Path.Combine(stage, "README.md"), Encoding.UTF8)),
                    DllHash = Data.Hash(dll), IniHash = Data.Hash(ini), DllSize = new FileInfo(dll).Length,
                    CachedAt = DateTimeOffset.Now.ToString("o"), Source = "在线更新", Directory = CacheRoot };
                Data.Json(Path.Combine(stage, "缓存清单.json"), p);
                token.ThrowIfCancellationRequested();
                if (Directory.Exists(CacheRoot)) { Directory.Move(CacheRoot, old); oldMoved = true; }
                Directory.Move(stage, CacheRoot); newMoved = true;
                if (ValidCache() == null) throw new IOException("缓存提交后校验失败。");
                if (oldMoved) { try { Data.DeleteTaskDirectory(old, Root); } catch (IOException) { log("旧缓存稍后清理，不影响本次使用。"); } }
                log("缓存准备完成：上游 " + p.Version + " / " + sha.Substring(0, 12));
                return p;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                if (newMoved && Directory.Exists(CacheRoot)) Data.DeleteTaskDirectory(CacheRoot, Root);
                if (oldMoved && Directory.Exists(old)) Directory.Move(old, CacheRoot);
                cached = ValidCache();
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
        public async Task<Backup> InstallAsync(string exe, Action<string> log, CancellationToken token)
        {
            string target = ValidateTarget(exe);
            using (OperationLock())
            {
                if (IsRunning(exe)) throw new IOException("游戏仍在运行，请退出后重新检查。");
                Package package = await PreparePackage(log, token);
                token.ThrowIfCancellationRequested();
                if (IsRunning(exe)) throw new IOException("下载期间游戏重新启动，安装已停止。");
                Backup b = CreateBackup(exe, target, package);
                log("安装前备份已完成并校验：" + b.Directory);
                bool started = false;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (IsRunning(exe)) throw new IOException("游戏重新启动，未进行文件替换。");
                    started = true;
                    foreach (FileRecord file in b.Files)
                    {
                        string destination = Path.Combine(target, file.Name);
                        File.Copy(Path.Combine(CacheRoot, file.Name), destination, true);
                        if (Data.Hash(destination) != file.InstalledHash) throw new IOException("部署校验失败：" + file.Name);
                    }
                    b.State = "已安装"; SaveBackup(b);
                    log("安装完成：DLL 与官方配套 INI 均通过 SHA256 校验。");
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
        Backup CreateBackup(string exe, string target, Package p)
        {
            Directory.CreateDirectory(BackupRoot);
            string dir = Path.Combine(BackupRoot, "备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(dir, "原文件备份"));
            Backup b = new Backup { Schema = 1, CreatedAt = DateTimeOffset.Now.ToString("o"), GameExe = Data.Full(exe),
                TargetDirectory = target, Commit = p.Commit, Version = p.Version, State = "已备份", Directory = dir, Files = new List<FileRecord>() };
            foreach (string name in Data.Names)
            {
                string path = Path.Combine(target, name);
                var r = new FileRecord { Name = name, Existed = File.Exists(path), BeforeHash = "",
                    InstalledHash = name == "version.dll" ? p.DllHash : p.IniHash };
                if (r.Existed)
                {
                    r.BeforeHash = Data.Hash(path);
                    string copy = Path.Combine(dir, "原文件备份", name);
                    File.Copy(path, copy, false);
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
                note += "\r\n" + r.Name + "：" + (r.Existed ? "用 原文件备份\\" + r.Name + " 覆盖回目标目录；原哈希 " + r.BeforeHash : "安装前不存在，恢复时移除本工具安装的同名文件") + "\r\n安装哈希：" + r.InstalledHash + "\r\n";
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
                        if (File.Exists(json)) b = Data.Read<Backup>(json);
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
                b.Files.Add(new FileRecord { Name = name, Existed = existed, BeforeHash = Convert.ToString(f["安装前SHA256"]), InstalledHash = Convert.ToString(f["安装后SHA256"]) });
            }
            return b;
        }
        static void ValidateBackup(Backup b)
        {
            if (b == null || b.Schema != 1 || String.IsNullOrWhiteSpace(b.TargetDirectory) ||
                !Path.IsPathRooted(b.TargetDirectory) || b.Files == null || b.Files.Count != 2 ||
                b.Files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2)
                throw new IOException("备份清单格式无效。");
            foreach (FileRecord r in b.Files)
            {
                if (!Data.Names.Contains(r.Name, StringComparer.OrdinalIgnoreCase) || (r.Existed && !Data.IsHash(r.BeforeHash)))
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
                Backup b = selected.Legacy ? ReadLegacy(Path.Combine(directory, "恢复清单.json")) : Data.Read<Backup>(modern);
                b.Directory = directory; ValidateBackup(b);
                if (!Data.Full(b.TargetDirectory).Equals(Data.Full(selected.TargetDirectory), StringComparison.OrdinalIgnoreCase) ||
                    !Data.Full(b.GameExe).Equals(Data.Full(selected.GameExe), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("备份清单在确认后发生变化，请重新刷新并选择。");
                if (!Directory.Exists(b.TargetDirectory)) throw new IOException("游戏目录已不存在。");
                if (IsRunning(b.GameExe)) throw new IOException("游戏仍在运行，无法恢复。");
                string before = Path.Combine(directory, "恢复前现状_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(before);
                foreach (FileRecord r in b.Files)
                {
                    string current = Path.Combine(b.TargetDirectory, r.Name);
                    if (File.Exists(current)) File.Copy(current, Path.Combine(before, r.Name), false);
                }
                try { RestoreFiles(b, true); log("恢复完成。恢复前现状保存在：" + before); }
                catch
                {
                    foreach (FileRecord r in b.Files)
                    {
                        string snapshot = Path.Combine(before, r.Name), current = Path.Combine(b.TargetDirectory, r.Name);
                        if (File.Exists(snapshot)) File.Copy(snapshot, current, true);
                        else if (File.Exists(current)) File.Delete(current);
                    }
                    throw;
                }
            }
        }
        static void RestoreFiles(Backup b, bool checkInstalled)
        {
            ValidateBackup(b);
            foreach (FileRecord r in b.Files)
            {
                string current = Path.Combine(b.TargetDirectory, r.Name);
                string original = Path.Combine(b.Directory, "原文件备份", r.Name);
                if (r.Existed && (!File.Exists(original) || Data.Hash(original) != r.BeforeHash))
                    throw new IOException("原备份损坏，未执行恢复：" + r.Name);
                if (checkInstalled && !r.Existed && File.Exists(current) &&
                    (!Data.IsHash(r.InstalledHash) || Data.Hash(current) != r.InstalledHash))
                    throw new IOException("安装后文件已被其他工具修改，请先按恢复说明手工处理：" + r.Name);
            }
            foreach (FileRecord r in b.Files)
            {
                string current = Path.Combine(b.TargetDirectory, r.Name);
                if (r.Existed)
                {
                    File.Copy(Path.Combine(b.Directory, "原文件备份", r.Name), current, true);
                    if (Data.Hash(current) != r.BeforeHash) throw new IOException("恢复后校验失败：" + r.Name);
                }
                else if (File.Exists(current)) File.Delete(current);
            }
        }
    }
}
