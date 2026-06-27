using Microsoft.Extensions.Logging;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.GameInstall;
using Starward.Features.GameLauncher;
using Starward.Features.HoYoPlay;
using Starward.Helpers;
using Starward.RPC.GameInstall;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.BSL.Backend.Adapters;

internal sealed class MiHoYoBslAdapter : IBslGameAdapter
{
    private readonly ILogger<MiHoYoBslAdapter> _logger = AppConfig.GetLogger<MiHoYoBslAdapter>();
    private readonly HoYoPlayService _hoYoPlayService;
    private readonly GameLauncherService _gameLauncherService;
    private readonly GameInstallService _gameInstallService;
    private readonly GamePackageService _gamePackageService;
    private readonly GameId _gameId;
    private readonly string _gameKey;
    private readonly string _displayName;

    public MiHoYoBslAdapter(
        string gameKey,
        string displayName,
        GameId gameId,
        HoYoPlayService hoYoPlayService,
        GameLauncherService gameLauncherService,
        GameInstallService gameInstallService,
        GamePackageService gamePackageService)
    {
        _gameKey = gameKey;
        _displayName = displayName;
        _gameId = gameId;
        _hoYoPlayService = hoYoPlayService;
        _gameLauncherService = gameLauncherService;
        _gameInstallService = gameInstallService;
        _gamePackageService = gamePackageService;
    }

    public string GameKey => _gameKey;

    public string DisplayName => _displayName;

    public BslGameSupportLevel SupportLevel => BslGameSupportLevel.Verified;

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
        string? installPath = GameLauncherService.GetGameInstallPath(_gameId);
        bool isInstalled = !string.IsNullOrWhiteSpace(installPath);
        string? defaultExecutablePath = isInstalled ? System.IO.Path.Join(installPath, await _gameLauncherService.GetGameExeNameAsync(_gameId)) : null;
        Version? localVersion = await _gameLauncherService.GetLocalGameVersionAsync(_gameId, installPath);
        (Version? latestVersion, Version? predownloadVersion) = await _gameLauncherService.GetLatestGameVersionAsync(_gameId);
        GameInstallContext? task = _gameInstallService.GetGameInstallTask(_gameId);

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
            CanInstall = !isInstalled,
            CanUpdate = isInstalled && latestVersion is not null && localVersion is not null && latestVersion > localVersion,
            CanPredownload = isInstalled && predownloadVersion is not null && latestVersion is not null && predownloadVersion > latestVersion,
            CanRepair = isInstalled,
            InstallPath = installPath,
            ExecutablePath = defaultExecutablePath,
            LocalVersion = localVersion?.ToString(),
            LatestVersion = latestVersion?.ToString(),
            PredownloadVersion = predownloadVersion?.ToString(),
            LastRefreshed = DateTimeOffset.Now,
        };

        snapshot.LaunchSettings = BslLaunchSettingsHelper.CreateMiHoYoSnapshot(
            GameKey,
            DisplayName,
            _gameId,
            installPath,
            defaultExecutablePath);

        if (task is not null)
        {
            snapshot.IsBusy = true;
            snapshot.HasPredownloadTask = task.Operation is GameInstallOperation.Predownload;
            snapshot.PredownloadButtonState = task.Operation is GameInstallOperation.Predownload ? task.State : snapshot.PredownloadButtonState;
            snapshot.StatusText = task.State switch
            {
                GameInstallState.Downloading => "正在下载",
                GameInstallState.Decompressing => "正在解压",
                GameInstallState.Merging => "正在合并",
                GameInstallState.Verifying => "正在校验",
                GameInstallState.Paused => "任务已暂停",
                GameInstallState.Queueing => "任务排队中",
                GameInstallState.Error => "任务失败",
                _ => "任务处理中",
            };
            snapshot.HintText = $"{task.Operation} {task.Progress_DownloadFinishBytes / 1024d / 1024d / 1024d:F2}GB / {Math.Max(0.01, task.Progress_DownloadTotalBytes / 1024d / 1024d / 1024d):F2}GB";
        }
        else if (!isInstalled)
        {
            snapshot.StatusText = "未安装";
            snapshot.HintText = "可直接复用 Starward 的下载、更新、预下载与修复链路。";
        }
        else if (snapshot.CanUpdate)
        {
            snapshot.StatusText = "可更新";
            snapshot.HintText = $"本地 {snapshot.LocalVersion}，最新 {snapshot.LatestVersion}";
        }
        else if (snapshot.CanPredownload)
        {
            snapshot.StatusText = "可预下载";
            snapshot.HintText = $"预下载版本 {snapshot.PredownloadVersion}";
        }
        else
        {
            snapshot.StatusText = "已安装";
            snapshot.HintText = $"当前版本 {snapshot.LocalVersion ?? "未知"}";
        }

        snapshot.RefreshUiSemantics();
        return snapshot;
    }

    public async Task<BslGameLaunchSettingsSnapshot> GetLaunchSettingsAsync(CancellationToken cancellationToken = default)
    {
        string? installPath = GameLauncherService.GetGameInstallPath(_gameId);
        string? defaultExecutablePath = null;
        if (!string.IsNullOrWhiteSpace(installPath))
        {
            defaultExecutablePath = System.IO.Path.Join(installPath, await _gameLauncherService.GetGameExeNameAsync(_gameId));
        }

        return BslLaunchSettingsHelper.CreateMiHoYoSnapshot(
            GameKey,
            DisplayName,
            _gameId,
            installPath,
            defaultExecutablePath);
    }

    public async Task<BslLaunchSettingsSaveResult> ImportGameAsync(string? installPath, CancellationToken cancellationToken = default)
    {
        string? normalizedInstallPath = NormalizeInstallPath(installPath);
        GameLauncherService.ChangeGameInstallPath(_gameId, normalizedInstallPath);

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
            string? normalizedInstallPath = NormalizeInstallPath(update.InstallPath);
            GameLauncherService.ChangeGameInstallPath(_gameId, normalizedInstallPath);
        }

        BslLaunchSettingsHelper.SaveMiHoYoSettings(_gameId, update);
        return Task.CompletedTask;
    }

    public Task<string?> NormalizeInstallPathAsync(string? installPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NormalizeInstallPath(installPath));
    }

    public async Task<IReadOnlyList<BslBannerEntry>> GetBannersAsync(CancellationToken cancellationToken = default)
    {
        GameContent content = await _hoYoPlayService.GetGameContentAsync(_gameId, cancellationToken);
        return content.Banners?
                      .Select(x => new BslBannerEntry
                      {
                          Title = DisplayName,
                          ImageUrl = x.Image?.Url ?? string.Empty,
                          Link = string.Empty,
                      })
                      .Where(x => !string.IsNullOrWhiteSpace(x.ImageUrl))
                      .ToList()
               ?? [];
    }

    public async Task<IReadOnlyList<BslNoticeEntry>> GetNoticesAsync(CancellationToken cancellationToken = default)
    {
        GameContent content = await _hoYoPlayService.GetGameContentAsync(_gameId, cancellationToken);
        List<BslNoticeEntry> notices = [];
        foreach (GamePostGroup? group in GamePostGroup.FromGameContent(content))
        {
            foreach (GamePost? item in group.List ?? [])
            {
                notices.Add(new BslNoticeEntry
                {
                    Category = group.Header ?? "公告",
                    Title = item.Title ?? string.Empty,
                    DateText = item.Date ?? string.Empty,
                    Link = item.Link ?? string.Empty,
                });
            }
        }
        return notices;
    }

    public async Task<BslBackendTaskItem> ExecuteAsync(BslQueuedActionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ActionType == BslGameActionType.Import)
        {
            string? folder = request.InstallPath ?? await FileDialogHelper.PickFolderAsync(Starward.Features.ViewHost.MainWindow.Current.Content.XamlRoot);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return Failed("已取消导入", null, request.ActionType);
            }

            string? path = GameLauncherService.ChangeGameInstallPath(_gameId, folder);
            return path is not null
                ? Success("导入完成", path, request.ActionType, "已保存游戏安装目录。")
                : Failed("导入失败", "所选目录不存在，或无法保存安装路径。", request.ActionType);
        }

        string? installPath = request.InstallPath ?? GameLauncherService.GetGameInstallPath(_gameId) ?? AppConfig.DefaultGameInstallationPath;
        if (string.IsNullOrWhiteSpace(installPath) && request.ActionType is not BslGameActionType.Refresh)
        {
            return Failed("缺少安装路径", "请先设置安装目录或导入已有游戏目录。", request.ActionType);
        }

        if (request.ActionType == BslGameActionType.Uninstall)
        {
            return await UninstallAsync(installPath, cancellationToken);
        }

        switch (request.ActionType)
        {
            case BslGameActionType.Launch:
            {
                Process? process = await _gameLauncherService.StartGameAsync(_gameId, installPath);
                return new BslBackendTaskItem
                {
                    GameKey = GameKey,
                    DisplayName = DisplayName,
                    ActionType = request.ActionType,
                    State = process is null ? BslBackendTaskState.Failed : BslBackendTaskState.Succeeded,
                    StatusText = process is null ? "启动失败" : "已启动",
                    DetailText = process is null ? "未获取到游戏进程。" : process.ProcessName,
                    InstallPath = installPath,
                    Progress = process is null ? 0 : 1,
                };
            }
            case BslGameActionType.Install:
            {
                BslBackendTaskItem? diskFailure = await ValidateDiskSpaceAsync(request.ActionType, installPath!, cancellationToken);
                if (diskFailure is not null)
                {
                    return diskFailure;
                }

                AudioLanguage audioLanguage = await ResolveAudioLanguageAsync(request.ActionType, installPath!);
                GameInstallContext? task = await _gameInstallService.StartInstallAsync(_gameId, installPath!, audioLanguage);
                return await WaitForInstallTaskAsync(task, request.ActionType, installPath, "安装", request.ProgressCallback, cancellationToken);
            }
            case BslGameActionType.Update:
            {
                BslBackendTaskItem? diskFailure = await ValidateDiskSpaceAsync(request.ActionType, installPath!, cancellationToken);
                if (diskFailure is not null)
                {
                    return diskFailure;
                }

                AudioLanguage audioLanguage = await ResolveAudioLanguageAsync(request.ActionType, installPath!);
                GameInstallContext? task = await _gameInstallService.StartUpdateAsync(_gameId, installPath!, audioLanguage);
                return await WaitForInstallTaskAsync(task, request.ActionType, installPath, "更新", request.ProgressCallback, cancellationToken);
            }
            case BslGameActionType.Predownload:
            {
                BslBackendTaskItem? diskFailure = await ValidateDiskSpaceAsync(request.ActionType, installPath!, cancellationToken);
                if (diskFailure is not null)
                {
                    return diskFailure;
                }

                AudioLanguage audioLanguage = await ResolveAudioLanguageAsync(request.ActionType, installPath!);
                GameInstallContext? task = await _gameInstallService.StartPredownloadAsync(_gameId, installPath!, audioLanguage);
                return await WaitForInstallTaskAsync(task, request.ActionType, installPath, "预下载", request.ProgressCallback, cancellationToken);
            }
            case BslGameActionType.Repair:
            {
                AudioLanguage audioLanguage = await ResolveAudioLanguageAsync(request.ActionType, installPath!);
                GameInstallContext? task = await _gameInstallService.StartRepairAsync(_gameId, installPath!, audioLanguage);
                return await WaitForInstallTaskAsync(task, request.ActionType, installPath, "修复", request.ProgressCallback, cancellationToken);
            }
            default:
                return Success("已刷新", installPath, request.ActionType, "米哈游适配器状态已刷新。");
        }
    }

    private async Task<BslBackendTaskItem> UninstallAsync(string? installPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? defaultExecutablePath = null;
        if (!string.IsNullOrWhiteSpace(installPath))
        {
            defaultExecutablePath = Path.Join(installPath, await _gameLauncherService.GetGameExeNameAsync(_gameId));
        }

        if (!BslUninstallHelper.TryValidateInstallDirectory(
                installPath,
                out string? normalizedInstallPath,
                out string? failureMessage,
                defaultExecutablePath,
                Path.Join(installPath ?? string.Empty, "config.ini")))
        {
            return Failed(BslUninstallHelper.FailedStatus, failureMessage, BslGameActionType.Uninstall, installPath);
        }

        if (await _gameLauncherService.GetGameProcessAsync(_gameId) is not null)
        {
            return Failed(BslUninstallHelper.FailedStatus, BslUninstallHelper.GameRunningMessage, BslGameActionType.Uninstall, normalizedInstallPath);
        }

        try
        {
            bool success = await _gameInstallService.StartUninstallAsync(_gameId, normalizedInstallPath!);
            if (!success)
            {
                return Failed(BslUninstallHelper.NotStartedStatus, BslUninstallHelper.RpcUninstallNotStartedMessage, BslGameActionType.Uninstall, normalizedInstallPath);
            }

            GameLauncherService.ChangeGameInstallPath(_gameId, null);
            BslLaunchSettingsHelper.SaveMiHoYoSettings(_gameId, BslUninstallHelper.CreateResetLaunchSettingsUpdate());

            return Success(BslUninstallHelper.CompletedStatus, null, BslGameActionType.Uninstall, BslUninstallHelper.CleanupCompletedDetail);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Uninstall miHoYo game failed: {GameKey}", GameKey);
            return Failed(BslUninstallHelper.FailedStatus, ex.Message, BslGameActionType.Uninstall, normalizedInstallPath);
        }
    }

    private async Task<BslBackendTaskItem?> ValidateDiskSpaceAsync(BslGameActionType actionType, string installPath, CancellationToken cancellationToken)
    {
        if (actionType is not BslGameActionType.Install
            and not BslGameActionType.Update
            and not BslGameActionType.Predownload)
        {
            return null;
        }

        try
        {
            BslDiskSpacePlan? plan = await TryBuildDiskSpacePlanAsync(actionType, installPath, cancellationToken);
            if (plan is null)
            {
                return null;
            }

            BslDiskSpaceCheckResult result = BslDownloadHelper.CheckDiskSpace(plan);
            if (result.IsSatisfied)
            {
                return null;
            }

            return Failed(
                BslDownloadHelper.BuildDiskSpaceFailureStatus(actionType),
                BslDownloadHelper.BuildDiskSpaceFailureMessage(actionType, result),
                actionType,
                installPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Build miHoYo disk space plan failed: {GameKey} {ActionType}", GameKey, actionType);
            return Failed(
                BslDownloadHelper.BuildDiskSpaceFailureStatus(actionType),
                $"无法完成空间检查：{ex.Message}",
                actionType,
                installPath);
        }
    }

    private async Task<BslDiskSpacePlan?> TryBuildDiskSpacePlanAsync(
        BslGameActionType actionType,
        string installPath,
        CancellationToken cancellationToken)
    {
        GameConfig? config = await _hoYoPlayService.GetGameConfigAsync(_gameId, cancellationToken);
        if (config is null)
        {
            throw new InvalidOperationException("未获取到米哈游游戏配置。");
        }

        AudioLanguage audioLanguage = await ResolveAudioLanguageAsync(actionType, installPath);
        return config.DefaultDownloadMode switch
        {
            DownloadMode.DOWNLOAD_MODE_CHUNK or DownloadMode.DOWNLOAD_MODE_LDIFF
                => await BuildSophonDiskSpacePlanAsync(actionType, installPath, config, audioLanguage, cancellationToken),
            _ => await BuildPackageDiskSpacePlanAsync(actionType, installPath, audioLanguage, cancellationToken),
        };
    }

    private async Task<BslDiskSpacePlan?> BuildPackageDiskSpacePlanAsync(
        BslGameActionType actionType,
        string installPath,
        AudioLanguage audioLanguage,
        CancellationToken cancellationToken)
    {
        GamePackage package = await _hoYoPlayService.GetGamePackageAsync(_gameId, cancellationToken);
        Version? localVersion = await _gameLauncherService.GetLocalGameVersionAsync(_gameId, installPath);
        string? localVersionText = localVersion?.ToString();

        GamePackageResource? resource = actionType switch
        {
            BslGameActionType.Install => package.Main.Major,
            BslGameActionType.Update => package.Main.Patches.FirstOrDefault(x => string.Equals(x.Version, localVersionText, StringComparison.OrdinalIgnoreCase))
                                        ?? package.Main.Major,
            BslGameActionType.Predownload => package.PreDownload.Patches.FirstOrDefault(x => string.Equals(x.Version, localVersionText, StringComparison.OrdinalIgnoreCase))
                                             ?? package.PreDownload.Major,
            _ => null,
        };

        if (resource is null)
        {
            return null;
        }

        long compressedBytes = resource.GamePackages.Sum(x => x.Size);
        long uncompressedBytes = resource.GamePackages.Sum(x => x.DecompressedSize);

        foreach (AudioLanguage lang in Enum.GetValues<AudioLanguage>())
        {
            if (lang is AudioLanguage.None or AudioLanguage.All || !audioLanguage.HasFlag(lang))
            {
                continue;
            }

            if (resource.AudioPackages.FirstOrDefault(x => x.Language == lang.ToDescription()) is GamePackageFile audioPackage)
            {
                compressedBytes += audioPackage.Size;
                uncompressedBytes += audioPackage.DecompressedSize;
            }
        }

        return BslDownloadHelper.CreateDiskSpacePlan(
            installPath,
            downloadBytes: compressedBytes,
            stagingBytes: compressedBytes,
            patchBytes: actionType is BslGameActionType.Update or BslGameActionType.Predownload ? compressedBytes / 5 : 0,
            finalWriteBytes: uncompressedBytes,
            safetyRatio: 0.08d);
    }

    private async Task<BslDiskSpacePlan?> BuildSophonDiskSpacePlanAsync(
        BslGameActionType actionType,
        string installPath,
        GameConfig config,
        AudioLanguage audioLanguage,
        CancellationToken cancellationToken)
    {
        GameBranch? branch = await _hoYoPlayService.GetGameBranchAsync(_gameId, cancellationToken);
        if (branch is null)
        {
            throw new InvalidOperationException("未获取到米哈游游戏分支配置。");
        }

        string? localVersion = (await _gameLauncherService.GetLocalGameVersionAsync(_gameId, installPath))?.ToString();
        GameBranchPackage targetPackage = actionType switch
        {
            BslGameActionType.Install => branch.Main,
            BslGameActionType.Update => branch.Main,
            BslGameActionType.Predownload => branch.PreDownload ?? throw new InvalidOperationException("当前未获取到预下载分支。"),
            _ => branch.Main,
        };

        List<string> ignoreMatchingFields = GetIgnoreMatchingFields(installPath, config);
        if (!string.IsNullOrWhiteSpace(localVersion)
            && targetPackage.DiffTags.Any(x => string.Equals(x, localVersion, StringComparison.OrdinalIgnoreCase)))
        {
            GameSophonPatchBuild? patchBuild = await _hoYoPlayService.GetGameSophonPatchBuildAsync(branch, targetPackage, cancellationToken);
            if (patchBuild is not null)
            {
                return BuildPatchDiskSpacePlan(installPath, patchBuild, localVersion, audioLanguage, ignoreMatchingFields);
            }
        }

        GameSophonChunkBuild? chunkBuild = await _hoYoPlayService.GetGameSophonChunkBuildAsync(branch, targetPackage, cancellationToken);
        if (chunkBuild is null)
        {
            return null;
        }

        List<GameSophonChunkManifest> manifests = GetAvailableGameSophonChunkManifests(chunkBuild, audioLanguage, ignoreMatchingFields);
        long compressedBytes = manifests.Sum(x => x.Stats.CompressedSize);
        long uncompressedBytes = manifests.Sum(x => x.Stats.UncompressedSize);

        return BslDownloadHelper.CreateDiskSpacePlan(
            installPath,
            downloadBytes: compressedBytes,
            stagingBytes: compressedBytes / 2,
            patchBytes: actionType is BslGameActionType.Update or BslGameActionType.Predownload ? compressedBytes / 4 : 0,
            finalWriteBytes: uncompressedBytes,
            safetyRatio: 0.08d);
    }

    private static BslDiskSpacePlan? BuildPatchDiskSpacePlan(
        string installPath,
        GameSophonPatchBuild patchBuild,
        string localVersion,
        AudioLanguage audioLanguage,
        IReadOnlyCollection<string> ignoreMatchingFields)
    {
        List<GameSophonPatchManifest> manifests = GetAvailableGameSophonPatchManifests(patchBuild, audioLanguage, ignoreMatchingFields);
        long compressedBytes = 0;
        long uncompressedBytes = 0;

        foreach (GameSophonPatchManifest manifest in manifests)
        {
            if (manifest.Stats.TryGetValue(localVersion, out GameSophonManifestStats? stats))
            {
                compressedBytes += stats.CompressedSize;
                uncompressedBytes += stats.UncompressedSize;
                continue;
            }

            if (manifest.Stats.Count > 0)
            {
                GameSophonManifestStats fallback = manifest.Stats.Values
                    .OrderByDescending(x => x.CompressedSize + x.UncompressedSize)
                    .First();
                compressedBytes += fallback.CompressedSize;
                uncompressedBytes += fallback.UncompressedSize;
            }
        }

        if (compressedBytes <= 0 && uncompressedBytes <= 0)
        {
            return null;
        }

        return BslDownloadHelper.CreateDiskSpacePlan(
            installPath,
            downloadBytes: compressedBytes,
            stagingBytes: compressedBytes / 2,
            patchBytes: compressedBytes,
            finalWriteBytes: uncompressedBytes,
            safetyRatio: 0.08d);
    }

    private async Task<AudioLanguage> ResolveAudioLanguageAsync(BslGameActionType actionType, string installPath)
    {
        if (actionType == BslGameActionType.Install || string.IsNullOrWhiteSpace(installPath))
        {
            return AudioLanguage.Chinese;
        }

        AudioLanguage audioLanguage = await _gamePackageService.GetAudioLanguageAsync(_gameId, installPath);
        return audioLanguage == AudioLanguage.None ? AudioLanguage.Chinese : audioLanguage;
    }

    private static List<GameSophonChunkManifest> GetAvailableGameSophonChunkManifests(
        GameSophonChunkBuild build,
        AudioLanguage audioLanguage,
        IReadOnlyCollection<string> ignoreMatchingFields)
    {
        List<GameSophonChunkManifest> manifests = [];
        foreach (GameSophonChunkManifest manifest in build.Manifests)
        {
            if (ignoreMatchingFields.Contains(manifest.MatchingField))
            {
                continue;
            }

            if (manifest.MatchingField.Length is 5 or 10 && manifest.MatchingField.Contains('-'))
            {
                continue;
            }

            manifests.Add(manifest);
        }

        foreach (AudioLanguage lang in Enum.GetValues<AudioLanguage>())
        {
            if (lang is AudioLanguage.None or AudioLanguage.All || !audioLanguage.HasFlag(lang))
            {
                continue;
            }

            if (build.Manifests.FirstOrDefault(x => x.MatchingField == lang.ToDescription()) is GameSophonChunkManifest audioManifest)
            {
                manifests.Add(audioManifest);
            }
        }

        return manifests;
    }

    private static List<GameSophonPatchManifest> GetAvailableGameSophonPatchManifests(
        GameSophonPatchBuild build,
        AudioLanguage audioLanguage,
        IReadOnlyCollection<string> ignoreMatchingFields)
    {
        List<GameSophonPatchManifest> manifests = [];
        foreach (GameSophonPatchManifest manifest in build.Manifests)
        {
            if (ignoreMatchingFields.Contains(manifest.MatchingField))
            {
                continue;
            }

            if (manifest.MatchingField.Length is 5 or 10 && manifest.MatchingField.Contains('-'))
            {
                continue;
            }

            manifests.Add(manifest);
        }

        foreach (AudioLanguage lang in Enum.GetValues<AudioLanguage>())
        {
            if (lang is AudioLanguage.None or AudioLanguage.All || !audioLanguage.HasFlag(lang))
            {
                continue;
            }

            if (build.Manifests.FirstOrDefault(x => x.MatchingField == lang.ToDescription()) is GameSophonPatchManifest audioManifest)
            {
                manifests.Add(audioManifest);
            }
        }

        return manifests;
    }

    private static List<string> GetIgnoreMatchingFields(string installPath, GameConfig config)
    {
        List<string> ignoreMatchingFields = [];
        if (string.IsNullOrWhiteSpace(config.ResCategoryDir))
        {
            return ignoreMatchingFields;
        }

        try
        {
            string file = Path.Join(installPath, config.ResCategoryDir);
            if (!File.Exists(file))
            {
                return ignoreMatchingFields;
            }

            foreach (string line in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                IgnoreMatchingField? item = JsonSerializer.Deserialize<IgnoreMatchingField>(line);
                if (item?.IsDelete == true && !string.IsNullOrWhiteSpace(item.Category))
                {
                    ignoreMatchingFields.Add(item.Category);
                }
            }
        }
        catch
        {
        }

        return ignoreMatchingFields;
    }

    private async Task<BslBackendTaskItem> WaitForInstallTaskAsync(
        GameInstallContext? task,
        BslGameActionType actionType,
        string? installPath,
        string actionName,
        Func<BslBackendTaskItem, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        if (task is null)
        {
            return Failed($"{actionName}未启动", "RPC 安装服务未返回任务。", actionType, installPath);
        }

        BslBackendTaskItem item = new()
        {
            GameKey = GameKey,
            DisplayName = DisplayName,
            ActionType = actionType,
            State = BslBackendTaskState.Running,
            StatusText = $"{actionName}任务已启动",
            DetailText = task.State.ToString(),
            InstallPath = installPath,
            Progress = 0,
        };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateTaskFromInstallContext(item, task, actionName);

            if (progressCallback is not null)
            {
                await progressCallback(item);
            }

            if (task.State is GameInstallState.Finish or GameInstallState.Error or GameInstallState.Stop)
            {
                break;
            }

            await Task.Delay(1000, cancellationToken);
        }

        return item;
    }

    private static void UpdateTaskFromInstallContext(BslBackendTaskItem item, GameInstallContext task, string actionName)
    {
        item.Progress = GetInstallProgress(task);
        item.DetailText = BuildInstallDetail(task);

        switch (task.State)
        {
            case GameInstallState.Queueing:
            case GameInstallState.Waiting:
                item.State = BslBackendTaskState.Running;
                item.StatusText = $"{actionName}排队中";
                break;
            case GameInstallState.Downloading:
                item.State = BslBackendTaskState.Running;
                item.StatusText = $"正在{actionName}";
                break;
            case GameInstallState.Decompressing:
                item.State = BslBackendTaskState.Running;
                item.StatusText = "正在解压";
                break;
            case GameInstallState.Merging:
                item.State = BslBackendTaskState.Running;
                item.StatusText = "正在合并";
                break;
            case GameInstallState.Verifying:
                item.State = BslBackendTaskState.Running;
                item.StatusText = "正在校验";
                break;
            case GameInstallState.Paused:
                item.State = BslBackendTaskState.Paused;
                item.StatusText = $"{actionName}已暂停";
                break;
            case GameInstallState.Finish:
                item.State = BslBackendTaskState.Succeeded;
                item.StatusText = $"{actionName}完成";
                item.Progress = 1;
                break;
            case GameInstallState.Error:
                item.State = BslBackendTaskState.Failed;
                item.StatusText = $"{actionName}失败";
                item.DetailText = string.IsNullOrWhiteSpace(task.ErrorMessage) ? "安装服务返回错误。" : task.ErrorMessage;
                break;
            case GameInstallState.Stop:
                item.State = BslBackendTaskState.Canceled;
                item.StatusText = $"{actionName}已停止";
                item.DetailText = string.IsNullOrWhiteSpace(task.ErrorMessage) ? "任务已停止。" : task.ErrorMessage;
                break;
            default:
                item.State = BslBackendTaskState.Running;
                item.StatusText = $"{actionName}处理中";
                break;
        }
    }

    private static double GetInstallProgress(GameInstallContext task)
    {
        if (task.State is GameInstallState.Decompressing or GameInstallState.Merging or GameInstallState.Verifying)
        {
            return Math.Clamp(task.Progress_Percent, 0, 1);
        }

        if (task.State is GameInstallState.Downloading)
        {
            if (task.Operation is GameInstallOperation.Update && task.DownloadMode is GameInstallDownloadMode.Chunk && task.Progress_WriteTotalBytes > 0)
            {
                return Math.Clamp((double)task.Progress_WriteFinishBytes / task.Progress_WriteTotalBytes, 0, 1);
            }

            if (task.Progress_DownloadTotalBytes > 0)
            {
                return Math.Clamp((double)task.Progress_DownloadFinishBytes / task.Progress_DownloadTotalBytes, 0, 1);
            }
        }

        return task.State is GameInstallState.Finish ? 1 : 0;
    }

    private static string? BuildInstallDetail(GameInstallContext task)
    {
        if (!string.IsNullOrWhiteSpace(task.ErrorMessage) && task.State is GameInstallState.Error or GameInstallState.Stop)
        {
            return task.ErrorMessage;
        }

        if (task.State is GameInstallState.Downloading && task.Progress_DownloadTotalBytes > 0)
        {
            return $"{BslDownloadHelper.FormatBytes(task.Progress_DownloadFinishBytes)} / {BslDownloadHelper.FormatBytes(task.Progress_DownloadTotalBytes)}";
        }

        if (task.State is GameInstallState.Decompressing or GameInstallState.Merging or GameInstallState.Verifying)
        {
            return $"{task.Progress_Percent:P0}";
        }

        return task.State.ToString();
    }

    private BslBackendTaskItem Success(string status, string? path, BslGameActionType actionType, string? detail)
    {
        return new BslBackendTaskItem
        {
            GameKey = GameKey,
            DisplayName = DisplayName,
            ActionType = actionType,
            State = BslBackendTaskState.Succeeded,
            StatusText = status,
            DetailText = detail,
            InstallPath = path,
            Progress = 1,
        };
    }

    private BslBackendTaskItem Failed(string status, string? detail, BslGameActionType actionType, string? path = null)
    {
        BslBackendIssueKind issueKind = BslDownloadHelper.ClassifyIssue(status, detail);
        return new BslBackendTaskItem
        {
            GameKey = GameKey,
            DisplayName = DisplayName,
            ActionType = actionType,
            State = BslBackendTaskState.Failed,
            StatusText = status,
            DetailText = detail,
            InstallPath = path,
            IssueKind = issueKind,
            SuggestedAction = BslDownloadHelper.SuggestAction(issueKind),
            Progress = 0,
        };
    }

    private string? NormalizeInstallPath(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(installPath);
            if (!Directory.Exists(fullPath))
            {
                return null;
            }

            string exeName = _gameLauncherService.GetGameExeNameAsync(_gameId).GetAwaiter().GetResult();
            if (File.Exists(Path.Combine(fullPath, exeName))
                || File.Exists(Path.Combine(fullPath, "config.ini")))
            {
                return fullPath;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class IgnoreMatchingField
    {
        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("is_delete")]
        public bool IsDelete { get; set; }
    }
}
