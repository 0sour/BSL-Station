using Starward.Features.GameLauncher;
using Starward.RPC.GameInstall;
using System;
using System.Collections.Generic;

namespace Starward.Features.BSL.Backend;

public sealed class BslGameStatusSnapshot
{
    public string GameKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public BslGameServerRegion Region { get; set; }

    public BslGameSupportLevel SupportLevel { get; set; }

    public BslGameCapability Capabilities { get; set; }

    public bool IsInstalled { get; set; }

    public bool CanLaunch { get; set; }

    public bool CanImport { get; set; }

    public bool CanInstall { get; set; }

    public bool CanUpdate { get; set; }

    public bool CanPredownload { get; set; }

    public bool CanRepair { get; set; }

    public string StatusText { get; set; } = string.Empty;

    public string HintText { get; set; } = string.Empty;

    public string? InstallPath { get; set; }

    public string? ExecutablePath { get; set; }

    public BslGameLaunchSettingsSnapshot? LaunchSettings { get; set; }

    public string? LocalVersion { get; set; }

    public string? LatestVersion { get; set; }

    public string? PredownloadVersion { get; set; }

    public DateTimeOffset LastRefreshed { get; set; } = DateTimeOffset.Now;

    public BslGameActionType PrimaryAction { get; set; }

    public GameState PrimaryButtonState { get; set; } = GameState.StartGame;

    public string? PrimaryButtonOverrideText { get; set; }

    public BslGameActionType ActiveTaskAction { get; set; }

    public BslBackendTaskState ActiveTaskState { get; set; }

    public GameInstallState ActiveTaskInstallState { get; set; } = GameInstallState.Stop;

    public double ActiveTaskProgress { get; set; }

    public string? ActiveTaskDetailText { get; set; }

    public BslBackendIssueKind ActiveTaskIssueKind { get; set; }

    public BslBackendSuggestedAction ActiveTaskSuggestedAction { get; set; }

    public bool ActiveTaskHasResidualCache { get; set; }

    public string? ActiveTaskResidualCachePath { get; set; }

    public bool IsBusy { get; set; }

    public bool HasPredownloadTask { get; set; }

    public GameInstallState PredownloadButtonState { get; set; } = GameInstallState.Finish;

    public string PredownloadText => CanPredownload ? "预下载可用" : "预下载未开放";

    public List<string> Warnings { get; } = [];

    public string RegionText => Region switch
    {
        BslGameServerRegion.China => "国服",
        BslGameServerRegion.Global => "官服",
        BslGameServerRegion.Bilibili => "B服",
        _ => "未指定",
    };

    public string SupportLevelText => SupportLevel switch
    {
        BslGameSupportLevel.Verified => "完整适配",
        BslGameSupportLevel.Partial => "部分适配",
        _ => "规划中",
    };

    public string CapabilitiesText => string.Join(" / ", GetCapabilityLabels());

    public string VersionText => $"本地：{LocalVersion ?? "未知"}    最新：{LatestVersion ?? "未知"}";

    public string InstallPathText => string.IsNullOrWhiteSpace(InstallPath) ? "未设置" : InstallPath;

    public string ExecutablePathText => string.IsNullOrWhiteSpace(ExecutablePath) ? "未设置" : ExecutablePath;

    public string LaunchArgumentText => LaunchSettings?.LaunchArgumentText ?? "未设置";

    public bool HasWarnings => Warnings.Count > 0;

    public string WarningText => string.Join("；", Warnings);

    public void RefreshUiSemantics()
    {
        if (IsBusy)
        {
            PrimaryButtonState = GameState.Installing;
            PrimaryAction = BslGameActionType.Refresh;
            if (PredownloadButtonState is GameInstallState.Finish)
            {
                PredownloadButtonState = GameInstallState.Stop;
            }

            return;
        }

        if (CanUpdate)
        {
            PrimaryAction = BslGameActionType.Update;
            PrimaryButtonState = GameState.UpdateGame;
            PrimaryButtonOverrideText = null;
        }
        else if (CanInstall)
        {
            PrimaryAction = BslGameActionType.Install;
            PrimaryButtonState = GameState.InstallGame;
            PrimaryButtonOverrideText = null;
        }
        else if (CanLaunch)
        {
            PrimaryAction = BslGameActionType.Launch;
            PrimaryButtonState = GameState.StartGame;
            PrimaryButtonOverrideText = null;
        }
        else if (CanImport)
        {
            PrimaryAction = BslGameActionType.Import;
            PrimaryButtonState = GameState.StartGame;
            PrimaryButtonOverrideText = "导入游戏";
        }
        else
        {
            PrimaryAction = BslGameActionType.Refresh;
            PrimaryButtonState = GameState.StartGame;
            PrimaryButtonOverrideText = "查看详情";
        }

        PredownloadButtonState = CanPredownload ? GameInstallState.Stop : GameInstallState.Finish;
    }

    private List<string> GetCapabilityLabels()
    {
        List<string> values = [];
        if (Capabilities.HasFlag(BslGameCapability.Import)) { values.Add("导入"); }
        if (Capabilities.HasFlag(BslGameCapability.Launch)) { values.Add("启动"); }
        if (Capabilities.HasFlag(BslGameCapability.Download)) { values.Add("下载"); }
        if (Capabilities.HasFlag(BslGameCapability.Update)) { values.Add("更新"); }
        if (Capabilities.HasFlag(BslGameCapability.Predownload)) { values.Add("预下载"); }
        if (Capabilities.HasFlag(BslGameCapability.Repair)) { values.Add("修复"); }
        if (Capabilities.HasFlag(BslGameCapability.Uninstall)) { values.Add("卸载"); }
        if (Capabilities.HasFlag(BslGameCapability.Notices)) { values.Add("公告"); }
        if (Capabilities.HasFlag(BslGameCapability.Background)) { values.Add("背景"); }
        return values;
    }
}
