#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Dalamud;

public class DalamudUpdater
{
    private readonly DirectoryInfo   addonDirectory;
    private readonly DirectoryInfo   assetDirectory;
    private readonly DirectoryInfo   configDirectory;
    private readonly IUniqueIdCache? cache;

    private readonly TimeSpan defaultTimeout = TimeSpan.FromMinutes(1);

    private bool forceProxy;

    public DownloadState State     { get; private set; } = DownloadState.Unknown;
    public bool          IsStaging { get; private set; } = false;

    public Exception? EnsurementException { get; private set; }

    private FileInfo runnerInternal;

    public FileInfo Runner
    {
        get => this.RunnerOverride ?? this.runnerInternal;
        private set => this.runnerInternal = value;
    }

    public DirectoryInfo Runtime { get; }

    public FileInfo? RunnerOverride { get; set; }

    public DirectoryInfo AssetDirectory { get; private set; }

    public IDalamudLoadingOverlay? Overlay { get; set; }

    public string? RolloutBucket { get; }

    public enum DownloadState
    {
        Unknown,
        Done,
        NoIntegrity // fail with error message
    }

    public DalamudUpdater(
        DirectoryInfo addonDirectory, DirectoryInfo runtimeDirectory, DirectoryInfo assetDirectory, DirectoryInfo configDirectory, IUniqueIdCache? cache, string? dalamudRolloutBucket)
    {
        this.addonDirectory  = addonDirectory;
        this.Runtime         = runtimeDirectory;
        this.assetDirectory  = assetDirectory;
        this.configDirectory = configDirectory;
        this.cache           = cache;

        this.RolloutBucket = dalamudRolloutBucket;

        if (this.RolloutBucket == null)
        {
            var rng = new Random();
            this.RolloutBucket = rng.Next(0, 9) >= 7 ? "Canary" : "Control";
        }
    }

    public void SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep progress)
    {
        this.Overlay!.SetStep(progress);
    }

    public void ShowOverlay()
    {
        this.Overlay!.SetVisible();
    }

    public void CloseOverlay()
    {
        this.Overlay!.SetInvisible();
    }

    private void ReportOverlayProgress(long? size, long downloaded, double? progress)
    {
        this.Overlay!.ReportProgress(size, downloaded, progress);
    }

    public void Run(bool overrideForceProxy = false)
    {
        Log.Information("[DUPDATE] 启动中... (是否强制使用代理: {ForceProxy})", overrideForceProxy);
        this.State = DownloadState.Unknown;

        this.forceProxy = overrideForceProxy;

        Task.Run(async () =>
        {
            const int MAX_TRIES = 10;

            var isUpdated = false;

            for (var tries = 0; tries < MAX_TRIES; tries++)
            {
                try
                {
                    await this.UpdateDalamud().ConfigureAwait(true);
                    isUpdated = true;
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[DUPDATE] 更新失败, 重试 {TryCnt}/{MaxTries}...", tries, MAX_TRIES);
                    this.EnsurementException = ex;
                    this.forceProxy          = false;
                }
            }

            this.State = isUpdated ? DownloadState.Done : DownloadState.NoIntegrity;
        });
    }

    private async Task<DalamudVersionInfo?> GetDalamudVersionInfo()
    {
        using var client = new HttpClient();
        client.Timeout = this.defaultTimeout;

        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
        client.DefaultRequestHeaders.Add("User-Agent", PlatformHelpers.GetVersion());

        var versionInfoJsonRelease = 
            await client.GetStringAsync($"{DalamudLauncher.REMOTE_BASE}release&bucket={this.RolloutBucket}").ConfigureAwait(false);

        var versionInfoRelease = JsonConvert.DeserializeObject<DalamudVersionInfo>(versionInfoJsonRelease);

        return versionInfoRelease;
    }

    private async Task UpdateDalamud()
    {
        var settings = DalamudSettings.GetSettings(this.configDirectory);

        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

        // 还是从 ottercorp 那里获取版本信息用于运行时下载
        var versionInfoRelease = await this.GetDalamudVersionInfo().ConfigureAwait(false);
        var versionInfoJson    = JsonConvert.SerializeObject(versionInfoRelease);
        var onlineHash         = await this.GetDalamudHashAsync();

        var addonPath          = new DirectoryInfo(Path.Combine(this.addonDirectory.FullName, "Hooks"));
        var currentVersionPath = new DirectoryInfo(Path.Combine(addonPath.FullName,           versionInfoRelease.AssemblyVersion));

        var runtimePaths = new DirectoryInfo[]
        {
            new(Path.Combine(this.Runtime.FullName, "host",   "fxr",                          versionInfoRelease.RuntimeVersion)),
            new(Path.Combine(this.Runtime.FullName, "shared", "Microsoft.NETCore.App",        versionInfoRelease.RuntimeVersion)),
            new(Path.Combine(this.Runtime.FullName, "shared", "Microsoft.WindowsDesktop.App", versionInfoRelease.RuntimeVersion))
        };

        // 处理 Dalamud
        if (!currentVersionPath.Exists || !CheckDalamudIntegrity(currentVersionPath, onlineHash))
        {
            Log.Information("[DUPDATE] 未找到有效版本，开始重新下载");
            this.SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Dalamud);

            try
            {
                // 下载 Dalamud
                await this.DownloadDalamud(currentVersionPath).ConfigureAwait(true);
                CleanUpOld(addonPath, versionInfoRelease.AssemblyVersion);

                this.cache?.Reset();
            }
            catch (Exception ex)
            {
                throw new DalamudIntegrityException("下载Dalamud失败", ex);
            }
        }

        // 处理 Runtime
        if (versionInfoRelease.RuntimeRequired || settings.DoDalamudRuntime)
        {
            Log.Information("[DUPDATE] 正在准备 .NET 运行时 {0}", versionInfoRelease.RuntimeVersion);

            var versionFile  = new FileInfo(Path.Combine(this.Runtime.FullName, "version"));
            var localVersion = this.GetLocalRuntimeVersion(versionFile);

            var runtimeNeedsUpdate = localVersion != versionInfoRelease.RuntimeVersion;

            if (!this.Runtime.Exists)
                Directory.CreateDirectory(this.Runtime.FullName);

            var isRuntimeIntegrity = false;

            if (!runtimeNeedsUpdate)
            {
                try { isRuntimeIntegrity = await this.CheckRuntimeHashes(this.Runtime, localVersion).ConfigureAwait(false); }
                catch (Exception ex) { Log.Error(ex, "[DUPDATE] 运行时完整性检查失败"); }
            }

            if (runtimePaths.Any(p => !p.Exists) || runtimeNeedsUpdate || !isRuntimeIntegrity)
            {
                Log.Information("[DUPDATE] 运行时缺失/过期/不完整：本地 {LocalVer} - 远程 {RemoteVer}",
                                localVersion, versionInfoRelease.RuntimeVersion);
                this.SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Runtime);

                try
                {
                    Log.Verbose("[DUPDATE] 开始下载运行时...");
                    await this.DownloadRuntime(this.Runtime, versionInfoRelease.RuntimeVersion).ConfigureAwait(false);
                    File.WriteAllText(versionFile.FullName, versionInfoRelease.RuntimeVersion);
                }
                catch (Exception ex)
                {
                    throw new DalamudIntegrityException("无法确保运行时完整性", ex);
                }
            }
        }

        // 处理 Asset
        Log.Verbose("[DUPDATE] 正在验证资源文件...");
        var assetVer = 0;

        try
        {
            this.SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Assets);
            this.ReportOverlayProgress(null, 0, null);
            var assetResult = await AssetManager.EnsureAssets(this, this.assetDirectory).ConfigureAwait(true);
            this.AssetDirectory = assetResult.AssetDir;
            assetVer            = assetResult.Version;
        }
        catch (Exception ex)
        {
            throw new DalamudIntegrityException("资源文件验证失败", ex);
        }

        if (!CheckDalamudIntegrity(currentVersionPath, onlineHash)) 
            throw new DalamudIntegrityException("完整性验证最终失败");

        WriteVersionJson(currentVersionPath, versionInfoJson);

        Log.Information("[DUPDATE] 已为游戏版本 {GameVersion} 准备好Dalamud {DalamudVersion}（运行时 {RuntimeVersion}，资源 {AssetVersion}）",
                        versionInfoRelease.SupportedGameVer, versionInfoRelease.AssemblyVersion, versionInfoRelease.RuntimeVersion, assetVer);

        this.Runner = new FileInfo(Path.Combine(currentVersionPath.FullName, "Dalamud.Injector.exe"));
        this.SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Starting);
        this.ReportOverlayProgress(null, 0, null);
    }

    #region Dalamud

    public static bool CheckDalamudIntegrity(DirectoryInfo addonPath, string onlineHash)
    {
        var files = addonPath.GetFiles();

        try
        {
            if (!CanRead(files.First(x => x.Name == "Dalamud.Injector.exe")) || 
                !CanRead(files.First(x => x.Name == "Dalamud.dll"))          || 
                !CanRead(files.First(x => x.Name == "ImGuiScene.dll")))
            {
                Log.Error("[DUPDATE] 无法打开核心文件");
                return false;
            }

            var hashesPath = Path.Combine(addonPath.FullName, "hashes.json");
            if (!File.Exists(hashesPath))
            {
                Log.Error("[DUPDATE] 无 hashes.json");
                return false;
            }

            if (!string.IsNullOrEmpty(onlineHash))
            {
                var hashHash = ComputeFileHash(hashesPath);

                if (onlineHash != hashHash)
                {
                    Log.Error($"[UPDATE] hashes.json 哈希比对不一致, 本地: {hashHash}, 远程: {onlineHash}");
                    return false;
                }
            }

            return CheckIntegrity(addonPath, File.ReadAllText(hashesPath));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 无 Dalamud 完整性");
            return false;
        }
    }

    public async Task<string> GetDalamudHashAsync()
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MyApp/1.0)");

        try
        {
            var response = await httpClient.GetAsync("https://api.github.com/repos/AtmoOmen/Dalamud/releases/latest");

            response.EnsureSuccessStatusCode();

            var       json    = await response.Content.ReadAsStringAsync();
            using var jsonDoc = JsonDocument.Parse(json);
            var       assets  = jsonDoc.RootElement.GetProperty("assets");

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() == "hashes.json")
                {
                    var downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    if (string.IsNullOrWhiteSpace(downloadUrl)) continue;
                    
                    var downloadPath = PlatformHelpers.GetTempFileName();
                    await DownloadFile($"https://gh.atmoomen.top/{downloadUrl}", downloadPath, TimeSpan.FromSeconds(30));
                    var hash = ComputeFileHash(downloadPath);
                    File.Delete(downloadPath);
                    return hash;
                }
            }

            throw new Exception("hashes.json not found in release assets");
        }
        catch (HttpRequestException e) { throw new Exception("Error accessing GitHub API: " + e.Message); }
    }
    
    private async Task DownloadDalamud(DirectoryInfo addonPath)
    {
        const string REPO_API                 = "https://api.github.com/repos/AtmoOmen/Dalamud/releases/latest";
        const string USER_AGENT               = "Mozilla/5.0 (compatible; MyApp/1.0)";

        if (addonPath.Exists) addonPath.Delete(true);
        addonPath.Create();

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(USER_AGENT);

        try
        {
            var response = await httpClient.GetAsync(REPO_API);
            response.EnsureSuccessStatusCode();

            var       json    = await response.Content.ReadAsStringAsync();
            using var jsonDoc = JsonDocument.Parse(json);
            var       assets  = jsonDoc.RootElement.GetProperty("assets");

            var downloadPath = PlatformHelpers.GetTempFileName();
            
            foreach (var asset in assets.EnumerateArray())
            {
                var fileName    = asset.GetProperty("name").GetString()!;
                var downloadUrl = asset.GetProperty("browser_download_url").GetString()!;

                if (fileName != "latest.7z") continue;
                
                await this.DownloadFile($"https://gh.atmoomen.top/{downloadUrl}", downloadPath, this.defaultTimeout).ConfigureAwait(false);
                PlatformHelpers.Un7za(downloadPath, addonPath.FullName);
                File.Delete(downloadPath);
                break;
            }
            
            try
            {
                var devPath = new DirectoryInfo(Path.Combine(addonPath.FullName, "..", "dev"));
                PlatformHelpers.DeleteAndRecreateDirectory(devPath);
                PlatformHelpers.CopyFilesRecursively(addonPath, devPath);
            }
            catch (Exception ex) { Log.Error(ex, "[DUPDATE] 复制到 dev 目录失败"); }
        }
        catch (HttpRequestException e)
        {
            Log.Error(e, "[DUPDATE] GitHub API 请求失败");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 下载过程中发生错误");
            throw;
        }
    }

    #endregion

    #region Runtime

    private string GetLocalRuntimeVersion(FileInfo versionFile)
    {
        // This is the version we first shipped. We didn't write out a version file, so we can't check it.
        var localVersion = "5.0.6";

        try
        {
            if (versionFile.Exists)
                localVersion = File.ReadAllText(versionFile.FullName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] Could not read local runtime version.");
        }

        return localVersion;
    }
    
    private async Task<bool> CheckRuntimeHashes(DirectoryInfo runtimePath, string version)
    {
        var     hashesFile    = new FileInfo(Path.Combine(runtimePath.FullName, $"hashes-{version}.json"));
        string? runtimeHashes = null;

        if (!hashesFile.Exists)
        {
            Log.Verbose("[DUPDATE] Hashes file does not exist, redownloading...");

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", PlatformHelpers.GetVersion());

                runtimeHashes = await client.GetStringAsync($"https://aonyx.ffxiv.wang/Dalamud/Release/Runtime/Hashes/{version}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] Could not download hashes for runtime v{Version}", version);
                return false;
            }

            File.WriteAllText(hashesFile.FullName, runtimeHashes);
        }
        else
            runtimeHashes = File.ReadAllText(hashesFile.FullName);

        return CheckIntegrity(runtimePath, runtimeHashes);
    }

    private async Task DownloadRuntime(DirectoryInfo runtimePath, string version)
    {
        // Ensure directory exists
        if (runtimePath.Exists)
            runtimePath.Delete(true);

        runtimePath.Create();

        var dotnetUrl  = $"https://aonyx.ffxiv.wang/Dalamud/Release/Runtime/DotNet/{version}";
        var desktopUrl = $"https://aonyx.ffxiv.wang/Dalamud/Release/Runtime/WindowsDesktop/{version}";

        var downloadPath = PlatformHelpers.GetTempFileName();

        if (File.Exists(downloadPath))
            File.Delete(downloadPath);

        await this.DownloadFile(dotnetUrl, downloadPath, this.defaultTimeout).ConfigureAwait(false);
        ZipFile.ExtractToDirectory(downloadPath, runtimePath.FullName);

        await this.DownloadFile(desktopUrl, downloadPath, this.defaultTimeout).ConfigureAwait(false);
        ZipFile.ExtractToDirectory(downloadPath, runtimePath.FullName);

        File.Delete(downloadPath);
    }

    #endregion

    private static bool CheckIntegrity(DirectoryInfo directory, string hashesJson)
    {
        try
        {
            Log.Verbose("[DUPDATE] 开始检查目录 {Directory} 的完整性", directory.FullName);

            var hashes = JsonConvert.DeserializeObject<Dictionary<string, string>>(hashesJson);
            if (hashes == null) throw new ArgumentNullException(nameof(hashes));

            foreach (var hash in hashes)
            {
                var file   = Path.Combine(directory.FullName, hash.Key.Replace("\\", "/"));
                var hashed = ComputeFileHash(file);

                if (hashed != hash.Value)
                {
                    Log.Error("[DUPDATE] 完整性检查失败: {0} ({1} - {2})", file, hash.Value, hashed);
                    return false;
                }

                Log.Verbose("[DUPDATE] 完整性检查通过: {0} ({1})", file, hashed);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DUPDATE] 完整性检查失败");
            return false;
        }

        return true;
    }
    
    private static bool CanRead(FileInfo info)
    {
        try
        {
            using var stream = info.OpenRead();
            stream.ReadByte();
        }
        catch { return false; }

        return true;
    }
    
    private static string ComputeFileHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var md5    = MD5.Create();

            var hashHash = BitConverter.ToString(md5.ComputeHash(stream)).ToUpperInvariant().Replace("-", string.Empty);
            return hashHash;
        }
        catch (Exception e)
        {
            throw new Exception("Error computing file hash: " + e.Message);
        }
    }
    
    private static void CleanUpOld(DirectoryInfo addonPath, string currentVer)
    {
        if (!addonPath.Exists)
            return;

        foreach (var directory in addonPath.GetDirectories())
        {
            if (directory.Name == "dev" || directory.Name == currentVer) continue;

            try { directory.Delete(true); }
            catch
            {
                // ignored
            }
        }
    }
    
    private static void WriteVersionJson(DirectoryInfo addonPath, string info)
    {
        File.WriteAllText(Path.Combine(addonPath.FullName, "version.json"), info);
    }
    
    public async Task DownloadFile(string url, string path, TimeSpan timeout)
    {
        if (this.forceProxy && url.Contains("/File/Get/")) url = url.Replace("/File/Get/", "/File/GetProxy/");

        using var downloader = new HttpClientDownloadWithProgress(url, path);
        downloader.ProgressChanged += this.ReportOverlayProgress;

        await downloader.Download(timeout).ConfigureAwait(false);
    }
}

public class DalamudIntegrityException(string msg, Exception? inner = null) : Exception(msg, inner);
