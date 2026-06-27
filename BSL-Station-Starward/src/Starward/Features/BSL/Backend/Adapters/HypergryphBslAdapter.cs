using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpHDiffPatch.Core;
using Starward.Helpers;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Starward.RPC.GameInstall;

namespace Starward.Features.BSL.Backend.Adapters;

internal sealed class HypergryphBslAdapter : IBslGameAdapter, IBslGameCacheProvider, IBslGamePackageProvider
{
    private const string DownloadCacheFolderName = ".bsl_download_cache";
    private const string DownloadCacheVersionFileName = "version.json";
    private const string ManifestCacheFileName = "game_files";

    private readonly ILogger<HypergryphBslAdapter> _logger = AppConfig.GetLogger<HypergryphBslAdapter>();
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _gameKey;
    private readonly string _displayName;
    private readonly BslGameServerRegion _region;
    private readonly string _apiUrl;
    private readonly string _webApiUrl;
    private readonly string _appCode;
    private readonly string _launcherAppCode;
    private readonly string _launcherTa;
    private readonly string _channel;
    private readonly string _subChannel;
    private readonly string _seq;
    private readonly string _registryKeyName;
    private readonly string _executableName;
    private readonly string _launcherFolderName;
    private readonly string _homePage;

    public HypergryphBslAdapter(
        string gameKey,
        string displayName,
        BslGameServerRegion region,
        string apiUrl,
        string webApiUrl,
        string appCode,
        string launcherAppCode,
        string launcherTa,
        string channel,
        string subChannel,
        string seq,
        string registryKeyName,
        string executableName,
        string launcherFolderName,
        string homePage,
        HttpClient httpClient)
    {
        _gameKey = gameKey;
        _displayName = displayName;
        _region = region;
        _apiUrl = apiUrl;
        _webApiUrl = webApiUrl;
        _appCode = appCode;
        _launcherAppCode = launcherAppCode;
        _launcherTa = launcherTa;
        _channel = channel;
        _subChannel = subChannel;
        _seq = seq;
        _registryKeyName = registryKeyName;
        _executableName = executableName;
        _launcherFolderName = launcherFolderName;
        _homePage = homePage;
        _httpClient = httpClient;
    }

    public string GameKey => _gameKey;

    public string DisplayName => _displayName;

    public BslGameSupportLevel SupportLevel => BslGameSupportLevel.Partial;

    public BslGameServerRegion Region => _region;

    public BslGameCapability Capabilities =>
        BslGameCapability.Import |
        BslGameCapability.Launch |
        BslGameCapability.Download |
        BslGameCapability.Update |
        BslGameCapability.Repair |
        BslGameCapability.Uninstall |
        BslGameCapability.Notices |
        BslGameCapability.Background;

    public async Task<BslGameStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        string? installPath = FindInstallPath();
        string? executablePath = ResolveExecutablePath(installPath);
        string? localVersion = ReadLocalVersion(installPath);
        HgLatestGameResponse? latestGame = await TryGetLatestGameAsync(localVersion, cancellationToken);

        bool isInstalled = !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath);
        bool hasFullPackage = latestGame?.Pkg?.Packs?.Any(x => !string.IsNullOrWhiteSpace(x.Url)) == true;
        bool hasDeltaPackage = latestGame?.Patch?.Patches?.Any(x => !string.IsNullOrWhiteSpace(x.Url)) == true;
        bool canInstall = !isInstalled && hasFullPackage;
        bool canUpdate = isInstalled
                         && !string.IsNullOrWhiteSpace(localVersion)
                         && !string.IsNullOrWhiteSpace(latestGame?.Version)
                         && !string.Equals(localVersion, latestGame.Version, StringComparison.OrdinalIgnoreCase)
                         && (hasFullPackage || hasDeltaPackage);
        bool canRepair = isInstalled && !string.IsNullOrWhiteSpace(latestGame?.Pkg?.FilePath);

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
            CanPredownload = false,
            CanRepair = canRepair,
            InstallPath = installPath,
            ExecutablePath = executablePath,
            LocalVersion = localVersion,
            LatestVersion = latestGame?.Version,
            LastRefreshed = DateTimeOffset.Now,
        };

        snapshot.LaunchSettings = BslLaunchSettingsHelper.CreateGenericSnapshot(
            GameKey,
            DisplayName,
            installPath,
            executablePath);

        if (!isInstalled)
        {
            snapshot.StatusText = "未安装";
            snapshot.HintText = canInstall
                ? $"可直接下载完整客户端 {snapshot.LatestVersion ?? "未知版本"}"
                : "暂时无法获取游戏资源清单，请稍后重试。";
        }
        else if (canUpdate)
        {
            snapshot.StatusText = "可更新";
            snapshot.HintText = hasDeltaPackage
                ? $"本地 {snapshot.LocalVersion}，最新 {snapshot.LatestVersion}，将优先尝试增量更新"
                : $"本地 {snapshot.LocalVersion}，最新 {snapshot.LatestVersion}";
        }
        else
        {
            snapshot.StatusText = "已安装";
            snapshot.HintText = $"当前版本 {snapshot.LocalVersion ?? "未知"}";
        }

        if (hasDeltaPackage)
        {
            long deltaBytes = latestGame!.Patch!.Patches!.Sum(x => ParseLong(x.PackageSize));
            snapshot.Warnings.Add($"检测到官方增量补丁入口，更新时优先尝试差分包，约 {BslDownloadHelper.FormatBytes(deltaBytes)}。");
        }

        if (!string.IsNullOrWhiteSpace(latestGame?.Pkg?.FilePath))
        {
            snapshot.Warnings.Add("当前使用官服资源直连，不依赖官方启动器。");
        }

        if (hasFullPackage)
        {
            long totalBytes = latestGame!.Pkg!.Packs!.Sum(x => ParseLong(x.PackageSize));
            snapshot.Warnings.Add($"完整包约 {BslDownloadHelper.FormatBytes(totalBytes)}。");
        }

        string? predownloadUnavailableWarning = GetPredownloadUnavailableWarning();
        if (!string.IsNullOrWhiteSpace(predownloadUnavailableWarning))
        {
            snapshot.Warnings.Add(predownloadUnavailableWarning);
        }

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

    public async Task<BslGamePackageManifest> GetPackageManifestAsync(CancellationToken cancellationToken = default)
    {
        string? localVersion = ReadLocalVersion(FindInstallPath());
        HgLatestGameResponse? latest = await TryGetLatestGameAsync(localVersion, cancellationToken);
        if (latest is null)
        {
            throw new InvalidOperationException("Unable to load Hypergryph package manifest.");
        }

        return new BslGamePackageManifest
        {
            GameKey = GameKey,
            DisplayName = DisplayName,
            LatestVersion = latest.Version,
            LatestPackageGroups = BuildPackageGroups(latest),
            PredownloadVersion = null,
            PredownloadPackageGroups = [],
        };
    }

    public Task<BslGameCacheSnapshot> GetCacheSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? installPath = FindInstallPath();
        string? cachePath = string.IsNullOrWhiteSpace(installPath)
            ? null
            : Path.Combine(installPath, DownloadCacheFolderName);
        bool hasCache = Directory.Exists(cachePath);

        return Task.FromResult(new BslGameCacheSnapshot
        {
            GameKey = GameKey,
            DisplayName = DisplayName,
            HasPredownloadCache = hasCache,
            PredownloadVersion = hasCache ? ReadDownloadCacheVersion(cachePath!) : null,
            PredownloadCachePath = hasCache ? cachePath : null,
            PredownloadCacheSize = hasCache ? GetDirectorySize(cachePath) : 0,
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

        string cachePath = Path.Combine(installPath, DownloadCacheFolderName);
        if (!Directory.Exists(cachePath))
        {
            return Task.FromResult(true);
        }

        return Task.FromResult(BslDownloadHelper.TryDeletePaths(cachePath));
    }

    public async Task<IReadOnlyList<BslBannerEntry>> GetBannersAsync(CancellationToken cancellationToken = default)
    {
        HgBatchResponse? body = await TryGetNewsBatchAsync(cancellationToken);
        List<BslBannerEntry> list = [];

        foreach (HgBanner banner in body?.ProxyRsps?.FirstOrDefault(x => x.Kind == "get_banner")?.GetBannerRsp?.Banners ?? [])
        {
            if (string.IsNullOrWhiteSpace(banner.Url))
            {
                continue;
            }

            list.Add(new BslBannerEntry
            {
                Title = DisplayName,
                ImageUrl = banner.Url,
                Link = banner.JumpUrl ?? _homePage,
            });
        }

        return list;
    }

    public async Task<IReadOnlyList<BslNoticeEntry>> GetNoticesAsync(CancellationToken cancellationToken = default)
    {
        HgBatchResponse? body = await TryGetNewsBatchAsync(cancellationToken);
        List<BslNoticeEntry> list = [];

        foreach (HgAnnouncementTab tab in body?.ProxyRsps?.FirstOrDefault(x => x.Kind == "get_announcement")?.GetAnnouncementRsp?.Tabs ?? [])
        {
            foreach (HgAnnouncement item in tab.Announcements ?? [])
            {
                string title = item.Content ?? string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                list.Add(new BslNoticeEntry
                {
                    Category = string.IsNullOrWhiteSpace(tab.TabName) ? "公告" : tab.TabName,
                    Title = title,
                    DateText = FormatTimestamp(item.StartTs),
                    Link = item.JumpUrl ?? _homePage,
                });
            }
        }

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
                return await ImportAsync(request.ActionType);
            case BslGameActionType.Launch:
                return await LaunchAsync(request.InstallPath, request.ActionType, cancellationToken);
            case BslGameActionType.Install:
                return await InstallOrUpdateAsync(request, forceVerifyAfterExtract: true, cancellationToken);
            case BslGameActionType.Update:
                return await InstallOrUpdateAsync(request, forceVerifyAfterExtract: true, cancellationToken);
            case BslGameActionType.Repair:
                return await RepairAsync(request, cancellationToken);
            case BslGameActionType.Predownload:
                return Failed("预下载暂不可用", GetPredownloadUnavailableWarning() ?? "当前未接入可用的预下载链路。", request.ActionType);
            default:
                return Failed("暂未接入", "该动作将在后续阶段接入。", request.ActionType);
        }
    }

    private string? GetPredownloadUnavailableWarning()
    {
        return GameKey switch
        {
            "arknights" => "官方 PC 启动器当前未提供可复用的预下载直连接口，BSL 第一版暂不支持预下载。",
            "arknights-endfield" => "官方存在预下载活动，但当前未发现稳定可复用的直连接口，BSL 第一版暂不支持预下载。",
            _ => null,
        };
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
                Path.Combine(installPath ?? string.Empty, "config.ini")))
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
            _logger.LogWarning(ex, "Uninstall Hypergryph game failed: {GameKey}", GameKey);
            return Failed(BslUninstallHelper.FailedStatus, ex.Message, request.ActionType, normalizedInstallPath);
        }
    }

    private async Task<BslBackendTaskItem> ImportAsync(BslGameActionType actionType)
    {
        string? folder = await FileDialogHelper.PickFolderAsync(Starward.Features.ViewHost.MainWindow.Current.Content.XamlRoot);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return Failed("已取消导入", null, actionType);
        }

        if (!TryNormalizeImportPath(folder, out string? installPath, out string? failureMessage))
        {
            return Failed("导入失败", failureMessage, actionType, folder);
        }

        SaveInstallPath(installPath!);
        return Success("导入完成", installPath, actionType, "已校验并保存游戏安装目录。");
    }

    private async Task<BslBackendTaskItem> LaunchAsync(string? installPath, BslGameActionType actionType, CancellationToken cancellationToken)
    {
        BslGameLaunchSettingsSnapshot launchSettings = await GetLaunchSettingsAsync(cancellationToken);
        string? resolvedInstallPath = installPath ?? launchSettings.InstallPath ?? FindInstallPath();
        string? exe = launchSettings.EffectiveExecutablePath ?? ResolveExecutablePath(resolvedInstallPath);
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            return Failed("启动失败", "未找到游戏可执行文件。", actionType);
        }

        try
        {
            Process.Start(BslLaunchSettingsHelper.CreateProcessStartInfo(exe, launchSettings.LaunchArgument));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Launch Hypergryph game failed: {GameKey}", GameKey);
            return Failed("启动失败", ex.Message, actionType);
        }

        return Success("已启动", resolvedInstallPath, actionType, Path.GetFileName(exe));
    }

    private async Task<BslBackendTaskItem> InstallOrUpdateAsync(
        BslQueuedActionRequest request,
        bool forceVerifyAfterExtract,
        CancellationToken cancellationToken)
    {
        string installPath = ResolveInstallRoot(request.InstallPath);
        string downloadCacheContainerRoot = Path.Combine(installPath, DownloadCacheFolderName);
        string? localVersion = ReadLocalVersion(installPath);
        HgLatestGameResponse? latest = await TryGetLatestGameAsync(localVersion, cancellationToken);
        if (latest is null)
        {
            return Failed("获取资源失败", "未获取到游戏版本信息。", request.ActionType, installPath);
        }

        Directory.CreateDirectory(installPath);
        SaveInstallPath(installPath);

        BslBackendTaskItem progressItem = new()
        {
            GameKey = GameKey,
            DisplayName = DisplayName,
            ActionType = request.ActionType,
            State = BslBackendTaskState.Running,
            InstallState = GameInstallState.Waiting,
            StatusText = "正在准备下载",
            DetailText = $"版本 {latest.Version ?? "未知"}",
            InstallPath = installPath,
            Progress = 0,
        };

        await ReportProgressAsync(request.ProgressCallback, progressItem);

        bool hasFullPackage = latest.Pkg?.Packs?.Any(x => !string.IsNullOrWhiteSpace(x.Url)) == true;
        bool hasDeltaPackage = request.ActionType == BslGameActionType.Update
                               && !string.IsNullOrWhiteSpace(localVersion)
                               && latest.Patch?.Patches?.Any(x => !string.IsNullOrWhiteSpace(x.Url)) == true;

        if (hasDeltaPackage)
        {
            BslDiskSpaceCheckResult deltaDiskCheck = EnsureDeltaDiskSpace(installPath, latest);
            if (!deltaDiskCheck.IsSatisfied)
            {
                if (!hasFullPackage)
                {
                    return Failed(
                        BslDownloadHelper.BuildDiskSpaceFailureStatus(request.ActionType),
                        BslDownloadHelper.BuildDiskSpaceFailureMessage(request.ActionType, deltaDiskCheck),
                        request.ActionType,
                        installPath);
                }

                hasDeltaPackage = false;
                progressItem.StatusText = "增量更新空间不足";
                progressItem.InstallState = GameInstallState.Waiting;
                progressItem.DetailText = "已切换为完整包更新";
                progressItem.Progress = 0.05d;
                await ReportProgressAsync(request.ProgressCallback, progressItem);
            }
        }

        if (hasDeltaPackage)
        {
            try
            {
                await ApplyDeltaUpdateAsync(
                    installPath,
                    latest,
                    progressItem,
                    request.ProgressCallback,
                    cancellationToken);

                if (forceVerifyAfterExtract)
                {
                    progressItem.StatusText = "正在校验游戏文件";
                    progressItem.InstallState = GameInstallState.Verifying;
                    progressItem.DetailText = "准备修复缺失或损坏文件";
                    progressItem.Progress = 0.88d;
                    await ReportProgressAsync(request.ProgressCallback, progressItem);

                    await VerifyAndRepairFilesAsync(
                        installPath,
                        latest,
                        progressItem,
                        request.ProgressCallback,
                        preferFreshManifest: true,
                        cancellationToken);
                }

                PromotePendingConfigIfExists(installPath);
                WriteLocalVersion(installPath, latest.Version);
                TryCleanupDownloadCacheRoot(downloadCacheContainerRoot);
                return Success("更新完成", installPath, request.ActionType, $"版本 {latest.Version ?? "未知"}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Hypergryph delta update failed, fallback to full package: {GameKey}", GameKey);
                TryDeleteFile(Path.Combine(installPath, "config.ini.new"));

                progressItem.StatusText = "增量更新失败";
                progressItem.InstallState = GameInstallState.Waiting;
                progressItem.DetailText = hasFullPackage ? "正在回退到完整包更新" : "当前无完整包可回退";
                progressItem.Progress = 0.1d;
                await ReportProgressAsync(request.ProgressCallback, progressItem);

                if (!hasFullPackage)
                {
                    return BuildFailureWithResidualCacheIfPresent(
                        "更新失败",
                        "增量更新失败，且当前没有可回退的完整包资源。",
                        request.ActionType,
                        installPath,
                        downloadCacheContainerRoot);
                }
            }
        }

        if (!hasFullPackage)
        {
            return Failed("获取资源失败", "未获取到可用的完整包下载信息。", request.ActionType, installPath);
        }

        BslDiskSpaceCheckResult fullPackageDiskCheck = EnsureFullPackageDiskSpace(installPath, latest);
        if (!fullPackageDiskCheck.IsSatisfied)
        {
            return Failed(
                BslDownloadHelper.BuildDiskSpaceFailureStatus(request.ActionType),
                BslDownloadHelper.BuildDiskSpaceFailureMessage(request.ActionType, fullPackageDiskCheck),
                request.ActionType,
                installPath);
        }

        try
        {
            TryDeleteFile(Path.Combine(installPath, "config.ini.new"));

            await DownloadAndExtractFullPackageAsync(
                installPath,
                latest,
                progressItem,
                request.ProgressCallback,
                cancellationToken);

            WriteLocalVersion(installPath, latest.Version);

            if (forceVerifyAfterExtract)
            {
                progressItem.StatusText = "正在校验游戏文件";
                progressItem.InstallState = GameInstallState.Verifying;
                progressItem.DetailText = "准备修复缺失或损坏文件";
                progressItem.Progress = 0.88d;
                await ReportProgressAsync(request.ProgressCallback, progressItem);

                await VerifyAndRepairFilesAsync(
                    installPath,
                    latest,
                    progressItem,
                    request.ProgressCallback,
                    preferFreshManifest: true,
                    cancellationToken);
            }

            TryCleanupDownloadCacheRoot(downloadCacheContainerRoot);
            return Success(
                request.ActionType == BslGameActionType.Update ? "更新完成" : "安装完成",
                installPath,
                request.ActionType,
                $"版本 {latest.Version ?? "未知"}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Hypergryph install/update failed: {GameKey} {ActionType}", GameKey, request.ActionType);
            TryDeleteFile(Path.Combine(installPath, "config.ini.new"));
            string status = request.ActionType == BslGameActionType.Update ? "更新失败" : "安装失败";
            return BuildFailureWithResidualCacheIfPresent(
                status,
                ex.Message,
                request.ActionType,
                installPath,
                downloadCacheContainerRoot);
        }
    }

    private async Task DownloadAndExtractFullPackageAsync(
        string installPath,
        HgLatestGameResponse latest,
        BslBackendTaskItem progressItem,
        Func<BslBackendTaskItem, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        List<HgPack> packs = latest.Pkg?.Packs?.Where(x => !string.IsNullOrWhiteSpace(x.Url)).ToList() ?? [];
        if (packs.Count == 0)
        {
            throw new InvalidOperationException("No full package download packs available.");
        }

        string downloadCacheRoot = Path.Combine(installPath, DownloadCacheFolderName, SanitizeVersion(latest.Version ?? "unknown"));
        Directory.CreateDirectory(downloadCacheRoot);

        await DownloadPackagesAsync(
            downloadCacheRoot,
            packs,
            progressItem,
            progressCallback,
            cacheStatusText: "正在检查下载缓存",
            downloadStatusText: "正在下载游戏资源",
            progressStart: 0.15d,
            progressSpan: 0.45d,
            cancellationToken);

        progressItem.StatusText = "正在解压游戏文件";
        progressItem.InstallState = GameInstallState.Decompressing;
        progressItem.DetailText = "准备解压分卷包";
        progressItem.Progress = 0.6d;
        await ReportProgressAsync(progressCallback, progressItem);

        await ExtractPackagesAsync(
            downloadCacheRoot,
            installPath,
            async (processedBytes, totalExtractBytes) =>
            {
                progressItem.StatusText = "正在解压游戏文件";
                progressItem.DetailText = $"{BslDownloadHelper.FormatBytes(processedBytes)} / {BslDownloadHelper.FormatBytes(totalExtractBytes)}";
                progressItem.Progress = BuildProgress(0.6d, 0.25d, processedBytes, totalExtractBytes);
                await ReportProgressAsync(progressCallback, progressItem);
            },
            cancellationToken);
    }

    private async Task ApplyDeltaUpdateAsync(
        string installPath,
        HgLatestGameResponse latest,
        BslBackendTaskItem progressItem,
        Func<BslBackendTaskItem, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        List<HgPack> patchPacks = latest.Patch?.Patches?.Where(x => !string.IsNullOrWhiteSpace(x.Url)).ToList() ?? [];
        if (patchPacks.Count == 0)
        {
            throw new InvalidOperationException("No delta update package available.");
        }

        string versionFolder = SanitizeVersion(latest.Version ?? "unknown");
        string deltaCacheRoot = Path.Combine(installPath, DownloadCacheFolderName, $"{versionFolder}_delta");
        string deltaExtractRoot = Path.Combine(installPath, DownloadCacheFolderName, $"{versionFolder}_delta_extract");
        Directory.CreateDirectory(deltaCacheRoot);
        TryDeleteDirectory(deltaExtractRoot);
        Directory.CreateDirectory(deltaExtractRoot);

        await DownloadPackagesAsync(
            deltaCacheRoot,
            patchPacks,
            progressItem,
            progressCallback,
            cacheStatusText: "正在检查补丁缓存",
            downloadStatusText: "正在下载增量补丁",
            progressStart: 0.1d,
            progressSpan: 0.35d,
            cancellationToken);

        try
        {
            progressItem.StatusText = "正在解压增量补丁";
            progressItem.InstallState = GameInstallState.Decompressing;
            progressItem.DetailText = "准备解压补丁包";
            progressItem.Progress = 0.45d;
            await ReportProgressAsync(progressCallback, progressItem);

            await ExtractPackagesAsync(
                deltaCacheRoot,
                deltaExtractRoot,
                async (processedBytes, totalExtractBytes) =>
                {
                    progressItem.StatusText = "正在解压增量补丁";
                    progressItem.DetailText = $"{BslDownloadHelper.FormatBytes(processedBytes)} / {BslDownloadHelper.FormatBytes(totalExtractBytes)}";
                    progressItem.Progress = BuildProgress(0.45d, 0.2d, processedBytes, totalExtractBytes);
                    await ReportProgressAsync(progressCallback, progressItem);
                },
                cancellationToken);

            await ApplyDeltaDeleteListAsync(deltaExtractRoot, installPath, cancellationToken);
            await CopyDeltaStaticFilesAsync(deltaExtractRoot, installPath, progressItem, progressCallback, cancellationToken);
            await ApplyVfsDeltaPatchAsync(
                deltaExtractRoot,
                installPath,
                latest.Patch?.V2PatchInfoUrl,
                progressItem,
                progressCallback,
                cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(deltaExtractRoot);
        }
    }

    private async Task DownloadPackagesAsync(
        string downloadCacheRoot,
        IReadOnlyList<HgPack> packs,
        BslBackendTaskItem progressItem,
        Func<BslBackendTaskItem, Task>? progressCallback,
        string cacheStatusText,
        string downloadStatusText,
        double progressStart,
        double progressSpan,
        CancellationToken cancellationToken)
    {
        long totalBytes = packs.Sum(x => ParseLong(x.PackageSize));
        long downloadedBytes = 0;
        int fileIndex = 0;

        foreach (HgPack pack in packs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fileName = Path.GetFileName(new Uri(pack.Url!).LocalPath);
            string filePath = Path.Combine(downloadCacheRoot, fileName);
            long expectedSize = ParseLong(pack.PackageSize);

            if (File.Exists(filePath) && await VerifyFileAsync(filePath, pack.Md5, expectedSize, cancellationToken))
            {
                downloadedBytes += expectedSize;
                fileIndex++;
                progressItem.StatusText = cacheStatusText;
                progressItem.InstallState = GameInstallState.Downloading;
                progressItem.DetailText = $"{fileIndex} / {packs.Count}  已复用 {fileName}";
                progressItem.Progress = BuildProgress(progressStart, progressSpan, downloadedBytes, totalBytes);
                await ReportProgressAsync(progressCallback, progressItem);
                continue;
            }

            progressItem.StatusText = downloadStatusText;
            progressItem.InstallState = GameInstallState.Downloading;
            progressItem.DetailText = fileName;
            progressItem.Progress = BuildProgress(progressStart, progressSpan, downloadedBytes, totalBytes);
            await ReportProgressAsync(progressCallback, progressItem);

            await DownloadWithResumeAsync(
                pack.Url!,
                filePath,
                expectedSize,
                async currentBytes =>
                {
                    long currentTotal = downloadedBytes + currentBytes;
                    progressItem.StatusText = downloadStatusText;
                    progressItem.InstallState = GameInstallState.Downloading;
                    progressItem.DetailText = totalBytes > 0
                        ? $"{BslDownloadHelper.FormatBytes(currentTotal)} / {BslDownloadHelper.FormatBytes(totalBytes)}  {fileName}"
                        : fileName;
                    progressItem.Progress = BuildProgress(progressStart, progressSpan, currentTotal, totalBytes);
                    await ReportProgressAsync(progressCallback, progressItem);
                },
                cancellationToken);

            if (!await VerifyFileAsync(filePath, pack.Md5, expectedSize, cancellationToken))
            {
                TryDeleteFile(filePath);
                throw new InvalidOperationException($"Downloaded package verification failed: {fileName}");
            }

            downloadedBytes += expectedSize;
            fileIndex++;
        }
    }

    private async Task ApplyDeltaDeleteListAsync(string deltaExtractRoot, string installPath, CancellationToken cancellationToken)
    {
        string deleteListPath = Path.Combine(deltaExtractRoot, "delete_files.txt");
        if (!File.Exists(deleteListPath))
        {
            return;
        }

        string[] lines = await File.ReadAllLinesAsync(deleteListPath, cancellationToken);
        foreach (string line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relativePath = line.Trim();
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            string targetPath = ResolveSafeSubPath(installPath, relativePath);
            if (File.Exists(targetPath))
            {
                TryDeleteFile(targetPath);
            }
            else if (Directory.Exists(targetPath))
            {
                TryDeleteDirectory(targetPath);
            }
        }
    }

    private async Task CopyDeltaStaticFilesAsync(
        string deltaExtractRoot,
        string installPath,
        BslBackendTaskItem progressItem,
        Func<BslBackendTaskItem, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        List<string> staticFiles = Directory.GetFiles(deltaExtractRoot, "*", SearchOption.AllDirectories)
            .Where(path => !ShouldSkipDeltaStaticFile(Path.GetRelativePath(deltaExtractRoot, path)))
            .ToList();

        long totalBytes = staticFiles.Sum(path => new FileInfo(path).Length);
        long copiedBytes = 0;

        foreach (string sourcePath in staticFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relativePath = NormalizeRelativePath(Path.GetRelativePath(deltaExtractRoot, sourcePath));
            string targetPath = relativePath.Equals("config.ini", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(installPath, "config.ini.new")
                : ResolveSafeSubPath(installPath, relativePath);

            string? targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            TryDeleteFile(targetPath);
            File.Copy(sourcePath, targetPath, true);

            copiedBytes += new FileInfo(sourcePath).Length;
            progressItem.StatusText = "正在应用补丁文件";
            progressItem.InstallState = GameInstallState.Merging;
            progressItem.DetailText = totalBytes > 0
                ? $"{BslDownloadHelper.FormatBytes(copiedBytes)} / {BslDownloadHelper.FormatBytes(totalBytes)}  {Path.GetFileName(sourcePath)}"
                : Path.GetFileName(sourcePath);
            progressItem.Progress = BuildProgress(0.65d, 0.08d, copiedBytes, totalBytes);
            await ReportProgressAsync(progressCallback, progressItem);
        }
    }

    private async Task ApplyVfsDeltaPatchAsync(
        string deltaExtractRoot,
        string installPath,
        string? patchManifestUrl,
        BslBackendTaskItem progressItem,
        Func<BslBackendTaskItem, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        HgPatchManifest? patchManifest = await LoadDeltaPatchManifestAsync(deltaExtractRoot, patchManifestUrl, cancellationToken);
        if (patchManifest?.Files?.Count is not > 0)
        {
            progressItem.StatusText = "增量更新已应用";
            progressItem.InstallState = GameInstallState.Merging;
            progressItem.DetailText = "未检测到 VFS 补丁，已按静态文件覆盖完成。";
            progressItem.Progress = 0.85d;
            await ReportProgressAsync(progressCallback, progressItem);
            return;
        }

        string vfsBasePath = ResolveSafeSubPath(
            installPath,
            string.IsNullOrWhiteSpace(patchManifest.VfsBasePath)
                ? "Hg_Data/StreamingAssets/VFS"
                : patchManifest.VfsBasePath);
        Directory.CreateDirectory(vfsBasePath);

        Dictionary<string, string> extractFileMap = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(deltaExtractRoot, "*", SearchOption.AllDirectories))
        {
            extractFileMap.TryAdd(Path.GetFileName(file), file);
        }

        long totalPatchBytes = patchManifest.Files.Sum(x => Math.Max(x.Size, 1));
        long patchedBytes = 0;

        foreach (HgPatchFile fileNode in patchManifest.Files.Where(x => !string.IsNullOrWhiteSpace(x.Name)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string targetFilePath = ResolveSafeSubPath(vfsBasePath, fileNode.Name!);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);

            if (!string.IsNullOrWhiteSpace(fileNode.LocalPath))
            {
                string sourceFilePath = ResolveExtractedDeltaFilePath(deltaExtractRoot, fileNode.LocalPath, extractFileMap)
                    ?? throw new FileNotFoundException($"Delta file not found: {fileNode.LocalPath}");

                TryDeleteFile(targetFilePath);
                File.Copy(sourceFilePath, targetFilePath, true);
            }
            else if (fileNode.Patches?.Count > 0)
            {
                HgPatchNode patchNode = fileNode.Patches[0];
                string baseFilePath = ResolveSafeSubPath(vfsBasePath, patchNode.BaseFile ?? fileNode.Name!);
                string diffFilePath = ResolveExtractedDeltaFilePath(deltaExtractRoot, patchNode.PatchPath, extractFileMap)
                    ?? throw new FileNotFoundException($"Delta patch file not found: {patchNode.PatchPath}");

                if (!File.Exists(baseFilePath))
                {
                    throw new FileNotFoundException($"Base file not found for delta patch: {patchNode.BaseFile}");
                }

                if (new FileInfo(diffFilePath).Length == 0)
                {
                    if (!string.Equals(baseFilePath, targetFilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDeleteFile(targetFilePath);
                        File.Copy(baseFilePath, targetFilePath, true);
                    }
                }
                else
                {
                    string tempOutputPath = $"{targetFilePath}.tmp";
                    TryDeleteFile(tempOutputPath);

                    HDiffPatch patcher = new();
                    patcher.Initialize(diffFilePath);
                    patcher.Patch(baseFilePath, tempOutputPath, true, cancellationToken);

                    TryDeleteFile(targetFilePath);
                    File.Move(tempOutputPath, targetFilePath, true);
                }
            }
            else
            {
                continue;
            }

            if (!await VerifyFileAsync(targetFilePath, fileNode.Md5, fileNode.Size, cancellationToken))
            {
                throw new InvalidOperationException($"Delta output verification failed: {fileNode.Name}");
            }

            patchedBytes += Math.Max(fileNode.Size, 1);
            progressItem.StatusText = "正在应用 VFS 补丁";
            progressItem.InstallState = GameInstallState.Merging;
            progressItem.DetailText = $"{BslDownloadHelper.FormatBytes(patchedBytes)} / {BslDownloadHelper.FormatBytes(totalPatchBytes)}  {Path.GetFileName(targetFilePath)}";
            progressItem.Progress = BuildProgress(0.73d, 0.12d, patchedBytes, totalPatchBytes);
            await ReportProgressAsync(progressCallback, progressItem);
        }
    }

    private async Task<HgPatchManifest?> LoadDeltaPatchManifestAsync(
        string deltaExtractRoot,
        string? patchManifestUrl,
        CancellationToken cancellationToken)
    {
        string localManifestPath = Path.Combine(deltaExtractRoot, "patch.json");
        if (File.Exists(localManifestPath) && new FileInfo(localManifestPath).Length > 0)
        {
            try
            {
                await using FileStream stream = new(
                    localManifestPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 64,
                    useAsync: true);
                HgPatchManifest? localManifest = await JsonSerializer.DeserializeAsync<HgPatchManifest>(stream, _jsonOptions, cancellationToken);
                if (localManifest is not null)
                {
                    return localManifest;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Parse Hypergryph local patch manifest failed: {GameKey}", GameKey);
            }
        }

        if (string.IsNullOrWhiteSpace(patchManifestUrl))
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(
                patchManifestUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<HgPatchManifest>(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fetch Hypergryph remote patch manifest failed: {GameKey}", GameKey);
            return null;
        }
    }

    private async Task<BslBackendTaskItem> RepairAsync(BslQueuedActionRequest request, CancellationToken cancellationToken)
    {
        string installPath = request.InstallPath ?? FindInstallPath() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            return Failed("修复失败", "未找到游戏安装目录。", request.ActionType, installPath);
        }

        string? localVersion = ReadLocalVersion(installPath);
        HgLatestGameResponse? latest = await TryGetLatestGameAsync(localVersion, cancellationToken);
        if (latest is null || string.IsNullOrWhiteSpace(latest.Pkg?.FilePath))
        {
            return Failed("修复失败", "未获取到修复清单。", request.ActionType, installPath);
        }

        BslBackendTaskItem progressItem = new()
        {
            GameKey = GameKey,
            DisplayName = DisplayName,
            ActionType = request.ActionType,
            State = BslBackendTaskState.Running,
            InstallState = GameInstallState.Verifying,
            StatusText = "正在校验游戏文件",
            DetailText = "准备修复缺失或损坏文件",
            InstallPath = installPath,
            Progress = 0,
        };

        await ReportProgressAsync(request.ProgressCallback, progressItem);
        await VerifyAndRepairFilesAsync(
            installPath,
            latest,
            progressItem,
            request.ProgressCallback,
            preferFreshManifest: true,
            cancellationToken);

        return Success("修复完成", installPath, request.ActionType, $"版本 {latest.Version ?? localVersion ?? "未知"}");
    }

    private async Task VerifyAndRepairFilesAsync(
        string installPath,
        HgLatestGameResponse latest,
        BslBackendTaskItem progressItem,
        Func<BslBackendTaskItem, Task>? progressCallback,
        bool preferFreshManifest,
        CancellationToken cancellationToken)
    {
        string manifestBaseUrl = latest.Pkg?.FilePath?.TrimEnd('/') ?? throw new InvalidOperationException("Missing file manifest path.");
        List<HgManifestNode> manifestNodes = await LoadManifestAsync(installPath, manifestBaseUrl, preferFreshManifest, cancellationToken);
        if (manifestNodes.Count == 0)
        {
            progressItem.Progress = 1d;
            progressItem.StatusText = "校验完成";
            progressItem.InstallState = GameInstallState.Finish;
            progressItem.DetailText = "未发现需要校验的文件。";
            await ReportProgressAsync(progressCallback, progressItem);
            return;
        }

        long totalVerifyBytes = manifestNodes.Sum(x => x.Size);
        long verifiedBytes = 0;
        List<HgManifestNode> brokenFiles = [];

        foreach (HgManifestNode node in manifestNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string targetPath = ResolveSafeSubPath(installPath, node.Path!);
            bool ok = await VerifyFileAsync(targetPath, node.Md5, node.Size, cancellationToken);
            if (!ok)
            {
                brokenFiles.Add(node);
            }

            verifiedBytes += node.Size;
            progressItem.StatusText = "正在校验游戏文件";
            progressItem.InstallState = GameInstallState.Verifying;
            progressItem.DetailText = $"{brokenFiles.Count} 个异常文件  {BslDownloadHelper.FormatBytes(verifiedBytes)} / {BslDownloadHelper.FormatBytes(totalVerifyBytes)}";
            progressItem.Progress = BuildProgress(0.88d, 0.08d, verifiedBytes, totalVerifyBytes);
            await ReportProgressAsync(progressCallback, progressItem);
        }

        if (brokenFiles.Count == 0)
        {
            progressItem.Progress = 1d;
            progressItem.StatusText = "校验完成";
            progressItem.DetailText = "未发现损坏文件。";
            await ReportProgressAsync(progressCallback, progressItem);
            return;
        }

        long brokenTotalBytes = brokenFiles.Sum(x => x.Size);
        long repairedBytes = 0;

        foreach (HgManifestNode node in brokenFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relativePath = NormalizeRelativePath(node.Path!);
            string url = $"{manifestBaseUrl}/{relativePath}";
            string targetPath = ResolveSafeSubPath(installPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            await DownloadWithResumeAsync(
                url,
                targetPath,
                node.Size,
                async currentBytes =>
                {
                    long currentTotal = repairedBytes + currentBytes;
                    progressItem.StatusText = "正在修复游戏文件";
                    progressItem.InstallState = GameInstallState.Downloading;
                    progressItem.DetailText = $"{BslDownloadHelper.FormatBytes(currentTotal)} / {BslDownloadHelper.FormatBytes(brokenTotalBytes)}  {Path.GetFileName(targetPath)}";
                    progressItem.Progress = BuildProgress(0.96d, 0.04d, currentTotal, brokenTotalBytes);
                    await ReportProgressAsync(progressCallback, progressItem);
                },
                cancellationToken);

            if (!await VerifyFileAsync(targetPath, node.Md5, node.Size, cancellationToken))
            {
                throw new InvalidOperationException($"Repaired file verification failed: {node.Path}");
            }

            repairedBytes += node.Size;
        }

        progressItem.Progress = 1d;
        progressItem.StatusText = "修复完成";
        progressItem.InstallState = GameInstallState.Finish;
        progressItem.DetailText = $"已修复 {brokenFiles.Count} 个文件。";
        await ReportProgressAsync(progressCallback, progressItem);
    }

    private async Task<List<HgManifestNode>> LoadManifestAsync(
        string installPath,
        string manifestBaseUrl,
        bool preferFreshManifest,
        CancellationToken cancellationToken)
    {
        byte[] encryptedManifest;
        string localManifestPath = Path.Combine(installPath, ManifestCacheFileName);

        if (!preferFreshManifest && File.Exists(localManifestPath))
        {
            encryptedManifest = await File.ReadAllBytesAsync(localManifestPath, cancellationToken);
        }
        else
        {
            encryptedManifest = await _httpClient.GetByteArrayAsync($"{manifestBaseUrl}/{ManifestCacheFileName}", cancellationToken);
            await File.WriteAllBytesAsync(localManifestPath, encryptedManifest, cancellationToken);
        }

        string content = DecryptBytesToString(encryptedManifest);
        List<HgManifestNode> nodes = [];
        using StringReader reader = new(content);
        while (await reader.ReadLineAsync() is string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            HgManifestNode? node = JsonSerializer.Deserialize<HgManifestNode>(line, _jsonOptions);
            if (node is null || string.IsNullOrWhiteSpace(node.Path))
            {
                continue;
            }

            if (string.Equals(node.Path, "config.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            nodes.Add(node);
        }

        return nodes;
    }

    private async Task ExtractPackagesAsync(
        string sourceDir,
        string destinationDir,
        Func<long, long, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        List<string> partFiles = Directory.GetFiles(sourceDir)
            .Where(f => f.EndsWith(".zip.001", StringComparison.OrdinalIgnoreCase)
                        || (Path.GetExtension(f).Length == 4
                            && char.IsDigit(Path.GetExtension(f)[1])
                            && f.Contains(".zip.", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (partFiles.Count == 0)
        {
            string? singleZip = Directory.GetFiles(sourceDir, "*.zip").FirstOrDefault();
            if (singleZip is not null)
            {
                partFiles.Add(singleZip);
            }
        }

        if (partFiles.Count == 0)
        {
            throw new FileNotFoundException("No archive package found.");
        }

        Directory.CreateDirectory(destinationDir);

        using MultiVolumeReadStream stream = new(partFiles);
        using IArchive archive = SevenZipArchive.OpenArchive(stream);
        long totalUncompressedBytes = archive.Entries.Where(x => !x.IsDirectory).Sum(x => x.Size);
        long processedBytes = 0;

        foreach (IArchiveEntry entry in archive.Entries.Where(x => !x.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string key = NormalizeRelativePath(entry.Key ?? string.Empty);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            string targetPath = ResolveSafeSubPath(destinationDir, key);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.WriteToFile(targetPath, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true,
            });

            processedBytes += entry.Size;
            if (progressCallback is not null)
            {
                await progressCallback(processedBytes, totalUncompressedBytes);
            }
        }
    }

    private async Task DownloadWithResumeAsync(
        string url,
        string destinationPath,
        long expectedSize,
        Func<long, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{destinationPath}.part";
        long totalReported = 0;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                long existingLength = 0;
                if (File.Exists(tempPath))
                {
                    existingLength = new FileInfo(tempPath).Length;
                    if (expectedSize > 0 && existingLength > expectedSize)
                    {
                        TryDeleteFile(tempPath);
                        existingLength = 0;
                    }
                }

                if (expectedSize > 0 && existingLength == expectedSize)
                {
                    if (File.Exists(destinationPath))
                    {
                        TryDeleteFile(destinationPath);
                    }

                    File.Move(tempPath, destinationPath, true);
                    if (progressCallback is not null)
                    {
                        await progressCallback(expectedSize);
                    }

                    return;
                }

                using HttpRequestMessage request = new(HttpMethod.Get, url);
                if (existingLength > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(existingLength, null);
                }

                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (existingLength > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    TryDeleteFile(tempPath);
                    existingLength = 0;
                    totalReported = 0;
                }

                response.EnsureSuccessStatusCode();

                await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using FileStream output = new(
                    tempPath,
                    existingLength > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 64,
                    useAsync: true);

                byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 64);
                try
                {
                    if (progressCallback is not null && existingLength != totalReported)
                    {
                        totalReported = existingLength;
                        await progressCallback(totalReported);
                    }

                    int bytesRead;
                    while ((bytesRead = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        totalReported += bytesRead;
                        if (progressCallback is not null)
                        {
                            await progressCallback(totalReported);
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await output.FlushAsync(cancellationToken);
                if (expectedSize > 0 && output.Length != expectedSize)
                {
                    throw new InvalidOperationException($"Size mismatch. Expected {expectedSize}, got {output.Length}");
                }

                if (File.Exists(destinationPath))
                {
                    TryDeleteFile(destinationPath);
                }

                File.Move(tempPath, destinationPath, true);
                return;
            }
            catch when (attempt < 3)
            {
                await Task.Delay(1000, cancellationToken);
            }
        }

        throw new InvalidOperationException($"Download failed after retries: {url}");
    }

    private string ResolveInstallRoot(string? requestPath)
    {
        string? installPath = requestPath ?? FindInstallPath();
        if (!string.IsNullOrWhiteSpace(installPath))
        {
            return Path.GetFullPath(installPath);
        }

        string? defaultRoot = AppConfig.DefaultGameInstallationPath;
        if (!string.IsNullOrWhiteSpace(defaultRoot))
        {
            return Path.Combine(defaultRoot, _launcherFolderName);
        }

        return Path.Combine(AppConfig.CacheFolder, "Games", _launcherFolderName);
    }

    private string? FindInstallPath()
    {
        string? saved = BslBackendSetting.GetInstallPath(GameKey);
        if (TryNormalizeInstallPath(saved, out string? normalizedSaved))
        {
            SaveInstallPath(normalizedSaved!);
            return normalizedSaved;
        }

        string? registry = Registry.GetValue($@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{_registryKeyName}", "InstallPath", null) as string
                           ?? Registry.GetValue($@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{_registryKeyName}", "InstallPath", null) as string
                           ?? Registry.GetValue($@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\{_registryKeyName}", "InstallPath", null) as string;
        if (TryNormalizeInstallPath(registry, out string? normalizedRegistry))
        {
            SaveInstallPath(normalizedRegistry!);
            return normalizedRegistry;
        }

        string? defaultRoot = AppConfig.DefaultGameInstallationPath;
        if (!string.IsNullOrWhiteSpace(defaultRoot))
        {
            string candidate = Path.Combine(defaultRoot, _launcherFolderName);
            if (TryNormalizeInstallPath(candidate, out string? normalizedCandidate))
            {
                SaveInstallPath(normalizedCandidate!);
                return normalizedCandidate;
            }
        }

        return saved;
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

    private string? ResolveExecutablePath(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return null;
        }

        string direct = Path.Combine(installPath, _executableName);
        if (File.Exists(direct))
        {
            return direct;
        }

        return Directory.EnumerateFiles(installPath, _executableName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private bool TryNormalizeImportPath(string? path, out string? normalizedPath, out string? failureMessage)
    {
        if (!TryNormalizeInstallPath(path, out normalizedPath))
        {
            failureMessage = $"所选目录中未找到 {DisplayName} 的可执行文件或版本配置，请选择游戏安装根目录。";
            return false;
        }

        failureMessage = null;
        return true;
    }

    private bool TryNormalizeInstallPath(string? path, out string? normalizedPath)
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

            if (ResolveExecutablePath(fullPath) is not null || ReadLocalVersion(fullPath) is not null)
            {
                normalizedPath = fullPath;
                return true;
            }

            string nestedRoot = Path.Combine(fullPath, _launcherFolderName);
            if (Directory.Exists(nestedRoot)
                && (ResolveExecutablePath(nestedRoot) is not null || ReadLocalVersion(nestedRoot) is not null))
            {
                normalizedPath = nestedRoot;
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

    private string? ReadLocalVersion(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return null;
        }

        try
        {
            string configFile = Path.Combine(installPath, "config.ini");
            if (!File.Exists(configFile))
            {
                return null;
            }

            string content = DecryptFileToString(configFile);
            using StringReader reader = new(content);
            while (reader.ReadLine() is string line)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("version=", StringComparison.OrdinalIgnoreCase))
                {
                    string[] segments = trimmed.Split('=', 2);
                    return segments.Length == 2 ? segments[1].Trim() : null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Read Hypergryph local version failed: {GameKey}", GameKey);
        }

        return null;
    }

    private void WriteLocalVersion(string installPath, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        string configFile = Path.Combine(installPath, "config.ini");
        if (!File.Exists(configFile))
        {
            EncryptStringToFile($"version={version}{Environment.NewLine}", configFile);
            return;
        }

        string contentText = DecryptFileToString(configFile);
        if (string.IsNullOrWhiteSpace(contentText))
        {
            EncryptStringToFile($"version={version}{Environment.NewLine}", configFile);
            return;
        }

        List<string> lines = contentText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToList();

        bool replaced = false;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim().StartsWith("version=", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"version={version}";
                replaced = true;
                break;
            }
        }

        if (!replaced)
        {
            lines.Add($"version={version}");
        }

        string merged = string.Join(Environment.NewLine, lines.Where(x => x is not null));
        EncryptStringToFile(merged, configFile);
    }

    private void SaveDownloadCacheVersion(string installPath, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        string folder = Path.Combine(installPath, DownloadCacheFolderName);
        Directory.CreateDirectory(folder);
        string filePath = Path.Combine(folder, DownloadCacheVersionFileName);
        File.WriteAllText(
            filePath,
            JsonSerializer.Serialize(new DownloadCacheVersionInfo { Version = version }, _jsonOptions),
            Encoding.UTF8);
    }

    private string? ReadDownloadCacheVersion(string cachePath)
    {
        try
        {
            string filePath = Path.Combine(cachePath, DownloadCacheVersionFileName);
            if (!File.Exists(filePath))
            {
                return null;
            }

            DownloadCacheVersionInfo? info = JsonSerializer.Deserialize<DownloadCacheVersionInfo>(
                File.ReadAllText(filePath, Encoding.UTF8),
                _jsonOptions);
            return string.IsNullOrWhiteSpace(info?.Version) ? null : info.Version;
        }
        catch
        {
            return null;
        }
    }

    private async Task<HgLatestGameResponse?> TryGetLatestGameAsync(string? localVersion, CancellationToken cancellationToken)
    {
        HgBatchRequest request = new()
        {
            Seq = _seq,
            ProxyReqs =
            [
                new HgProxyRequest
                {
                    Kind = "get_latest_game",
                    GetLatestGameReq = new HgLatestGameRequest
                    {
                        AppCode = _appCode,
                        LauncherAppCode = _launcherAppCode,
                        Channel = _channel,
                        SubChannel = _subChannel,
                        Version = localVersion ?? string.Empty,
                    }
                }
            ]
        };

        try
        {
            using StringContent content = new(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync(_apiUrl, content, cancellationToken);
            response.EnsureSuccessStatusCode();
            HgBatchResponse? body = await response.Content.ReadFromJsonAsync<HgBatchResponse>(cancellationToken);
            return body?.ProxyRsps?.FirstOrDefault(x => x.Kind == "get_latest_game")?.GetLatestGameRsp;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fetch Hypergryph latest game failed: {GameKey}", GameKey);
            return null;
        }
    }

    private async Task<HgBatchResponse?> TryGetNewsBatchAsync(CancellationToken cancellationToken)
    {
        HgBatchRequest request = new()
        {
            Seq = _seq,
            ProxyReqs =
            [
                new HgProxyRequest { Kind = "get_announcement", GetAnnouncementReq = CreateCommonRequest() },
                new HgProxyRequest { Kind = "get_banner", GetBannerReq = CreateCommonRequest() },
            ]
        };

        try
        {
            using StringContent content = new(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync(_webApiUrl, content, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<HgBatchResponse>(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fetch Hypergryph banner/news failed: {GameKey}", GameKey);
            return null;
        }
    }

    private HgCommonRequest CreateCommonRequest()
    {
        return new HgCommonRequest
        {
            AppCode = _appCode,
            Language = Region == BslGameServerRegion.Global ? "en-us" : "zh-cn",
            Channel = _channel,
            SubChannel = _subChannel,
        };
    }

    private static long ParseLong(string? value)
    {
        return long.TryParse(value, out long result) ? result : 0;
    }

    private static List<BslGamePackageGroup> BuildPackageGroups(HgLatestGameResponse latest)
    {
        List<BslGamePackageGroup> groups = [];

        List<BslGamePackageItem> fullPackages = BuildPackageItems(latest.Pkg?.Packs);
        if (fullPackages.Count > 0)
        {
            groups.Add(new BslGamePackageGroup
            {
                Name = $"Full package ({fullPackages.Count})",
                Items = fullPackages,
            });
        }

        List<BslGamePackageItem> deltaPackages = BuildPackageItems(latest.Patch?.Patches);
        if (deltaPackages.Count > 0)
        {
            groups.Add(new BslGamePackageGroup
            {
                Name = $"Delta package ({deltaPackages.Count})",
                Items = deltaPackages,
            });
        }

        return groups;
    }

    private static List<BslGamePackageItem> BuildPackageItems(List<HgPack>? packs)
    {
        return (packs ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .Select((x, index) => new BslGamePackageItem
            {
                FileName = GetPackageFileName(x.Url, index),
                Url = x.Url ?? string.Empty,
                Md5 = x.Md5 ?? string.Empty,
                PackageSize = ParseLong(x.PackageSize),
            })
            .ToList();
    }

    private static string GetPackageFileName(string? url, int index)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            string fileName = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        return $"package-{index + 1}";
    }

    private BslDiskSpaceCheckResult EnsureFullPackageDiskSpace(string installPath, HgLatestGameResponse latest)
    {
        long packageBytes = latest.Pkg?.Packs?.Sum(x => ParseLong(x.PackageSize)) ?? 0;
        long declaredTotalSize = ParseLong(latest.Patch?.TotalSize);
        long extractBytes = Math.Max(packageBytes, declaredTotalSize);
        long manifestBytes = 16L * 1024 * 1024;
        BslDiskSpacePlan plan = BslDownloadHelper.CreateDiskSpacePlan(
            installPath,
            downloadBytes: packageBytes,
            stagingBytes: packageBytes,
            patchBytes: manifestBytes,
            finalWriteBytes: extractBytes,
            safetyRatio: 0.12d);
        return BslDownloadHelper.CheckDiskSpace(plan);
    }

    private BslDiskSpaceCheckResult EnsureDeltaDiskSpace(string installPath, HgLatestGameResponse latest)
    {
        long packageBytes = latest.Patch?.Patches?.Sum(x => ParseLong(x.PackageSize)) ?? 0;
        long declaredTotalSize = ParseLong(latest.Patch?.TotalSize);
        long extractBytes = Math.Max(packageBytes, declaredTotalSize > 0 ? declaredTotalSize / 2 : packageBytes);
        long patchBytes = latest.Patch?.V2PatchInfoUrl is null
            ? extractBytes / 2
            : Math.Max(extractBytes / 2, 64L * 1024 * 1024);
        BslDiskSpacePlan plan = BslDownloadHelper.CreateDiskSpacePlan(
            installPath,
            downloadBytes: packageBytes,
            stagingBytes: packageBytes + extractBytes,
            patchBytes: patchBytes,
            finalWriteBytes: 0,
            safetyRatio: 0.12d);
        return BslDownloadHelper.CheckDiskSpace(plan);
    }

    private static long GetDirectorySize(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
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

    private static string FormatTimestamp(string? unixTimestamp)
    {
        if (!long.TryParse(unixTimestamp, out long ts))
        {
            return string.Empty;
        }

        try
        {
            if (ts < 10_000_000_000)
            {
                ts *= 1000;
            }

            return DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime().ToString("MM/dd");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task ReportProgressAsync(Func<BslBackendTaskItem, Task>? callback, BslBackendTaskItem item)
    {
        item.UpdatedAt = DateTimeOffset.Now;
        if (callback is not null)
        {
            await callback(item);
        }
    }

    private static double BuildProgress(double start, double span, long current, long total)
    {
        if (total <= 0)
        {
            return start;
        }

        return start + Math.Clamp((double)current / total, 0d, 1d) * span;
    }

    private async Task<bool> VerifyFileAsync(string filePath, string? expectedMd5, long expectedSize, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        FileInfo info = new(filePath);
        if (expectedSize > 0 && info.Length != expectedSize)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(expectedMd5))
        {
            return true;
        }

        string actual = await ComputeFileMd5Async(filePath, cancellationToken);
        return string.Equals(actual, expectedMd5, StringComparison.OrdinalIgnoreCase);
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
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }

    private static void PromotePendingConfigIfExists(string installPath)
    {
        string pendingConfigPath = Path.Combine(installPath, "config.ini.new");
        if (!File.Exists(pendingConfigPath))
        {
            return;
        }

        string targetConfigPath = Path.Combine(installPath, "config.ini");
        TryDeleteFile(targetConfigPath);
        File.Move(pendingConfigPath, targetConfigPath, true);
    }

    private static bool ShouldSkipDeltaStaticFile(string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        return normalized.StartsWith("vfs_files/", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("diff_", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("/diff_", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("patch.json", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("delete_files.txt", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        return relativePath
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .TrimStart('/');
    }

    private static string ResolveSafeSubPath(string rootPath, string relativePath)
    {
        string normalizedRoot = Path.GetFullPath(rootPath);
        string safeRelativePath = relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidatePath = Path.GetFullPath(Path.Combine(normalizedRoot, safeRelativePath));
        string rootWithSeparator = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        if (!candidatePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidatePath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsafe relative path detected: {relativePath}");
        }

        return candidatePath;
    }

    private static string? ResolveExtractedDeltaFilePath(
        string deltaExtractRoot,
        string? relativePath,
        IReadOnlyDictionary<string, string> extractFileMap)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        string candidatePath = ResolveSafeSubPath(deltaExtractRoot, relativePath);
        if (File.Exists(candidatePath))
        {
            return candidatePath;
        }

        string fileName = Path.GetFileName(relativePath);
        return extractFileMap.TryGetValue(fileName, out string? foundPath) ? foundPath : null;
    }

    private static string SanitizeVersion(string version)
    {
        StringBuilder sb = new(version.Length);
        foreach (char c in version)
        {
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        }

        return sb.ToString();
    }

    private static string DecryptFileToString(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return string.Empty;
        }

        return DecryptBytesToString(File.ReadAllBytes(filePath));
    }

    private static string DecryptBytesToString(byte[] encryptedBytes)
    {
        try
        {
            if (encryptedBytes.Length == 0)
            {
                return string.Empty;
            }

            using Aes aes = Aes.Create();
            aes.Key = HypergryphCrypto.AesKey;
            aes.IV = HypergryphCrypto.AesIv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using ICryptoTransform decryptor = aes.CreateDecryptor();
            byte[] decryptedBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void EncryptStringToFile(string content, string filePath)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] input = Encoding.UTF8.GetBytes(content);
        using Aes aes = Aes.Create();
        aes.Key = HypergryphCrypto.AesKey;
        aes.IV = HypergryphCrypto.AesIv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using ICryptoTransform encryptor = aes.CreateEncryptor();
        byte[] encrypted = encryptor.TransformFinalBlock(input, 0, input.Length);
        File.WriteAllBytes(filePath, encrypted);
    }

    private void TryCleanupDownloadCacheRoot(string cacheRoot)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            return;
        }

        BslDownloadHelper.TryDeletePaths(cacheRoot);
    }

    private BslBackendTaskItem BuildFailureWithResidualCacheIfPresent(
        string status,
        string? detail,
        BslGameActionType actionType,
        string? installPath,
        string? cachePath)
    {
        string? normalizedCachePath = BslDownloadHelper.NormalizeExistingPath(cachePath);
        if (actionType is BslGameActionType.Update or BslGameActionType.Predownload
            && normalizedCachePath is not null)
        {
            return Failed(
                status,
                detail,
                actionType,
                installPath,
                normalizedCachePath,
                BslDownloadHelper.BuildResidualCacheHint(actionType, normalizedCachePath));
        }

        return Failed(status, detail, actionType, installPath);
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

    private sealed class HgBatchRequest
    {
        [JsonPropertyName("seq")]
        public string Seq { get; set; } = "5";

        [JsonPropertyName("proxy_reqs")]
        public List<HgProxyRequest> ProxyReqs { get; set; } = [];
    }

    private sealed class HgProxyRequest
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("get_latest_game_req")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HgLatestGameRequest? GetLatestGameReq { get; set; }

        [JsonPropertyName("get_banner_req")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HgCommonRequest? GetBannerReq { get; set; }

        [JsonPropertyName("get_announcement_req")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HgCommonRequest? GetAnnouncementReq { get; set; }
    }

    private sealed class HgLatestGameRequest
    {
        [JsonPropertyName("appcode")]
        public string AppCode { get; set; } = string.Empty;

        [JsonPropertyName("launcher_appcode")]
        public string LauncherAppCode { get; set; } = string.Empty;

        [JsonPropertyName("channel")]
        public string Channel { get; set; } = string.Empty;

        [JsonPropertyName("sub_channel")]
        public string SubChannel { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;
    }

    private sealed class HgCommonRequest
    {
        [JsonPropertyName("appcode")]
        public string AppCode { get; set; } = string.Empty;

        [JsonPropertyName("language")]
        public string Language { get; set; } = "zh-cn";

        [JsonPropertyName("channel")]
        public string Channel { get; set; } = string.Empty;

        [JsonPropertyName("sub_channel")]
        public string SubChannel { get; set; } = string.Empty;

        [JsonPropertyName("platform")]
        public string Platform { get; set; } = "Windows";

        [JsonPropertyName("source")]
        public string Source { get; set; } = "launcher";
    }

    private sealed class HgBatchResponse
    {
        [JsonPropertyName("proxy_rsps")]
        public List<HgProxyResponse>? ProxyRsps { get; set; }
    }

    private sealed class HgProxyResponse
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("get_latest_game_rsp")]
        public HgLatestGameResponse? GetLatestGameRsp { get; set; }

        [JsonPropertyName("get_banner_rsp")]
        public HgBannerResponse? GetBannerRsp { get; set; }

        [JsonPropertyName("get_announcement_rsp")]
        public HgAnnouncementResponse? GetAnnouncementRsp { get; set; }
    }

    private sealed class HgLatestGameResponse
    {
        [JsonPropertyName("action")]
        public int Action { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("pkg")]
        public HgPkgInfo? Pkg { get; set; }

        [JsonPropertyName("patch")]
        public HgPatchInfo? Patch { get; set; }
    }

    private sealed class HgPatchInfo
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("md5")]
        public string? Md5 { get; set; }

        [JsonPropertyName("package_size")]
        public string? PackageSize { get; set; }

        [JsonPropertyName("total_size")]
        public string? TotalSize { get; set; }

        [JsonPropertyName("patches")]
        public List<HgPack>? Patches { get; set; }

        [JsonPropertyName("v2_patch_info_url")]
        public string? V2PatchInfoUrl { get; set; }

        [JsonPropertyName("v2_patch_info_md5")]
        public string? V2PatchInfoMd5 { get; set; }
    }

    private sealed class HgPatchManifest
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("vfs_base_path")]
        public string? VfsBasePath { get; set; }

        [JsonPropertyName("files")]
        public List<HgPatchFile>? Files { get; set; }
    }

    private sealed class HgPatchFile
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("md5")]
        public string? Md5 { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("diffType")]
        public int DiffType { get; set; }

        [JsonPropertyName("local_path")]
        public string? LocalPath { get; set; }

        [JsonPropertyName("patch")]
        public List<HgPatchNode>? Patches { get; set; }
    }

    private sealed class HgPatchNode
    {
        [JsonPropertyName("base_file")]
        public string? BaseFile { get; set; }

        [JsonPropertyName("base_md5")]
        public string? BaseMd5 { get; set; }

        [JsonPropertyName("patch")]
        public string? PatchPath { get; set; }
    }

    private sealed class HgPkgInfo
    {
        [JsonPropertyName("packs")]
        public List<HgPack>? Packs { get; set; }

        [JsonPropertyName("file_path")]
        public string? FilePath { get; set; }
    }

    private sealed class HgPack
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("md5")]
        public string? Md5 { get; set; }

        [JsonPropertyName("package_size")]
        public string? PackageSize { get; set; }
    }

    private sealed class HgBannerResponse
    {
        [JsonPropertyName("banners")]
        public List<HgBanner>? Banners { get; set; }
    }

    private sealed class HgBanner
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("jump_url")]
        public string? JumpUrl { get; set; }
    }

    private sealed class HgAnnouncementResponse
    {
        [JsonPropertyName("tabs")]
        public List<HgAnnouncementTab>? Tabs { get; set; }
    }

    private sealed class HgAnnouncementTab
    {
        [JsonPropertyName("tabName")]
        public string? TabName { get; set; }

        [JsonPropertyName("announcements")]
        public List<HgAnnouncement>? Announcements { get; set; }
    }

    private sealed class HgAnnouncement
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("jump_url")]
        public string? JumpUrl { get; set; }

        [JsonPropertyName("start_ts")]
        public string? StartTs { get; set; }
    }

    private sealed class HgManifestNode
    {
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("md5")]
        public string? Md5 { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    private sealed class DownloadCacheVersionInfo
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;
    }

    private static class HypergryphCrypto
    {
        public static readonly byte[] AesKey =
        [
            0xC0, 0xF3, 0x0E, 0x1C, 0xE7, 0x63, 0xBB, 0xC2, 0x1C, 0xC3, 0x55, 0xA3, 0x43, 0x03, 0xAC, 0x50,
            0x39, 0x94, 0x44, 0xBF, 0xF6, 0x8C, 0x4A, 0x22, 0xAF, 0x39, 0x8C, 0x0A, 0x16, 0x6E, 0xE1, 0x43,
        ];

        public static readonly byte[] AesIv =
        [
            0x33, 0x46, 0x78, 0x61, 0x19, 0x27, 0x50, 0x64, 0x95, 0x01, 0x93, 0x72, 0x64, 0x60, 0x84, 0x00,
        ];
    }

    private sealed class MultiVolumeReadStream : Stream
    {
        private readonly List<long> _fileLengths;
        private readonly List<string> _filePaths;
        private readonly long _totalLength;
        private int _currentIndex;
        private FileStream? _currentStream;
        private long _position;

        public MultiVolumeReadStream(IEnumerable<string> filePaths)
        {
            _filePaths = filePaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            _fileLengths = _filePaths.Select(p => new FileInfo(p).Length).ToList();
            _totalLength = _fileLengths.Sum();
            OpenStreamAtIndex(0);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _totalLength;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalBytesRead = 0;
            while (count > 0)
            {
                if (_currentIndex >= _filePaths.Count)
                {
                    break;
                }

                if (_currentStream!.Position >= _currentStream.Length && !OpenStreamAtIndex(_currentIndex + 1))
                {
                    break;
                }

                int bytesToRead = (int)Math.Min(count, _currentStream.Length - _currentStream.Position);
                int bytesRead = _currentStream.Read(buffer, offset, bytesToRead);
                if (bytesRead == 0)
                {
                    break;
                }

                _position += bytesRead;
                offset += bytesRead;
                count -= bytesRead;
                totalBytesRead += bytesRead;
            }

            return totalBytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long targetPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _totalLength + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };

            if (targetPosition < 0 || targetPosition > _totalLength)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            _position = targetPosition;

            long accumulated = 0;
            for (int i = 0; i < _filePaths.Count; i++)
            {
                long fileLength = _fileLengths[i];
                if (targetPosition < accumulated + fileLength)
                {
                    OpenStreamAtIndex(i);
                    _currentStream!.Position = targetPosition - accumulated;
                    return _position;
                }

                accumulated += fileLength;
            }

            if (targetPosition == _totalLength)
            {
                OpenStreamAtIndex(_filePaths.Count - 1);
                _currentStream!.Position = _currentStream.Length;
                return _position;
            }

            throw new IOException("Seek failed.");
        }

        private bool OpenStreamAtIndex(int index)
        {
            if (index >= _filePaths.Count)
            {
                return false;
            }

            if (_currentIndex != index || _currentStream is null)
            {
                _currentStream?.Dispose();
                _currentIndex = index;
                _currentStream = new FileStream(_filePaths[index], FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _currentStream?.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
