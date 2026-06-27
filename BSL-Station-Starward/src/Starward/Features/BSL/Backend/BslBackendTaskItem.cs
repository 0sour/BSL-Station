using CommunityToolkit.Mvvm.ComponentModel;
using Starward.RPC.GameInstall;
using System;

namespace Starward.Features.BSL.Backend;

public sealed partial class BslBackendTaskItem : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string GameKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public BslGameActionType ActionType { get; set; }

    [ObservableProperty]
    private BslBackendTaskState state;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private string? detailText;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private GameInstallState installState = GameInstallState.Stop;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    [ObservableProperty]
    private DateTimeOffset updatedAt = DateTimeOffset.Now;

    public string? InstallPath { get; set; }

    public int RetryCount { get; set; }

    public int MaxRetryCount { get; set; }

    public bool CanRetry => RetryCount < MaxRetryCount;

    [ObservableProperty]
    private bool hasResidualCache;

    public string? ResidualCachePath { get; set; }

    public string? CleanupHint { get; set; }

    public bool RecommendManualCleanup { get; set; }

    public BslBackendIssueKind IssueKind { get; set; }

    public BslBackendSuggestedAction SuggestedAction { get; set; }

    public string ActionText => ActionType switch
    {
        BslGameActionType.Import => "导入",
        BslGameActionType.Launch => "启动",
        BslGameActionType.Install => "安装",
        BslGameActionType.Update => "更新",
        BslGameActionType.Predownload => "预下载",
        BslGameActionType.Repair => "修复",
        BslGameActionType.Uninstall => "卸载",
        BslGameActionType.Refresh => "刷新",
        _ => "未知",
    };

    public string StateText => State switch
    {
        BslBackendTaskState.Pending => "待开始",
        BslBackendTaskState.Queued => StatusText.StartsWith("等待恢复后", StringComparison.Ordinal) ? "已恢复" : "排队中",
        BslBackendTaskState.Running => "执行中",
        BslBackendTaskState.Paused => "已暂停",
        BslBackendTaskState.Succeeded => "已完成",
        BslBackendTaskState.Failed => "失败",
        BslBackendTaskState.Canceled => "已取消",
        _ => "未知",
    };

    public string ProgressText => $"{Progress:P0}";

    public string SourceLabel => "BSL 自建队列";

    public string ControlPolicyText => "可暂停 / 继续 / 取消 / 重试 / 移除 / 清理残留缓存；当前仍需继续做实机验证和边界状态收口。";

    public bool CanPauseFromQueue => State is BslBackendTaskState.Queued or BslBackendTaskState.Running;

    public bool CanContinueFromQueue => State is BslBackendTaskState.Paused;

    public bool CanCancelFromQueue => State is BslBackendTaskState.Queued or BslBackendTaskState.Running or BslBackendTaskState.Paused;

    public bool CanRetryFromQueue => State is BslBackendTaskState.Failed or BslBackendTaskState.Canceled
                                     && !HasResidualCache
                                     && CanRetry
                                     && ActionType != BslGameActionType.None;

    public bool CanRemoveFromQueue => State is BslBackendTaskState.Succeeded or BslBackendTaskState.Canceled
                                      || State == BslBackendTaskState.Failed && !HasResidualCache;

    public bool CanCleanupResidualCache => HasResidualCache;

    partial void OnStateChanged(BslBackendTaskState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(CanPauseFromQueue));
        OnPropertyChanged(nameof(CanContinueFromQueue));
        OnPropertyChanged(nameof(CanCancelFromQueue));
        OnPropertyChanged(nameof(CanRetryFromQueue));
        OnPropertyChanged(nameof(CanRemoveFromQueue));
    }

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(StateText));
    }

    partial void OnProgressChanged(double value)
    {
        OnPropertyChanged(nameof(ProgressText));
    }

    partial void OnHasResidualCacheChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRetryFromQueue));
        OnPropertyChanged(nameof(CanRemoveFromQueue));
        OnPropertyChanged(nameof(CanCleanupResidualCache));
    }
}
