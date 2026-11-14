using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DlssgManager;

sealed class ReleaseResponse : HttpMessageHandler
{
    readonly string manifest, binary;
    public ReleaseResponse(string manifest, string binary) { this.manifest = manifest; this.binary = binary; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string url = request.RequestUri.AbsoluteUri;
        if (url.EndsWith("releases/latest")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
            "{\"tag_name\":\"v2.1.0\",\"body\":\"GUI update\",\"assets\":[{\"name\":\"DLSSG-Install-Manager.exe\",\"browser_download_url\":\"https://github.com/BerryFuwawa/dlssg-install-manager/releases/download/v2.1.0/DLSSG-Install-Manager.exe\"},{\"name\":\"update-manifest.json\",\"browser_download_url\":\"https://github.com/BerryFuwawa/dlssg-install-manager/releases/download/v2.1.0/update-manifest.json\"}]}" ) });
        if (url.EndsWith("update-manifest.json")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(manifest)) });
        if (url.EndsWith("DLSSG-Install-Manager.exe")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(binary)) });
        throw new IOException("Unexpected request: " + url);
    }
}
static class LegacyClientCheck
{
    static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "dlssg-legacy-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (typeof(GuiUpdater).Assembly.GetName().Version != new Version(2, 0, 0, 0)) throw new Exception("Expected the original published v2.0.0 client assembly.");
            using (var updater = new GuiUpdater(new ReleaseResponse(args[0], args[1])))
            {
                var release = updater.Check(CancellationToken.None).GetAwaiter().GetResult();
                if (release == null || release.Version != "2.1.0") throw new Exception("v2.0.0 did not detect v2.1.0.");
                string downloaded = updater.Download(release, root, Console.WriteLine, CancellationToken.None).GetAwaiter().GetResult();
                if (!File.Exists(downloaded)) throw new Exception("v2.0.0 could not verify the new package.");
                Console.WriteLine("PASS published v2.0.0 client detects and verifies the actual v2.1.0 GUI binary.");
            }
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
