using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DlssgManager
{
    public sealed class Settings
    {
        public bool CheckGuiUpdates { get; set; }
        public Settings() { CheckGuiUpdates = true; }
    }
    public sealed class UpdateManifest
    {
        public int Schema { get; set; }
        public string Version { get; set; }
        public string File { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
    }
    public sealed class GuiUpdate
    {
        public string Version;
        public string Page;
        public string Notes;
        public string ManifestUrl;
        public string ExeUrl;
    }
    public sealed class GuiUpdater : IDisposable
    {
        public const string Repository = "https://github.com/BerryFuwawa/dlssg-install-manager";
        public const string Current = "2.0.0";
        readonly HttpClient http;
        public GuiUpdater() : this(new HttpClientHandler()) { }
        public GuiUpdater(HttpMessageHandler handler)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DlssgInstallManager/" + Current);
        }
        public static bool IsNewer(string remote)
        {
            Version v;
            return System.Version.TryParse((remote ?? "").TrimStart('v'), out v) && v > new Version(Current);
        }
        public async Task<GuiUpdate> Check(CancellationToken token)
        {
            using (var r = await http.GetAsync("https://api.github.com/repos/BerryFuwawa/dlssg-install-manager/releases/latest", token))
            {
                if (r.StatusCode == HttpStatusCode.NotFound) return null;
                r.EnsureSuccessStatusCode();
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(await r.Content.ReadAsStringAsync());
                string tag = Convert.ToString(d["tag_name"]);
                if (!IsNewer(tag)) return null;
                string exe = null, manifest = null;
                foreach (var item in (System.Collections.IEnumerable)d["assets"])
                {
                    var a = (Dictionary<string, object>)item;
                    string name = Convert.ToString(a["name"]), url = Convert.ToString(a["browser_download_url"]);
                    ValidateAsset(url, tag);
                    if (name == "DLSSG-Install-Manager.exe") exe = url;
                    if (name == "update-manifest.json") manifest = url;
                }
                if (exe == null || manifest == null) throw new IOException("新版本尚未发布完整更新文件，请稍后重试。");
                return new GuiUpdate { Version = tag.TrimStart('v'), Page = Repository + "/releases/tag/" + Uri.EscapeDataString(tag),
                    Notes = Convert.ToString(d["body"]), ExeUrl = exe, ManifestUrl = manifest };
            }
        }
        static void ValidateAsset(string url, string tag)
        {
            string prefix = Repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/";
            if (url == null || !url.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("更新地址不属于官方 GUI 仓库。");
        }
        public async Task<string> Download(GuiUpdate release, string root, Action<string> log, CancellationToken token)
        {
            ValidateAsset(release.ManifestUrl, "v" + release.Version);
            ValidateAsset(release.ExeUrl, "v" + release.Version);
            UpdateManifest manifest;
            using (var response = await http.GetAsync(release.ManifestUrl, token))
            {
                response.EnsureSuccessStatusCode();
                manifest = new JavaScriptSerializer().Deserialize<UpdateManifest>(await response.Content.ReadAsStringAsync());
            }
            if (manifest.Schema != 1 || manifest.Version != release.Version || manifest.File != "DLSSG-Install-Manager.exe" ||
                !Data.IsHash(manifest.Sha256) || manifest.Size < 16384 || manifest.Size > 50 * 1024 * 1024)
                throw new IOException("GUI 更新清单无效，未替换当前版本。");
            string directory = Path.Combine(root, "程序更新", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, manifest.File);
            try
            {
                log("正在下载 GUI v" + release.Version + "，当前程序不会被提前覆盖……");
                using (var r = await http.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    r.EnsureSuccessStatusCode();
                    using (var input = await r.Content.ReadAsStreamAsync())
                    using (var output = File.Create(target))
                    {
                        byte[] buffer = new byte[81920]; int count; long total = 0;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                        {
                            total += count;
                            if (total > manifest.Size) throw new IOException("GUI 更新文件大小超出清单。");
                            await output.WriteAsync(buffer, 0, count, token);
                        }
                    }
                }
                if (new FileInfo(target).Length != manifest.Size || !Data.Hash(target).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("GUI 更新 SHA256 或大小校验失败。");
                AssemblyName name = AssemblyName.GetAssemblyName(target);
                if (name.Name != "DLSSG-Install-Manager" || name.Version != new Version(release.Version + ".0"))
                    throw new IOException("GUI 更新程序标识或版本不匹配。");
                token.ThrowIfCancellationRequested();
                log("GUI 更新已下载并校验完成。");
                return target;
            }
            catch { Data.DeleteTaskDirectory(directory, root); throw; }
        }
        public static async Task StartReplacement(string stage)
        {
            string helper = Path.Combine(Path.GetDirectoryName(stage), "DLSSG-UpdateHelper.exe");
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("UpdateHelper.exe"))
            using (var output = File.Create(helper))
            {
                if (resource == null) throw new IOException("内置更新助手缺失，未关闭当前程序。");
                resource.CopyTo(output);
            }
            string current = Process.GetCurrentProcess().MainModule.FileName;
            string args = Process.GetCurrentProcess().Id + " \"" + current + "\" \"" + stage + "\" " + Data.Hash(stage);
            var start = new ProcessStartInfo(helper, args) { UseShellExecute = false, CreateNoWindow = true };
            using (var process = Process.Start(start))
            {
                for (int i = 0; i < 100; i++)
                {
                    if (System.IO.File.Exists(stage + ".ready")) return;
                    if (process.HasExited) throw new IOException("更新助手未就绪，当前程序继续保留。");
                    await Task.Delay(50);
                }
                throw new IOException("更新助手启动超时，当前程序继续保留。");
            }
        }
        public void Dispose() { http.Dispose(); }
    }
}
