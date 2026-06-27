using Microsoft.Extensions.Logging;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Starward.RPC.GameInstall;

namespace Starward.Features.BSL.Backend.Adapters;

internal sealed class WutheringWavesBslAdapter : IBslGameAdapter, IBslGameCacheProvider, IBslGamePackageProvider
{
    private const string ChinaLauncherGameApi = "https://prod-cn-alicdn-gamestarter.kurogame.com/launcher/game/G152/10003_Y8xXrXk65DqFHEDgApn3cpK5lfczpFx5/index.json";
    private const string ChinaLauncherInfoApi = "https://prod-cn-alicdn-gamestarter.kurogame.com/launcher/10003_Y8xXrXk65DqFHEDgApn3cpK5lfczpFx5/G152/information/zh-Hans.json";
    private const string DefaultFolderName = "Wuthering Waves Game";
    private const string ConfigFileName = "launcherDownloadConfig.json";
    private const string PredownloadFolderName = ".predownload";
    private const string PredownloadVersionFileName = "predownload_version.json";
    private const string Md5CacheFileName = ".bsl_wuwa_md5_cache.json";

    private readonly ILogger<WutheringWavesBslAdapter> _logger = AppConfig.GetLogger<WutheringWavesBslAdapter>();
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public WutheringWavesBslAdapter(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string GameKey => "wuthering-waves";

    public string DisplayName => "鸣潮";

    public BslGameSupportLevel SupportLevel => BslGameSupportLevel.Partial;

    public BslGameServerRegion Region => BslGameServerRegion.China;

    public BslGameCapability Capabilities =>
        BslGameCapability.Import |
        BslGameCapability.Launch |
        BslGameCapability.Download |
        BslGameCapability.Update |
        BslGameCapability.Predownload |
        BslGameCapability.Repair |
        BslGameCapability.Uninstall |
        BslGameCapability.Notices |
        BslGameCapability.Background;

    public async Task<BslGameStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        string? installPath = FindInstallPath();
        string? exe = ResolveExecutablePath(installPath);
        WuwaLauncherGameResponse? remoteGame = await TryGetLauncherGameAsync(cancellationToken);
        WuwaRemoteContext? remoteContext = await TryBuildRemoteContextAsync(cancellationToken);
        string? localVersion = ReadLocalVersion(installPath);
        PredownloadVersionInfo? predownloadInfo = ReadPredownloadVersionInfo(installPath);

        bool isInstalled = IsCompleteInstallation(installPath, exe, localVersion);
        bool canInstall = !isInstalled && remoteContext is not null;
        bool canUpdate = isInstalled
                         && remoteContext is not null
                         && !string.IsNullOrWhiteSpace(localVersion)
                         && !string.IsNullOrWhiteSpace(remoteContext.DefaultVersion)
                         && !string.Equals(localVersion, remoteContext.DefaultVersion, StringComparison.OrdinalIgnoreCase);
        bool canPredownload = isInstalled
                              && remoteContext?.PredownloadManifest is not null
                              && !string.IsNullOrWhiteSpace(remoteContext.PredownloadVersion);
        bool canRepair = isInstalled && remoteContext is not null;

        BslGameStatusSnapshot snapshot = new()
        {
            GameKey = GameKey,
            DisplayName = DisplayName,
            Region = Region,
            SupportLevel = SupportLevel,
            Capabilities = Capabilities,
            IsInstalled = isInstalled,
            CanLaunch = isInstalled,
            CanImport = true,
            CanInstall = canInstall,
            CanUpdate = canUpdate,
            CanPredownload = canPredownload,
            CanRepair = canRepair,
            InstallPath = installPath,
            ExecutablePath = exe,
            LatestVersion = remoteContext?.DefaultVersion ?? remoteGame?.Default?.Version,
            LocalVersion = localVersion,
            PredownloadVersion = remoteContext?.PredownloadVersion,
            LastRefreshed = DateTimeOffset.Now,
        };

        snapshot.LaunchSettings = BslLaunchSettingsHelper.CreateGenericSnapshot(
            GameKey,
            DisplayName,
            installPath,
            exe);

        if (!snapshot.IsInstalled)
        {
            snapshot.StatusText = "未安装";
            snapshot.HintText = snapshot.CanInstall
                ? $"可直接下载完整客户端 {snapshot.LatestVersion ?? "未知版本"}"
                : "无法获取鸣潮资源清单，请稍后重试。";
        }
        else if (snapshot.CanUpdate)
        {
            snapshot.StatusText = "可更新";
            snapshot.HintText = $"本地 {snapshot.LocalVersion}，最新 {snapshot.LatestVersion}";
        }
        else
        {
            snapshot.StatusText = "已安装";
            snapshot.HintText = $"当前版本 {snapshot.LocalVersion ?? "未知"}";
        }

        if (snapshot.CanPredownload && remoteContext?.PredownloadVersion is not null)
        {
            snapshot.Warnings.Add($"预下载版本 {remoteContext.PredownloadVersion} 已开放。");
        }

        if (predownloadInfo is not null)
        {
            snapshot.Warnings.Add($"本地已缓存预下载资源 {predownloadInfo.Version}。");
        }

        snapshot.Warnings.Add("当前已改为直接下载游戏资源，不再依赖官方启动器安装包。");
        snapshot.RefreshUiSemantics();
        return snapshot;
    }

    public Task<BslGameLaunchSettingsSnapshot> GetLaunchSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? installPath = FindInstallPath();
        string? executablePath = ResolveExecutablePath(installPath);
        return Task.FromResult(BslLaunchSettingsHelper.CreateGenericSnapshot(
            GameKey,
            DisplayName,
            installPath,
            executablePath));
    }

    public Task<BslGameCacheSnapshot> GetCacheSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? installPath = FindInstallPath();
        PredownloadVersionInfo? predownloadInfo = ReadPredownloadVersionInfo(installPath);
        string? predownloadPath = string.IsNullOrWhiteSpace(installPath)
            ? null
            : Path.Combine(installPath, PredownloadFolderName);

        bool hasPredownloadCache = predownloadInfo is not null
                                   && !string.IsNullOrWhiteSpace(predownloadPath)
                                   && Directory.Exists(predownloadPath);

        return Task.FromResult(new BslGameCacheSnapshot
        {
            GameKey = GameKey,
            DisplayName = DisplayName,
            HasPredownloadCache = hasPredownloadCache,
            PredownloadVersion = predownloadInfo?.Version,
            PredownloadCachePath = hasPredownloadCache ? predownloadPath : null,
            PredownloadCacheSize = hasPredownloadCache ? GetDirectorySize(predownloadPath) : 0,
        });
    }

    public Task<bool> ClearPredownloadCacheAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? installPath = FindInstallPath();
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return Task.FromResult(false);
        }

        string predownloadPath = Path.Combine(installPath, PredownloadFolderName);
        if (!Directory.Exists(predownloadPath))
        {
            return Task.FromResult(false);
        }

        try
        {
            Directory.Delete(predownloadPath, true);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clear Wuthering Waves predownload cache failed.");
            return Task.FromResult(false);
        }
    }

    public async Task<BslGamePackageManifest> GetPackageManifestAsync(CancellationToken cancellationToken = default)
    {
        WuwaRemoteContext context = await BuildRemoteContextAsync(cancellationToken);
        return new BslGamePackageManifest
        {
            GameKey = GameKey,
            DisplayName = DisplayName,
            LatestVersion = context.DefaultVersion,
            PredownloadVersion = context.PredownloadVersion,
            LatestPackageGroups = BuildPackageGroups(context.DefaultManifest.Resource ?? [], context.DefaultResourcesBaseUrl),
            PredownloadPackageGroups = context.PredownloadManifest is null
                ? []
                : BuildPackageGroups(context.PredownloadManifest.Resource ?? [], context.PredownloadResourcesBaseUrl ?? context.DefaultResourcesBaseUrl),
        };
    }

    public async Task<BslLaunchSettingsSaveResult> ImportGameAsync(string? installPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? normalizedInstallPath = TryNormalizeInstallPath(installPath, out string? normalizedPath)
            ? normalizedPath
            : null;
        if (normalizedInstallPath is not null)
        {
            SaveInstallPath(normalizedInstallPath);
        }

        BslGameLaunchSettingsSnapshot launchSettings = await GetLaunchSettingsAsync(cancellationToken);
        BslGameStatusSnapshot status = await GetStatusAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(installPath) && normalizedInstallPath is null)
        {
            launchSettings.InvalidInstallPath = installPath;
        }

        BslLaunchSettingsHelper.ApplyToStatus(status, launchSettings);
        status.RefreshUiSemantics();

        return BslLaunchSettingsHelper.CreateSaveResult(
            GameKey,
            new BslGameLaunchSettingsUpdate
            {
                UpdateInstallPath = true,
                InstallPath = installPath,
            },
            launchSettings,
            status);
    }

    public Task SaveLaunchSettingsAsync(BslGameLaunchSettingsUpdate update, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (update.UpdateInstallPath)
        {
            string? normalizedInstallPath = TryNormalizeInstallPath(update.InstallPath, out string? installPath)
                ? installPath
                : null;
            SaveInstallPathState(normalizedInstallPath);
        }

        BslLaunchSettingsHelper.SaveGenericSettings(GameKey, update);
        return Task.CompletedTask;
    }

    public Task<string?> NormalizeInstallPathAsync(string? installPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TryNormalizeInstallPath(installPath, out string? normalizedPath) ? normalizedPath : null);
    }

    public async Task<IReadOnlyList<BslBannerEntry>> GetBannersAsync(CancellationToken cancellationToken = default)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        WuwaInformationResponse? info = await _httpClient.GetFromJsonAsync<WuwaInformationResponse>($"{ChinaLauncherInfoApi}?_t={timestamp}", cancellationToken);
        List<BslBannerEntry> list = [];

        foreach (WuwaSlideshowItem? item in info?.Slideshow ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Url))
            {
                continue;
            }

            list.Add(new BslBannerEntry
            {
                Title = item.CarouselNotes ?? DisplayName,
                ImageUrl = item.Url,
                Link = item.JumpUrl ?? string.Empty,
            });
        }

        return list;
    }

    public async Task<IReadOnlyList<BslNoticeEntry>> GetNoticesAsync(CancellationToken cancellationToken = default)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        WuwaInformationResponse? info = await _httpClient.GetFromJsonAsync<WuwaInformationResponse>($"{ChinaLauncherInfoApi}?_t={timestamp}", cancellationToken);
        List<BslNoticeEntry> list = [];
        AddGuidanceEntries(list, "活动", info?.Guidance?.Activity?.Contents);
        AddGuidanceEntries(list, "公告", info?.Guidance?.Notice?.Contents);
        AddGuidanceEntries(list, "新闻", info?.Guidance?.News?.Contents);
        return list;
    }

    public async Task<BslBackendTaskItem> ExecuteAsync(BslQueuedActionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ActionType == BslGameActionType.Uninstall)
        {
            return await UninstallAsync(request, cancellationToken);
        }

        switch (request.ActionType)
        {
            case BslGameActionType.Import:
            {
                string? folder = await FileDialogHelper.PickFolderAsync(Starward.Features.ViewHost.MainWindow.Current.Content.XamlRoot);
                if (string.IsNullOrWhiteSpace(folder))
                {
                    return Failed("已取消导入", null, request.ActionType);
                }

                if (!TryNormalizeImportPath(folder, out string? installPath, out string? failureMessage))
                {
                    return Failed("导入失败", failureMessage, request.ActionType, folder);
                }

                SaveInstallPath(installPath!);
                return Success("导入完成", installPath, request.ActionType, "已校验并保存鸣潮安装目录。");
            }
            case BslGameActionType.Launch:
            {
                BslGameLaunchSettingsSnapshot launchSettings = await GetLaunchSettingsAsync(cancellationToken);
                string? installPath = request.InstallPath ?? launchSettings.InstallPath ?? FindInstallPath();
                string? exe = launchSettings.EffectiveExecutablePath ?? ResolveExecutablePath(installPath);
                string? localVersion = ReadLocalVersion(installPath);
                if (!IsCompleteInstallation(installPath, exe, localVersion))
                {
                    return Failed("启动失败", "鸣潮安装未完成或本地版本配置缺失，请先完成安装或重新导入已安装目录。", request.ActionType, installPath);
                }

                string executablePath = exe!;
                Process.Start(BslLaunchSettingsHelper.CreateProcessStartInfo(executablePath, launchSettings.LaunchArgument));
                return Success("已启动", installPath, request.ActionType, Path.GetFileName(executablePath));
            }
            case BslGameActionType.Install:
                return await DownloadOrSyncAsync(request, forceCheckMd5: false, predownload: false, cancellationToken);
            case BslGameActionType.Update:
                return await UpdateAsync(request, cancellationToken);
            case BslGameActionType.Repair:
                return await DownloadOrSyncAsync(request, forceCheckMd5: true, predownload: false, cancellationToken);
            case BslGameActionType.Predownload:
                return await DownloadOrSyncAsync(request, forceCheckMd5: false, predownload: true, cancellationToken);
            default:
                return Failed("暂未接入", "该动作将在后续阶段继续接入。", request.ActionType);
        }
    }

    private async Task<BslBackendTaskItem> UninstallAsync(BslQueuedActionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        BslGameLaunchSettingsSnapshot launchSettings = await GetLaunchSettingsAsync(cancellationToken);
        string? installPath = request.InstallPath ?? launchSettings.InstallPath ?? FindInstallPath();
        string? executablePath = launchSettings.EffectiveExecutablePath ?? ResolveExecutablePath(installPath);

        if (!BslUninstallHelper.TryValidateInstallDirectory(
                installPath,
                out string? normalizedInstallPath,
                out string? failureMessage,
                executablePath,
                Path.Combine(installPath ?? string.Empty, ConfigFileName),
                Path.Combine(installPath ?? string.Empty, DefaultFolderName, ConfigFileName)))
        {
            return Failed(BslUninstallHelper.FailedStatus, failureMessage, request.ActionType, installPath);
        }

        if (IsGameRunning(executablePath))
        {
            return Failed(BslUninstallHelper.FailedStatus, BslUninstallHelper.GameRunningMessage, request.ActionType, normalizedInstallPath);
        }

        try
        {
            if (!BslDownloadHelper.DeleteDirectoryIfExists(normalizedInstallPath!))
            {
                return Failed(BslUninstallHelper.FailedStatus, BslUninstallHelper.DeleteDirectoryFailureMessage, request.ActionType, normalizedInstallPath);
            }

            SaveInstallPathState(null);
            BslLaunchSettingsHelper.SaveGenericSettings(GameKey, BslUninstallHelper.CreateResetLaunchSettingsUpdate());

            return Success(BslUninstallHelper.CompletedStatus, null, request.ActionType, BslUninstallHelper.CleanupCompletedDetail);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Uninstall Wuthering Waves failed.");
            return Failed(BslUninstallHelper.FailedStatus, ex.Message, request.ActionType, normalizedInstallPath);
        }
    }

    private async Task<BslBackendTaskItem> UpdateAsync(BslQueuedActionRequest request, CancellationToken cancellationToken)
    {
        string installPath = ResolveInstallRoot(request.InstallPath);
        string? localVersion = ReadLocalVersion(installPath);
        WuwaRemoteContext? context = await BuildRemoteContextAsync(cancellationToken);

        if (TryApplyPredownloadCache(installPath, context, out string? appliedPredownloadVersion))
        {
            WriteLocalConfig(installPath, appliedPredownloadVersion!);
            return await DownloadOrSyncAsync(request, forceCheckMd5: false, predownload: false, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(localVersion) && !string.IsNullOrWhiteSpace(context.DefaultVersion))
        {
            WuwaPatchConfig? patch = context.GameApi?.Default?.Config?.PatchConfig?
                .FirstOrDefault(x => string.Equals(x.Version, localVersion, StringComparison.OrdinalIgnoreCase));
            if (patch is not null
                && !string.IsNullOrWhiteSpace(patch.IndexFile)
                && !string.IsNullOrWhiteSpace(patch.BaseUrl))
            {
                WuwaResourceIndexResponse patchManifest = await GetResourceIndexAsync(context.BaseCdnUrl, patch.IndexFile, cancellationToken);
                WuwaManifestContext manifestContext = new(
                    Version: context.DefaultVersion ?? localVersion,
                    ResourcesBaseUrl: JoinUrl(context.BaseCdnUrl, patch.BaseUrl),
                    ResourceFiles: patchManifest.Resource ?? [],
                    Predownload: false,
                    SourceVersion: localVersion);

                return await ExecuteManifestSyncAsync(
                    request,
                    installPath,
                    manifestContext,
                    forceCheckMd5: false,
                    cancellationToken);
            }
        }

        return await DownloadOrSyncAsync(request, forceCheckMd5: true, predownload: false, cancellationToken);
    }

    private async Task<BslBackendTaskItem> DownloadOrSyncAsync(
        BslQueuedActionRequest request,
        bool forceCheckMd5,
        bool predownload,
        CancellationToken cancellationToken)
    {
        string installPath = ResolveInstallRoot(request.InstallPath);
        WuwaRemoteContext context = await BuildRemoteContextAsync(cancellationToken);

        WuwaManifestContext manifestContext;
        if (predownload)
        {
            if (context.PredownloadManifest is null || string.IsNullOrWhiteSpace(context.PredownloadResourcesBaseUrl))
            {
                return Failed("预下载未开放", "当前未获取到鸣潮预下载资源。", request.ActionType);
            }

            manifestContext = new WuwaManifestContext(
                Version: context.PredownloadVersion ?? "unknown",
                ResourcesBaseUrl: context.PredownloadResourcesBaseUrl,
                ResourceFiles: context.PredownloadManifest.Resource ?? [],
                Predownload: true,
                SourceVersion: context.DefaultVersion);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(context.DefaultResourcesBaseUrl))
            {
                return Failed("获取资源失败", "未获取到鸣潮资源路径配置。", request.ActionType);
            }

            manifestContext = new WuwaManifestContext(
                Version: context.DefaultVersion ?? "unknown",
                ResourcesBaseUrl: context.DefaultResourcesBaseUrl,
                ResourceFiles: context.DefaultManifest?.Resource ?? [],
                Predownload: false,
                SourceVersion: null);
        }

        return await ExecuteManifestSyncAsync(
            request,
            installPath,
            manifestContext,
            forceCheckMd5,
            cancellationToken);
    }

    private async Task<BslBackendTaskItem> ExecuteManifestSyncAsync(
        BslQueuedActionRequest request,
        string installPath,
        WuwaManifestContext manifestContext,
        bool forceCheckMd5,
        CancellationToken cancellationToken)
    {
        if (manifestContext.ResourceFiles.Count == 0)
        {
            return Failed("资源清单为空", "未从鸣潮接口获取到有效资源文件列表。", request.ActionType);
        }

        Directory.CreateDirectory(installPath);
        SaveInstallPath(installPath);

        string rootPath = manifestContext.Predownload
            ? Path.Combine(installPath, PredownloadFolderName)
            : installPath;
        string predownloadCachePath = Path.Combine(installPath, PredownloadFolderName);
        Directory.CreateDirectory(rootPath);

        Md5CacheStore md5Cache = LoadMd5Cache(rootPath);
        List<DownloadJob> downloadJobs = new();
        long bytesToDownload = 0;
        int verifiedCount = 0;

        BslBackendTaskItem progressItem = new()
        {
            GameKey = GameKey,
            DisplayName = DisplayName,
            ActionType = request.ActionType,
            State = BslBackendTaskState.Running,
            InstallState = GameInstallState.Waiting,
            StatusText = manifestContext.Predownload ? "正在准备预下载资源" : "正在校验资源",
            DetailText = $"0 / {manifestContext.ResourceFiles.Count}",
            InstallPath = installPath,
            Progress = 0,
        };

        if (request.ProgressCallback is not null)
        {
            await request.ProgressCallback(progressItem);
        }

        foreach (WuwaResourceFile item in manifestContext.ResourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(item.Dest) || string.IsNullOrWhiteSpace(item.Md5))
            {
                verifiedCount++;
                continue;
            }

            string destinationPath = Path.Combine(rootPath, item.Dest.Replace('/', Path.DirectorySeparatorChar));
            bool needsDownload = !File.Exists(destinationPath);

            if (!needsDownload)
            {
                FileInfo fileInfo = new(destinationPath);
                if (fileInfo.Length != item.Size)
                {
                    needsDownload = true;
                }
                else if (forceCheckMd5)
                {
                    string? actualMd5 = await GetFileMd5Async(destinationPath, md5Cache, cancellationToken);
                    needsDownload = !string.Equals(actualMd5, item.Md5, StringComparison.OrdinalIgnoreCase);
                }
            }

            if (needsDownload)
            {
                string url = JoinUrl(manifestContext.ResourcesBaseUrl, item.Dest);
                downloadJobs.Add(new DownloadJob(url, destinationPath, item.Size, item.Md5));
                bytesToDownload += item.Size;
            }

            verifiedCount++;
            progressItem.StatusText = manifestContext.Predownload ? "正在检查预下载资源" : "正在校验资源";
            progressItem.InstallState = GameInstallState.Verifying;
            progressItem.DetailText = $"{verifiedCount} / {manifestContext.ResourceFiles.Count}";
            progressItem.Progress = Math.Clamp((double)verifiedCount / manifestContext.ResourceFiles.Count * 0.25d, 0, 0.25d);

            if (request.ProgressCallback is not null)
            {
                await request.ProgressCallback(progressItem);
            }
        }

        if (downloadJobs.Count > 0)
        {
            BslDiskSpacePlan diskPlan = BuildDiskSpacePlan(rootPath, bytesToDownload, manifestContext.Predownload);
            BslDiskSpaceCheckResult diskCheck = BslDownloadHelper.CheckDiskSpace(diskPlan);
            if (!diskCheck.IsSatisfied)
            {
                return Failed(
                    BslDownloadHelper.BuildDiskSpaceFailureStatus(request.ActionType),
                    BslDownloadHelper.BuildDiskSpaceFailureMessage(request.ActionType, diskCheck),
                    request.ActionType);
            }

            progressItem.StatusText = manifestContext.Predownload ? "正在下载预下载资源" : "正在下载游戏资源";
            progressItem.InstallState = GameInstallState.Downloading;
            progressItem.DetailText = $"0 / {BslDownloadHelper.FormatBytes(bytesToDownload)}";
            progressItem.Progress = 0.25d;
            if (request.ProgressCallback is not null)
            {
                await request.ProgressCallback(progressItem);
            }

            long downloadedBytes = 0;
            int downloadedCount = 0;

            foreach (DownloadJob job in downloadJobs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await BslDownloadHelper.DownloadFileWithRetryAsync(
                    _httpClient,
                    job.Url,
                    job.Path,
                    async (currentBytes, totalBytes) =>
                    {
                        long completedBeforeCurrent = downloadedBytes;
                        long currentOverall = completedBeforeCurrent + currentBytes;
                        double progressRatio = bytesToDownload <= 0 ? 1d : (double)currentOverall / bytesToDownload;
                        progressItem.State = BslBackendTaskState.Running;
                        progressItem.InstallState = GameInstallState.Downloading;
                        progressItem.StatusText = manifestContext.Predownload ? "正在下载预下载资源" : "正在下载游戏资源";
                        progressItem.DetailText = $"{BslDownloadHelper.FormatBytes(currentOverall)} / {BslDownloadHelper.FormatBytes(bytesToDownload)} · {Path.GetFileName(job.Path)}";
                        progressItem.Progress = 0.25d + Math.Clamp(progressRatio, 0, 1) * 0.7d;

                        if (request.ProgressCallback is not null)
                        {
                            await request.ProgressCallback(progressItem);
                        }
                    },
                    (attempt, ex) =>
                    {
                        progressItem.State = BslBackendTaskState.Running;
                        progressItem.InstallState = GameInstallState.Queueing;
                        progressItem.StatusText = "下载失败，准备重试";
                        progressItem.DetailText = $"第 {attempt} 次重试前失败：{ex.Message}";
                    },
                    maxAttempts: Math.Max(1, request.MaxRetryCount + 1),
                    expectedTotalBytes: job.Size,
                    allowResume: true,
                    cancellationToken);

                string actualMd5 = await ComputeFileMd5Async(job.Path, cancellationToken);
                if (!string.Equals(actualMd5, job.ExpectedMd5, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(job.Path);
                    throw new InvalidOperationException($"文件校验失败：{Path.GetFileName(job.Path)}");
                }

                md5Cache.Set(job.Path, actualMd5, new FileInfo(job.Path).Length, File.GetLastWriteTimeUtc(job.Path));
                downloadedBytes += job.Size;
                downloadedCount++;

                progressItem.StatusText = manifestContext.Predownload ? "正在下载预下载资源" : "正在下载游戏资源";
                progressItem.InstallState = GameInstallState.Downloading;
                progressItem.DetailText = $"{downloadedCount} / {downloadJobs.Count} · {BslDownloadHelper.FormatBytes(downloadedBytes)}";
                progressItem.Progress = 0.25d + (bytesToDownload <= 0 ? 0.7d : Math.Clamp((double)downloadedBytes / bytesToDownload, 0, 1) * 0.7d);

                if (request.ProgressCallback is not null)
                {
                    await request.ProgressCallback(progressItem);
                }
            }
        }

        try
        {
            SaveMd5Cache(rootPath, md5Cache);

            if (manifestContext.Predownload)
            {
                PredownloadVersionInfo versionInfo = new()
                {
                    Version = manifestContext.Version,
                    Server = "cn",
                };
                await File.WriteAllTextAsync(
                    Path.Combine(rootPath, PredownloadVersionFileName),
                    JsonSerializer.Serialize(versionInfo, _jsonOptions),
                    Encoding.UTF8,
                    cancellationToken);

                return Success(
                    "预下载完成",
                    installPath,
                    request.ActionType,
                    $"{manifestContext.Version} 预下载资源已缓存。");
            }

            WriteLocalConfig(installPath, manifestContext.Version);
            if (!string.IsNullOrWhiteSpace(manifestContext.SourceVersion))
            {
                ClearPredownloadCacheIfMatches(installPath, manifestContext.Version);
            }

            progressItem.StatusText = "同步完成";
            progressItem.InstallState = GameInstallState.Finish;
            progressItem.DetailText = $"{manifestContext.Version} · {downloadJobs.Count} 个文件更新";
            progressItem.Progress = 1;

            return Success(
                forceCheckMd5 ? "修复完成" : request.ActionType == BslGameActionType.Update ? "更新完成" : "安装完成",
                installPath,
                request.ActionType,
                $"{manifestContext.Version} 已就绪。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Wuthering Waves manifest sync failed: {ActionType} {Predownload}", request.ActionType, manifestContext.Predownload);
            return BuildFailureWithResidualCacheIfPresent(
                manifestContext.Predownload ? "预下载失败" : request.ActionType == BslGameActionType.Update ? "更新失败" : "同步失败",
                ex.Message,
                request.ActionType,
                installPath,
                manifestContext.Predownload ? predownloadCachePath : null);
        }
    }

    private async Task<WuwaRemoteContext> BuildRemoteContextAsync(CancellationToken cancellationToken)
    {
        WuwaRemoteContext? context = await TryBuildRemoteContextAsync(cancellationToken);
        return context ?? throw new InvalidOperationException("无法获取鸣潮远程资源配置。");
    }

    private async Task<WuwaRemoteContext?> TryBuildRemoteContextAsync(CancellationToken cancellationToken)
    {
        try
        {
            WuwaLauncherGameResponse? gameApi = await TryGetLauncherGameAsync(cancellationToken);
            if (gameApi?.Default?.Config is null)
            {
                return null;
            }

            string? cdn = gameApi.Default.CdnList?
                .Where(x => x.K1 == 1 && x.K2 == 1 && !string.IsNullOrWhiteSpace(x.Url))
                .OrderByDescending(x => x.Priority)
                .Select(x => x.Url)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(cdn) || string.IsNullOrWhiteSpace(gameApi.Default.Config.IndexFile))
            {
                return null;
            }

            WuwaResourceIndexResponse defaultManifest = await GetResourceIndexAsync(cdn, gameApi.Default.Config.IndexFile, cancellationToken);

            WuwaResourceIndexResponse? predownloadManifest = null;
            string? predownloadBaseUrl = null;
            if (!string.IsNullOrWhiteSpace(gameApi.Predownload?.Config?.IndexFile))
            {
                predownloadManifest = await GetResourceIndexAsync(cdn, gameApi.Predownload.Config.IndexFile!, cancellationToken);
                predownloadBaseUrl = JoinUrl(cdn, gameApi.Predownload.Config.BaseUrl ?? string.Empty);
            }

            return new WuwaRemoteContext(
                gameApi,
                cdn,
                defaultManifest,
                JoinUrl(cdn, gameApi.Default.Config.BaseUrl ?? string.Empty),
                predownloadManifest,
                predownloadBaseUrl,
                gameApi.Default.Version,
                gameApi.Predownload?.Version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Build Wuthering Waves remote context failed.");
            return null;
        }
    }

    private async Task<WuwaResourceIndexResponse> GetResourceIndexAsync(string cdnBaseUrl, string indexFile, CancellationToken cancellationToken)
    {
        string url = JoinUrl(cdnBaseUrl, indexFile);
        WuwaResourceIndexResponse? manifest = await _httpClient.GetFromJsonAsync<WuwaResourceIndexResponse>(url, cancellationToken);
        return manifest ?? throw new InvalidOperationException($"无法读取鸣潮资源清单：{indexFile}");
    }

    private async Task<WuwaLauncherGameResponse?> TryGetLauncherGameAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<WuwaLauncherGameResponse>(ChinaLauncherGameApi, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fetch Wuthering Waves launcher game api failed.");
            return null;
        }
    }

    private static List<BslGamePackageGroup> BuildPackageGroups(List<WuwaResourceFile> files, string baseUrl)
    {
        return files
            .Where(x => !string.IsNullOrWhiteSpace(x.Dest))
            .GroupBy(x => GetTopLevelFolder(x.Dest!))
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new BslGamePackageGroup
            {
                Name = $"{group.Key} ({group.Count()})",
                Items = group
                    .OrderBy(x => x.Dest, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new BslGamePackageItem
                    {
                        FileName = x.Dest ?? string.Empty,
                        Url = JoinUrl(baseUrl, x.Dest ?? string.Empty),
                        Md5 = x.Md5 ?? string.Empty,
                        PackageSize = Math.Max(0, x.Size),
                    })
                    .ToList(),
            })
            .ToList();
    }

    private static string GetTopLevelFolder(string path)
    {
        string normalized = path.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Root";
        }

        int index = normalized.IndexOf('/');
        return index <= 0 ? "Root" : normalized[..index];
    }

    private static long GetDirectorySize(string? path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return 0;
            }

            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(x => x.Length);
        }
        catch
        {
            return 0;
        }
    }

    private string? FindInstallPath()
    {
        string? saved = BslBackendSetting.GetInstallPath(GameKey);
        if (TryNormalizeInstallPath(saved, out string? normalizedSaved))
        {
            SaveInstallPath(normalizedSaved!);
            return normalizedSaved;
        }

        string? defaultRoot = AppConfig.DefaultGameInstallationPath;
        if (!string.IsNullOrWhiteSpace(defaultRoot))
        {
            string candidate = Path.Combine(defaultRoot, DefaultFolderName);
            if (TryNormalizeInstallPath(candidate, out string? normalizedCandidate))
            {
                SaveInstallPath(normalizedCandidate!);
                return normalizedCandidate;
            }
        }

        return saved;
    }

    private string ResolveInstallRoot(string? requestedPath)
    {
        string? installPath = requestedPath ?? FindInstallPath();
        if (!string.IsNullOrWhiteSpace(installPath))
        {
            return Path.GetFullPath(installPath);
        }

        string? defaultRoot = AppConfig.DefaultGameInstallationPath;
        if (!string.IsNullOrWhiteSpace(defaultRoot))
        {
            return Path.Combine(defaultRoot, DefaultFolderName);
        }

        return Path.Combine(AppConfig.CacheFolder, "Games", DefaultFolderName);
    }

    private void SaveInstallPath(string path)
    {
        BslBackendSetting.SetInstallPath(GameKey, Path.GetFullPath(path));
    }

    private void SaveInstallPathState(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            BslBackendSetting.SetInstallPath(GameKey, null);
        }
        else
        {
            SaveInstallPath(path);
        }
    }

    private PredownloadVersionInfo? ReadPredownloadVersionInfo(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return null;
        }

        try
        {
            string filePath = Path.Combine(installPath, PredownloadFolderName, PredownloadVersionFileName);
            if (!File.Exists(filePath))
            {
                return null;
            }

            string json = File.ReadAllText(filePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<PredownloadVersionInfo>(json, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveExecutablePath(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return null;
        }

        string wrapper = Path.Combine(installPath, "Wuthering Waves.exe");
        if (File.Exists(wrapper))
        {
            return wrapper;
        }

        string shipping = Path.Combine(installPath, "Client", "Binaries", "Win64", "Client-Win64-Shipping.exe");
        if (File.Exists(shipping))
        {
            return shipping;
        }

        string nestedShipping = Path.Combine(installPath, DefaultFolderName, "Client", "Binaries", "Win64", "Client-Win64-Shipping.exe");
        if (File.Exists(nestedShipping))
        {
            return nestedShipping;
        }

        return null;
    }

    private static bool IsCompleteInstallation(string? installPath, string? executablePath, string? localVersion)
    {
        return !string.IsNullOrWhiteSpace(installPath)
               && Directory.Exists(installPath)
               && !string.IsNullOrWhiteSpace(executablePath)
               && File.Exists(executablePath)
               && !string.IsNullOrWhiteSpace(localVersion);
    }

    private static bool TryNormalizeImportPath(string? path, out string? normalizedPath, out string? failureMessage)
    {
        if (!TryNormalizeInstallPath(path, out normalizedPath))
        {
            failureMessage = "所选目录中未找到鸣潮可执行文件或启动器配置，请选择游戏安装根目录。";
            return false;
        }

        failureMessage = null;
        return true;
    }

    private static bool TryNormalizeInstallPath(string? path, out string? normalizedPath)
    {
        normalizedPath = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                return false;
            }

            if (ResolveExecutablePath(fullPath) is not null)
            {
                normalizedPath = fullPath;
                return true;
            }

            string nestedRoot = Path.Combine(fullPath, DefaultFolderName);
            if (Directory.Exists(nestedRoot) && ResolveExecutablePath(nestedRoot) is not null)
            {
                normalizedPath = nestedRoot;
                return true;
            }

            if (File.Exists(Path.Combine(fullPath, ConfigFileName))
                || File.Exists(Path.Combine(fullPath, DefaultFolderName, ConfigFileName)))
            {
                normalizedPath = fullPath;
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGameRunning(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        string processName = Path.GetFileNameWithoutExtension(executablePath);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        int currentSessionId = Process.GetCurrentProcess().SessionId;
        return Process.GetProcessesByName(processName)
            .Any(p =>
            {
                try
                {
                    return p.SessionId == currentSessionId && !p.HasExited;
                }
                catch
                {
                    return false;
                }
            });
    }

    private static string? ReadLocalVersion(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return null;
        }

        try
        {
            string launcherConfig = Path.Combine(installPath, ConfigFileName);
            if (!File.Exists(launcherConfig))
            {
                launcherConfig = Path.Combine(installPath, DefaultFolderName, ConfigFileName);
            }

            if (!File.Exists(launcherConfig))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(launcherConfig, Encoding.UTF8));
            return document.RootElement.TryGetProperty("version", out JsonElement versionElement)
                ? versionElement.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void WriteLocalConfig(string installPath, string version)
    {
        WuwaLocalConfig config = new()
        {
            AppId = "10003",
            Group = "default",
            Version = version,
        };
        string path = Path.Combine(installPath, ConfigFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(config, _jsonOptions), Encoding.UTF8);
    }

    private void ClearPredownloadCacheIfMatches(string installPath, string currentVersion)
    {
        try
        {
            string folder = Path.Combine(installPath, PredownloadFolderName);
            string versionPath = Path.Combine(folder, PredownloadVersionFileName);
            if (!Directory.Exists(folder) || !File.Exists(versionPath))
            {
                return;
            }

            PredownloadVersionInfo? info = JsonSerializer.Deserialize<PredownloadVersionInfo>(File.ReadAllText(versionPath, Encoding.UTF8), _jsonOptions);
            if (info is not null && string.Equals(info.Version, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(folder, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Clear Wuthering Waves predownload cache failed.");
        }
    }

    private bool TryApplyPredownloadCache(string installPath, WuwaRemoteContext context, out string? appliedVersion)
    {
        appliedVersion = null;

        PredownloadVersionInfo? info = ReadPredownloadVersionInfo(installPath);
        if (info is null || string.IsNullOrWhiteSpace(context.DefaultVersion))
        {
            return false;
        }

        if (!string.Equals(info.Version, context.DefaultVersion, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string predownloadRoot = Path.Combine(installPath, PredownloadFolderName);
        if (!Directory.Exists(predownloadRoot))
        {
            return false;
        }

        foreach (string sourcePath in Directory.EnumerateFiles(predownloadRoot, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(sourcePath), PredownloadVersionFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(sourcePath), Md5CacheFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(predownloadRoot, sourcePath);
            string targetPath = Path.Combine(installPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
        }

        appliedVersion = info.Version;
        return true;
    }

    private Md5CacheStore LoadMd5Cache(string rootPath)
    {
        try
        {
            string cachePath = Path.Combine(rootPath, Md5CacheFileName);
            if (!File.Exists(cachePath))
            {
                return new Md5CacheStore();
            }

            string json = File.ReadAllText(cachePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<Md5CacheStore>(json, _jsonOptions) ?? new Md5CacheStore();
        }
        catch
        {
            return new Md5CacheStore();
        }
    }

    private void SaveMd5Cache(string rootPath, Md5CacheStore cache)
    {
        try
        {
            string path = Path.Combine(rootPath, Md5CacheFileName);
            File.WriteAllText(path, JsonSerializer.Serialize(cache, _jsonOptions), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Save Wuthering Waves md5 cache failed.");
        }
    }

    private async Task<string?> GetFileMd5Async(string filePath, Md5CacheStore cache, CancellationToken cancellationToken)
    {
        FileInfo info = new(filePath);
        string relativeKey = filePath;
        Md5CacheEntry? entry = cache.Entries.FirstOrDefault(x => string.Equals(x.Path, relativeKey, StringComparison.OrdinalIgnoreCase));
        if (entry is not null
            && entry.Size == info.Length
            && entry.LastWriteTimeUtc == info.LastWriteTimeUtc)
        {
            return entry.Md5;
        }

        string md5 = await ComputeFileMd5Async(filePath, cancellationToken);
        cache.Set(relativeKey, md5, info.Length, info.LastWriteTimeUtc);
        return md5;
    }

    private static async Task<string> ComputeFileMd5Async(string filePath, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 64,
            useAsync: true);
        using MD5 md5 = MD5.Create();
        byte[] hash = await md5.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void AddGuidanceEntries(List<BslNoticeEntry> list, string category, List<WuwaContentItem>? items)
    {
        foreach (WuwaContentItem item in items ?? [])
        {
            list.Add(new BslNoticeEntry
            {
                Category = category,
                Title = item.Content ?? string.Empty,
                DateText = item.Time ?? string.Empty,
                Link = item.JumpUrl ?? string.Empty,
            });
        }
    }

    private static string JoinUrl(string baseUrl, string relativePath)
    {
        string left = baseUrl.EndsWith('/') ? baseUrl : $"{baseUrl}/";
        string right = relativePath.TrimStart('/');
        return $"{left}{right}";
    }

    private static BslDiskSpacePlan BuildDiskSpacePlan(string rootPath, long bytesToDownload, bool predownload)
    {
        long normalizedBytes = Math.Max(0, bytesToDownload);
        long stagingBytes = normalizedBytes;
        long patchBytes = predownload ? normalizedBytes / 5 : normalizedBytes / 10;
        return BslDownloadHelper.CreateDiskSpacePlan(
            rootPath,
            downloadBytes: normalizedBytes,
            stagingBytes: stagingBytes,
            patchBytes: patchBytes,
            finalWriteBytes: 0,
            safetyRatio: 0.08d);
    }

    private BslBackendTaskItem Success(string status, string? path, BslGameActionType actionType, string? detail)
    {
        return new BslBackendTaskItem
        {
            GameKey = GameKey,
            DisplayName = DisplayName,
            ActionType = actionType,
            State = BslBackendTaskState.Succeeded,
            InstallState = GameInstallState.Finish,
            StatusText = status,
            DetailText = detail,
            InstallPath = path,
            Progress = 1,
        };
    }

    private BslBackendTaskItem BuildFailureWithResidualCacheIfPresent(
        string status,
        string? detail,
        BslGameActionType actionType,
        string? installPath,
        string? residualCachePath)
    {
        string? normalizedResidualCachePath = BslDownloadHelper.NormalizeExistingPath(residualCachePath);
        if (actionType is BslGameActionType.Update or BslGameActionType.Predownload
            && normalizedResidualCachePath is not null)
        {
            return Failed(
                status,
                detail,
                actionType,
                installPath,
                normalizedResidualCachePath,
                BslDownloadHelper.BuildResidualCacheHint(actionType, normalizedResidualCachePath));
        }

        return Failed(status, detail, actionType, installPath);
    }

    private BslBackendTaskItem Failed(
        string status,
        string? detail,
        BslGameActionType actionType,
        string? path = null,
        string? residualCachePath = null,
        string? cleanupHint = null)
    {
        string? normalizedResidualCachePath = BslDownloadHelper.NormalizeExistingPath(residualCachePath);
        BslBackendIssueKind issueKind = normalizedResidualCachePath is not null
            ? BslBackendIssueKind.ResidualCache
            : BslDownloadHelper.ClassifyIssue(status, detail);
        return new BslBackendTaskItem
        {
            GameKey = GameKey,
            DisplayName = DisplayName,
            ActionType = actionType,
            State = BslBackendTaskState.Failed,
            InstallState = GameInstallState.Error,
            StatusText = status,
            DetailText = detail,
            InstallPath = path,
            HasResidualCache = normalizedResidualCachePath is not null,
            ResidualCachePath = normalizedResidualCachePath,
            CleanupHint = cleanupHint,
            RecommendManualCleanup = normalizedResidualCachePath is not null,
            IssueKind = issueKind,
            SuggestedAction = normalizedResidualCachePath is not null
                ? BslBackendSuggestedAction.CleanResidualCache
                : BslDownloadHelper.SuggestAction(issueKind),
            Progress = 0,
        };
    }

    private sealed class WuwaLauncherGameResponse
    {
        [JsonPropertyName("default")]
        public WuwaLauncherGameVersion? Default { get; set; }

        [JsonPropertyName("predownload")]
        public WuwaPredownloadEntry? Predownload { get; set; }
    }

    private sealed class WuwaLauncherGameVersion
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("cdnList")]
        public List<WuwaCdnEntry>? CdnList { get; set; }

        [JsonPropertyName("config")]
        public WuwaGameConfig? Config { get; set; }
    }

    private sealed class WuwaPredownloadEntry
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("config")]
        public WuwaGameConfig? Config { get; set; }
    }

    private sealed class WuwaGameConfig
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("baseUrl")]
        public string? BaseUrl { get; set; }

        [JsonPropertyName("indexFile")]
        public string? IndexFile { get; set; }

        [JsonPropertyName("patchConfig")]
        public List<WuwaPatchConfig>? PatchConfig { get; set; }
    }

    private sealed class WuwaPatchConfig
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("baseUrl")]
        public string? BaseUrl { get; set; }

        [JsonPropertyName("indexFile")]
        public string? IndexFile { get; set; }
    }

    private sealed class WuwaCdnEntry
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("P")]
        public int Priority { get; set; }

        [JsonPropertyName("K1")]
        public int K1 { get; set; }

        [JsonPropertyName("K2")]
        public int K2 { get; set; }
    }

    private sealed class WuwaResourceIndexResponse
    {
        [JsonPropertyName("resource")]
        public List<WuwaResourceFile>? Resource { get; set; }
    }

    private sealed class WuwaResourceFile
    {
        [JsonPropertyName("dest")]
        public string? Dest { get; set; }

        [JsonPropertyName("md5")]
        public string? Md5 { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    private sealed class WuwaInformationResponse
    {
        [JsonPropertyName("guidance")]
        public WuwaGuidance? Guidance { get; set; }

        [JsonPropertyName("slideshow")]
        public List<WuwaSlideshowItem>? Slideshow { get; set; }
    }

    private sealed class WuwaGuidance
    {
        [JsonPropertyName("activity")]
        public WuwaGuidanceGroup? Activity { get; set; }

        [JsonPropertyName("notice")]
        public WuwaGuidanceGroup? Notice { get; set; }

        [JsonPropertyName("news")]
        public WuwaGuidanceGroup? News { get; set; }
    }

    private sealed class WuwaGuidanceGroup
    {
        [JsonPropertyName("contents")]
        public List<WuwaContentItem>? Contents { get; set; }
    }

    private sealed class WuwaContentItem
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("jumpUrl")]
        public string? JumpUrl { get; set; }

        [JsonPropertyName("time")]
        public string? Time { get; set; }
    }

    private sealed class WuwaSlideshowItem
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("jumpUrl")]
        public string? JumpUrl { get; set; }

        [JsonPropertyName("carouselNotes")]
        public string? CarouselNotes { get; set; }
    }

    private sealed record WuwaRemoteContext(
        WuwaLauncherGameResponse GameApi,
        string BaseCdnUrl,
        WuwaResourceIndexResponse DefaultManifest,
        string DefaultResourcesBaseUrl,
        WuwaResourceIndexResponse? PredownloadManifest,
        string? PredownloadResourcesBaseUrl,
        string? DefaultVersion,
        string? PredownloadVersion);

    private sealed record WuwaManifestContext(
        string Version,
        string ResourcesBaseUrl,
        List<WuwaResourceFile> ResourceFiles,
        bool Predownload,
        string? SourceVersion);

    private sealed record DownloadJob(string Url, string Path, long Size, string ExpectedMd5);

    private sealed class WuwaLocalConfig
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("appId")]
        public string AppId { get; set; } = "10003";

        [JsonPropertyName("group")]
        public string Group { get; set; } = "default";
    }

    private sealed class PredownloadVersionInfo
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("server")]
        public string Server { get; set; } = "cn";
    }

    private sealed class Md5CacheStore
    {
        [JsonPropertyName("entries")]
        public List<Md5CacheEntry> Entries { get; set; } = [];

        public void Set(string path, string md5, long size, DateTime lastWriteTimeUtc)
        {
            Md5CacheEntry? existing = Entries.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                Entries.Add(new Md5CacheEntry
                {
                    Path = path,
                    Md5 = md5,
                    Size = size,
                    LastWriteTimeUtc = lastWriteTimeUtc,
                });
            }
            else
            {
                existing.Md5 = md5;
                existing.Size = size;
                existing.LastWriteTimeUtc = lastWriteTimeUtc;
            }
        }
    }

    private sealed class Md5CacheEntry
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("md5")]
        public string Md5 { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("lastWriteTimeUtc")]
        public DateTime LastWriteTimeUtc { get; set; }
    }
}
