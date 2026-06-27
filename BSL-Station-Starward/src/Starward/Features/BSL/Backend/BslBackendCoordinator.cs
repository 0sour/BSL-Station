using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Starward.Helpers;
using Starward.RPC.GameInstall;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.BSL.Backend;

internal sealed class BslBackendCoordinator
{
    private readonly ILogger<BslBackendCoordinator> _logger = AppConfig.GetLogger<BslBackendCoordinator>();
    private readonly Dictionary<string, IBslGameAdapter> _adapters;
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly object _activeTaskLock = new();
    private readonly Dictionary<Guid, BslTaskControlIntent> _controlIntents = [];

    private bool _workerRunning;
    private Guid? _activeTaskId;
    private CancellationTokenSource? _activeTaskCts;

    public BslBackendCoordinator(IEnumerable<IBslGameAdapter> adapters)
    {
        _adapters = adapters.ToDictionary(x => x.GameKey, StringComparer.OrdinalIgnoreCase);
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        Tasks.CollectionChanged += Tasks_CollectionChanged;
        RestorePersistedTasks();
        if (Tasks.Any(x => x.State == BslBackendTaskState.Queued))
        {
            _ = EnsureWorkerLoopAsync(CancellationToken.None);
        }
    }

    public ObservableCollection<BslBackendTaskItem> Tasks { get; } = [];

    public IReadOnlyCollection<IBslGameAdapter> Adapters => _adapters.Values;

    public IBslGameAdapter? GetAdapter(string gameKey)
    {
        _adapters.TryGetValue(gameKey, out IBslGameAdapter? adapter);
        return adapter;
    }

    public async Task<IReadOnlyList<BslGameStatusSnapshot>> RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        List<BslGameStatusSnapshot> list = [];
        foreach (IBslGameAdapter adapter in _adapters.Values)
        {
            try
            {
                BslGameStatusSnapshot snapshot = await adapter.GetStatusAsync(cancellationToken);
                await ApplyQueueTaskOverlayAsync(snapshot);
                snapshot.RefreshUiSemantics();
                list.Add(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Refresh game status failed: {GameKey}", adapter.GameKey);
                list.Add(new BslGameStatusSnapshot
                {
                    GameKey = adapter.GameKey,
                    DisplayName = adapter.DisplayName,
                    Region = adapter.Region,
                    SupportLevel = adapter.SupportLevel,
                    Capabilities = adapter.Capabilities,
                    StatusText = "状态刷新失败",
                    HintText = ex.Message,
                    PrimaryAction = BslGameActionType.Refresh,
                    PrimaryButtonOverrideText = "查看详情",
                    LastRefreshed = DateTimeOffset.Now,
                });
            }
        }

        return list;
    }

    public async Task<BslGameStatusSnapshot> GetStatusAsync(string gameKey, CancellationToken cancellationToken = default)
    {
        IBslGameAdapter adapter = GetAdapter(gameKey) ?? throw new InvalidOperationException($"Unknown adapter: {gameKey}");
        BslGameStatusSnapshot snapshot = await adapter.GetStatusAsync(cancellationToken);
        if (snapshot.LaunchSettings is null)
        {
            snapshot.LaunchSettings = await adapter.GetLaunchSettingsAsync(cancellationToken);
        }

        await ApplyQueueTaskOverlayAsync(snapshot);
        snapshot.RefreshUiSemantics();
        return snapshot;
    }

    public async Task<BslBackendTaskItem> QueueAsync(BslQueuedActionRequest request, CancellationToken cancellationToken = default)
    {
        IBslGameAdapter adapter = GetAdapter(request.GameKey) ?? throw new InvalidOperationException($"Unknown adapter: {request.GameKey}");
        BslBackendTaskItem? existing = await RunOnUiThreadAsync(() => FindDuplicateTask(request.GameKey, request.ActionType));
        if (existing is not null)
        {
            return existing;
        }
        BslBackendTaskItem item = new()
        {
            GameKey = request.GameKey,
            DisplayName = adapter.DisplayName,
            ActionType = request.ActionType,
            State = BslBackendTaskState.Queued,
            StatusText = "已加入队列",
            InstallPath = request.InstallPath,
            RetryCount = 0,
            MaxRetryCount = Math.Max(0, request.MaxRetryCount),
            InstallState = GameInstallState.Queueing,
        };

        await RunOnUiThreadAsync(() => Tasks.Add(item));
        _ = EnsureWorkerLoopAsync(cancellationToken);
        return item;
    }

    public Task<BslGameLaunchSettingsSnapshot> GetLaunchSettingsAsync(string gameKey, CancellationToken cancellationToken = default)
    {
        IBslGameAdapter adapter = GetAdapter(gameKey) ?? throw new InvalidOperationException($"Unknown adapter: {gameKey}");
        return adapter.GetLaunchSettingsAsync(cancellationToken);
    }

    public Task<BslLaunchSettingsSaveResult> ImportGameAsync(
        string gameKey,
        string? installPath,
        CancellationToken cancellationToken = default)
    {
        IBslGameAdapter adapter = GetAdapter(gameKey) ?? throw new InvalidOperationException($"Unknown adapter: {gameKey}");
        return adapter.ImportGameAsync(installPath, cancellationToken);
    }

    public async Task<BslGameLaunchSettingsSnapshot> SaveLaunchSettingsAsync(
        string gameKey,
        BslGameLaunchSettingsUpdate update,
        CancellationToken cancellationToken = default)
    {
        return (await SaveLaunchSettingsAndRefreshStatusAsync(gameKey, update, cancellationToken)).LaunchSettings;
    }

    public async Task<BslLaunchSettingsSaveResult> SaveLaunchSettingsAndRefreshStatusAsync(
        string gameKey,
        BslGameLaunchSettingsUpdate update,
        CancellationToken cancellationToken = default)
    {
        IBslGameAdapter adapter = GetAdapter(gameKey) ?? throw new InvalidOperationException($"Unknown adapter: {gameKey}");
        string? normalizedInstallPath = null;
        if (update.UpdateInstallPath)
        {
            normalizedInstallPath = await adapter.NormalizeInstallPathAsync(update.InstallPath, cancellationToken);
        }

        BslGameLaunchSettingsUpdate normalizedUpdate = new()
        {
            UpdateInstallPath = update.UpdateInstallPath,
            InstallPath = normalizedInstallPath,
            UseCustomExecutable = update.UseCustomExecutable,
            UpdateCustomExecutablePath = update.UpdateCustomExecutablePath,
            CustomExecutablePath = update.CustomExecutablePath,
            UpdateLaunchArgument = update.UpdateLaunchArgument,
            LaunchArgument = update.LaunchArgument,
        };
        await adapter.SaveLaunchSettingsAsync(normalizedUpdate, cancellationToken);

        BslGameLaunchSettingsSnapshot launchSettings = await adapter.GetLaunchSettingsAsync(cancellationToken);
        BslGameStatusSnapshot status = await adapter.GetStatusAsync(cancellationToken);
        if (update.UpdateInstallPath
            && !string.IsNullOrWhiteSpace(update.InstallPath)
            && string.IsNullOrWhiteSpace(normalizedInstallPath))
        {
            launchSettings.InvalidInstallPath = update.InstallPath;
        }

        BslLaunchSettingsHelper.ApplyToStatus(status, launchSettings);
        status.RefreshUiSemantics();

        return BslLaunchSettingsHelper.CreateSaveResult(gameKey, normalizedUpdate, launchSettings, status);
    }

    public async Task<bool> CleanupResidualCacheAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        BslBackendTaskItem? task = await RunOnUiThreadAsync(() => Tasks.FirstOrDefault(x => x.Id == taskId));
        if (task is null || !task.HasResidualCache)
        {
            return false;
        }

        string? residualPath = task.ResidualCachePath;
        bool cleaned = string.IsNullOrWhiteSpace(residualPath) || BslDownloadHelper.TryDeletePaths(residualPath);

        await RunOnUiThreadAsync(() =>
        {
            if (cleaned)
            {
                ClearResidualCacheMetadata(task);
                task.StatusText = "任务失败";
                task.DetailText = AppendDetailMessage(task.DetailText, "残留缓存已清理，可重新发起任务。");
                task.IssueKind = BslBackendIssueKind.None;
                task.SuggestedAction = BslBackendSuggestedAction.Retry;
            }
            else
            {
                task.StatusText = "残留缓存清理失败";
                task.DetailText = AppendDetailMessage(task.DetailText, "自动清理失败，请按提示手动清理残留缓存。");
                task.RecommendManualCleanup = true;
                task.IssueKind = BslBackendIssueKind.ResidualCache;
                task.SuggestedAction = BslBackendSuggestedAction.CleanResidualCache;
            }

            task.UpdatedAt = DateTimeOffset.Now;
            PersistTasks();
        });

        return cleaned;
    }

    public async Task<bool> RemoveTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await RunOnUiThreadAsync(() =>
        {
            BslBackendTaskItem? task = Tasks.FirstOrDefault(x => x.Id == taskId);
            if (task is null || !CanRemoveTask(task))
            {
                return false;
            }

            bool removed = Tasks.Remove(task);
            if (removed)
            {
                PersistTasks();
            }

            return removed;
        });
    }

    public async Task<int> ClearFinishedTasksAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await RunOnUiThreadAsync(() =>
        {
            List<BslBackendTaskItem> removableTasks = Tasks
                .Where(CanRemoveTask)
                .ToList();

            foreach (BslBackendTaskItem task in removableTasks)
            {
                Tasks.Remove(task);
            }

            if (removableTasks.Count > 0)
            {
                PersistTasks();
            }

            return removableTasks.Count;
        });
    }

    public async Task<bool> RetryTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool retried = await RunOnUiThreadAsync(() =>
        {
            BslBackendTaskItem? task = Tasks.FirstOrDefault(x => x.Id == taskId);
            if (task is null || !CanRetryTask(task))
            {
                return false;
            }

            ClearControlIntent(task.Id);
            ResetTaskForRetry(task, "已重新加入队列", "将重新执行该任务。");
            PersistTasks();
            return true;
        });

        if (retried)
        {
            _ = EnsureWorkerLoopAsync(cancellationToken);
        }

        return retried;
    }

    public async Task<bool> PauseTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool wasRunning = false;
        bool paused = await RunOnUiThreadAsync(() =>
        {
            BslBackendTaskItem? task = Tasks.FirstOrDefault(x => x.Id == taskId);
            if (task is null || task.State is not (BslBackendTaskState.Queued or BslBackendTaskState.Running))
            {
                return false;
            }

            wasRunning = task.State == BslBackendTaskState.Running;
            MarkControlIntent(task.Id, BslTaskControlIntent.Pause);
            task.State = BslBackendTaskState.Paused;
            task.StatusText = "已暂停";
            task.DetailText = "任务已暂停，可继续执行。";
            task.InstallState = GameInstallState.Paused;
            task.UpdatedAt = DateTimeOffset.Now;
            PersistTasks();
            return true;
        });

        if (paused)
        {
            // For a Running task we keep the intent even when CancelActiveTaskIfMatches returns false:
            // the worker may not have registered its CTS yet and will honor the intent on its own.
            // For a Queued task the worker never started, so the intent can be dropped immediately.
            if (!CancelActiveTaskIfMatches(taskId) && !wasRunning)
            {
                ClearControlIntent(taskId);
            }
        }

        return paused;
    }

    public async Task<bool> ContinueTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool continued = await RunOnUiThreadAsync(() =>
        {
            BslBackendTaskItem? task = Tasks.FirstOrDefault(x => x.Id == taskId);
            if (task is null || task.State != BslBackendTaskState.Paused)
            {
                return false;
            }

            ClearControlIntent(task.Id);
            task.State = BslBackendTaskState.Queued;
            task.StatusText = "已加入队列";
            task.DetailText = "任务将从本地缓存继续执行。";
            task.InstallState = GameInstallState.Queueing;
            task.UpdatedAt = DateTimeOffset.Now;
            PersistTasks();
            return true;
        });

        if (continued)
        {
            _ = EnsureWorkerLoopAsync(cancellationToken);
        }

        return continued;
    }

    public async Task<bool> CancelTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool wasRunning = false;
        bool canceled = await RunOnUiThreadAsync(() =>
        {
            BslBackendTaskItem? task = Tasks.FirstOrDefault(x => x.Id == taskId);
            if (task is null || task.State is not (BslBackendTaskState.Queued or BslBackendTaskState.Running or BslBackendTaskState.Paused))
            {
                return false;
            }

            wasRunning = task.State == BslBackendTaskState.Running;
            MarkControlIntent(task.Id, BslTaskControlIntent.Cancel);
            task.State = BslBackendTaskState.Canceled;
            task.StatusText = "已取消";
            task.DetailText = "任务已取消，已下载的可续传缓存会保留到后续任务复用或手动清理。";
            task.InstallState = GameInstallState.Stop;
            task.UpdatedAt = DateTimeOffset.Now;
            PersistTasks();
            return true;
        });

        if (canceled)
        {
            // Same race handling as PauseTaskAsync: keep the intent for a Running task whose CTS may
            // not be registered yet; a Queued/Paused task has no in-flight work, so drop the intent.
            if (!CancelActiveTaskIfMatches(taskId) && !wasRunning)
            {
                ClearControlIntent(taskId);
            }
        }

        return canceled;
    }

    private void Tasks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        PersistTasks();
    }

    private async Task EnsureWorkerLoopAsync(CancellationToken cancellationToken)
    {
        await _queueLock.WaitAsync(cancellationToken);
        try
        {
            if (_workerRunning)
            {
                return;
            }

            _workerRunning = true;
        }
        finally
        {
            _queueLock.Release();
        }

        try
        {
            while (true)
            {
                BslBackendTaskItem? current = await RunOnUiThreadAsync(() => Tasks.FirstOrDefault(x => x.State == BslBackendTaskState.Queued));
                if (current is null)
                {
                    break;
                }

                // Promote to Running only if the task is still Queued. A Pause/Cancel issued in the
                // gap between "find queued" and here may already have moved it to Paused/Canceled,
                // and we must not resurrect it back to Running.
                bool promoted = await RunOnUiThreadAsync(() =>
                {
                    if (current.State != BslBackendTaskState.Queued)
                    {
                        return false;
                    }

                    current.State = BslBackendTaskState.Running;
                    current.StatusText = current.RetryCount > 0 ? $"正在重试 ({current.RetryCount}/{current.MaxRetryCount})" : "正在执行";
                    current.UpdatedAt = DateTimeOffset.Now;
                    PersistTasks();
                    return true;
                });

                if (!promoted)
                {
                    // The intent (if any) was already applied to the task state by Pause/Cancel.
                    ClearControlIntent(current.Id);
                    continue;
                }

                try
                {
                    IBslGameAdapter adapter = _adapters[current.GameKey];
                    using CancellationTokenSource activeTaskCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    SetActiveTask(current.Id, activeTaskCts);

                    // Close the startup-window race: a Pause/Cancel that arrived before the CTS was
                    // registered left its intent behind without being able to cancel. Honor it now.
                    if (HasControlIntent(current.Id))
                    {
                        activeTaskCts.Cancel();
                    }

                    BslBackendTaskItem result = await adapter.ExecuteAsync(new BslQueuedActionRequest
                    {
                        GameKey = current.GameKey,
                        ActionType = current.ActionType,
                        InstallPath = current.InstallPath,
                        MaxRetryCount = current.MaxRetryCount,
                        ProgressCallback = task => RunOnUiThreadAsync(() =>
                        {
                            if (HasControlIntent(current.Id))
                            {
                                return;
                            }

                            current.State = task.State;
                            current.StatusText = task.StatusText;
                            current.DetailText = task.DetailText;
                            current.Progress = task.Progress;
                            current.InstallState = task.InstallState;
                            current.InstallPath = task.InstallPath;
                            current.HasResidualCache = task.HasResidualCache;
                            current.ResidualCachePath = task.ResidualCachePath;
                            current.CleanupHint = task.CleanupHint;
                            current.RecommendManualCleanup = task.RecommendManualCleanup;
                            current.IssueKind = task.IssueKind;
                            current.SuggestedAction = task.SuggestedAction;
                            current.UpdatedAt = DateTimeOffset.Now;
                            PersistTasks();
                        }),
                    }, activeTaskCts.Token);

                    ClearActiveTask(current.Id);

                    BslTaskControlIntent? completedIntent = ConsumeControlIntent(current.Id);
                    if (completedIntent is not null)
                    {
                        // If the work actually finished before the cancellation took effect, keep the
                        // success: the files are already on disk and reporting "已暂停/已取消" would be wrong.
                        if (result.State != BslBackendTaskState.Succeeded)
                        {
                            await ApplyControlledCancellationAsync(current, completedIntent.Value);
                            continue;
                        }
                    }

                    bool scheduledRetry = false;
                    if (ShouldRetry(current, result))
                    {
                        scheduledRetry = true;
                        await ScheduleRetryAsync(current, result);
                    }

                    if (!scheduledRetry)
                    {
                        await RunOnUiThreadAsync(() =>
                        {
                            current.State = result.State;
                            current.StatusText = result.StatusText;
                            current.DetailText = result.DetailText;
                            current.Progress = result.Progress;
                            current.InstallState = result.InstallState;
                            current.InstallPath = result.InstallPath;
                            current.HasResidualCache = result.HasResidualCache;
                            current.ResidualCachePath = result.ResidualCachePath;
                            current.CleanupHint = result.CleanupHint;
                            current.RecommendManualCleanup = result.RecommendManualCleanup;
                            current.IssueKind = result.IssueKind;
                            current.SuggestedAction = result.SuggestedAction;
                            current.UpdatedAt = DateTimeOffset.Now;
                            PersistTasks();
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    ClearActiveTask(current.Id);
                    BslTaskControlIntent? intent = ConsumeControlIntent(current.Id);
                    if (intent is not null)
                    {
                        await ApplyControlledCancellationAsync(current, intent.Value);
                    }
                    else
                    {
                        await RunOnUiThreadAsync(() =>
                        {
                            current.State = BslBackendTaskState.Failed;
                            current.StatusText = "执行已中断";
                            current.DetailText = "任务被系统中断，可重新加入队列。";
                            current.InstallState = GameInstallState.Error;
                            current.UpdatedAt = DateTimeOffset.Now;
                            PersistTasks();
                        });
                    }
                }
                catch (Exception ex)
                {
                    ClearActiveTask(current.Id);
                    _logger.LogError(ex, "Execute queued action failed: {GameKey} {ActionType}", current.GameKey, current.ActionType);
                    bool scheduledRetry = current.CanRetry && IsRetryableException(ex);
                    if (scheduledRetry)
                    {
                        await ScheduleRetryAsync(current, new BslBackendTaskItem
                        {
                            GameKey = current.GameKey,
                            DisplayName = current.DisplayName,
                            ActionType = current.ActionType,
                            State = BslBackendTaskState.Failed,
                            StatusText = "执行失败",
                            DetailText = ex.Message,
                            Progress = current.Progress,
                            InstallState = current.InstallState,
                            InstallPath = current.InstallPath,
                            IssueKind = BslDownloadHelper.ClassifyIssue("执行失败", ex.Message),
                            SuggestedAction = BslDownloadHelper.SuggestAction(BslDownloadHelper.ClassifyIssue("执行失败", ex.Message)),
                        });
                    }
                    else
                    {
                        await RunOnUiThreadAsync(() =>
                        {
                            current.State = BslBackendTaskState.Failed;
                            current.StatusText = "执行失败";
                            current.DetailText = ex.Message;
                            current.InstallState = GameInstallState.Error;
                            current.IssueKind = BslDownloadHelper.ClassifyIssue(current.StatusText, current.DetailText);
                            current.SuggestedAction = BslDownloadHelper.SuggestAction(current.IssueKind);
                            current.UpdatedAt = DateTimeOffset.Now;
                            PersistTasks();
                        });
                        InAppToast.MainWindow?.Error(ex);
                    }
                }
            }
        }
        finally
        {
            await _queueLock.WaitAsync(cancellationToken);
            try
            {
                _workerRunning = false;
            }
            finally
            {
                _queueLock.Release();
            }
        }
    }

    private static bool ShouldRetry(BslBackendTaskItem current, BslBackendTaskItem result)
    {
        if (!current.CanRetry
            || result.State != BslBackendTaskState.Failed
            || result.HasResidualCache)
        {
            return false;
        }

        return IsRetryableIssueKind(result.IssueKind)
               || IsRetryableMessage(result.DetailText)
               || IsRetryableMessage(result.StatusText);
    }

    private static bool IsRetryableException(Exception ex)
    {
        return ex is TimeoutException
               || ex is HttpRequestException
               || ex is TaskCanceledException
               || ex is IOException
               || IsRetryableMessage(ex.Message);
    }

    private static bool IsRetryableMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return ContainsAny(
            text,
            "超时",
            "timeout",
            "network",
            "http",
            "连接失败",
            "连接中断",
            "远程主机",
            "下载失败",
            "更新失败",
            "安装失败",
            "同步失败",
            "获取资源失败");
    }

    private async Task ScheduleRetryAsync(BslBackendTaskItem current, BslBackendTaskItem result)
    {
        await RunOnUiThreadAsync(() =>
        {
            current.RetryCount++;
            ClearControlIntent(current.Id);
            ResetTaskForRetry(
                current,
                $"准备重试 ({current.RetryCount}/{current.MaxRetryCount})",
                result.DetailText ?? result.StatusText);
            PersistTasks();
        });
    }

    private void MarkControlIntent(Guid taskId, BslTaskControlIntent intent)
    {
        lock (_activeTaskLock)
        {
            _controlIntents[taskId] = intent;
        }
    }

    private void ClearControlIntent(Guid taskId)
    {
        lock (_activeTaskLock)
        {
            _controlIntents.Remove(taskId);
        }
    }

    private bool HasControlIntent(Guid taskId)
    {
        lock (_activeTaskLock)
        {
            return _controlIntents.ContainsKey(taskId);
        }
    }

    private BslTaskControlIntent? ConsumeControlIntent(Guid taskId)
    {
        lock (_activeTaskLock)
        {
            if (_controlIntents.Remove(taskId, out BslTaskControlIntent intent))
            {
                return intent;
            }

            return null;
        }
    }

    private void SetActiveTask(Guid taskId, CancellationTokenSource cts)
    {
        lock (_activeTaskLock)
        {
            _activeTaskId = taskId;
            _activeTaskCts = cts;
        }
    }

    private void ClearActiveTask(Guid taskId)
    {
        lock (_activeTaskLock)
        {
            if (_activeTaskId == taskId)
            {
                _activeTaskId = null;
                _activeTaskCts = null;
            }
        }
    }

    private bool CancelActiveTaskIfMatches(Guid taskId)
    {
        lock (_activeTaskLock)
        {
            if (_activeTaskId != taskId || _activeTaskCts is null)
            {
                return false;
            }

            try
            {
                _activeTaskCts.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    private async Task ApplyControlledCancellationAsync(BslBackendTaskItem task, BslTaskControlIntent intent)
    {
        await RunOnUiThreadAsync(() =>
        {
            switch (intent)
            {
                case BslTaskControlIntent.Pause:
                    task.State = BslBackendTaskState.Paused;
                    task.StatusText = "已暂停";
                    task.DetailText = "任务已暂停，可继续执行。";
                    task.InstallState = GameInstallState.Paused;
                    break;
                case BslTaskControlIntent.Cancel:
                    task.State = BslBackendTaskState.Canceled;
                    task.StatusText = "已取消";
                    task.DetailText = "任务已取消，已下载的可续传缓存会保留到后续任务复用或手动清理。";
                    task.InstallState = GameInstallState.Stop;
                    break;
                default:
                    task.State = BslBackendTaskState.Failed;
                    task.StatusText = "执行已中断";
                    task.DetailText = "任务被系统中断，可重新加入队列。";
                    task.InstallState = GameInstallState.Error;
                    break;
            }

            task.UpdatedAt = DateTimeOffset.Now;
            PersistTasks();
        });
    }

    private static void ClearResidualCacheMetadata(BslBackendTaskItem task)
    {
        task.HasResidualCache = false;
        task.ResidualCachePath = null;
        task.CleanupHint = null;
        task.RecommendManualCleanup = false;
        if (task.IssueKind == BslBackendIssueKind.ResidualCache)
        {
            task.IssueKind = BslBackendIssueKind.None;
        }

        if (task.SuggestedAction == BslBackendSuggestedAction.CleanResidualCache)
        {
            task.SuggestedAction = BslBackendSuggestedAction.None;
        }
    }

    private static bool CanRemoveTask(BslBackendTaskItem task)
    {
        return task.State is BslBackendTaskState.Succeeded or BslBackendTaskState.Canceled
               || task.State == BslBackendTaskState.Failed && !task.HasResidualCache;
    }

    private static bool CanRetryTask(BslBackendTaskItem task)
    {
        return task.State is BslBackendTaskState.Failed or BslBackendTaskState.Canceled
               && !task.HasResidualCache
               && task.ActionType != BslGameActionType.None;
    }

    private static string AppendDetailMessage(string? detailText, string message)
    {
        if (string.IsNullOrWhiteSpace(detailText))
        {
            return message;
        }

        if (detailText.Contains(message, StringComparison.Ordinal))
        {
            return detailText;
        }

        return $"{detailText}{Environment.NewLine}{message}";
    }

    private void RestorePersistedTasks()
    {
        foreach (BslBackendQueueStoreItem item in BslBackendQueueStore.Load())
        {
            if (!_adapters.ContainsKey(item.GameKey))
            {
                continue;
            }

            if (!IsRecoverableQueueAction(item.ActionType))
            {
                continue;
            }

            BslBackendTaskState restoredState = item.State switch
            {
                BslBackendTaskState.Running => BslBackendTaskState.Queued,
                BslBackendTaskState.Pending => BslBackendTaskState.Queued,
                _ => item.State,
            };

            if (restoredState is not BslBackendTaskState.Queued
                && restoredState is not BslBackendTaskState.Paused
                && restoredState is not BslBackendTaskState.Failed)
            {
                continue;
            }

            string? normalizedResidualCachePath = item.HasResidualCache
                ? BslDownloadHelper.NormalizeExistingPath(item.ResidualCachePath)
                : null;
            bool keepFailedResidual = restoredState == BslBackendTaskState.Failed && normalizedResidualCachePath is not null;
            if (restoredState == BslBackendTaskState.Failed && !keepFailedResidual)
            {
                continue;
            }

            bool restorePaused = restoredState == BslBackendTaskState.Paused;
            Tasks.Add(new BslBackendTaskItem
            {
                Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id,
                GameKey = item.GameKey,
                DisplayName = item.DisplayName,
                ActionType = item.ActionType,
                State = keepFailedResidual
                    ? BslBackendTaskState.Failed
                    : restorePaused
                        ? BslBackendTaskState.Paused
                        : BslBackendTaskState.Queued,
                StatusText = keepFailedResidual
                    ? item.StatusText
                    : restorePaused
                        ? "已暂停"
                        : item.RetryCount > 0
                            ? $"等待恢复后重试 ({item.RetryCount}/{item.MaxRetryCount})"
                            : "等待恢复后继续",
                DetailText = keepFailedResidual
                    ? item.DetailText
                    : restorePaused
                        ? "启动器重开后已恢复暂停的任务，可点击继续从本地缓存接着执行。"
                        : "启动器重开后已恢复下载队列，将先检查本地缓存再继续执行。",
                Progress = keepFailedResidual || restorePaused ? item.Progress : 0,
                InstallState = keepFailedResidual
                    ? item.InstallState
                    : restorePaused
                        ? GameInstallState.Paused
                        : GameInstallState.Queueing,
                CreatedAt = item.CreatedAt == default ? DateTimeOffset.Now : item.CreatedAt,
                UpdatedAt = DateTimeOffset.Now,
                InstallPath = item.InstallPath,
                RetryCount = item.RetryCount,
                MaxRetryCount = item.MaxRetryCount,
                HasResidualCache = normalizedResidualCachePath is not null,
                ResidualCachePath = normalizedResidualCachePath,
                CleanupHint = normalizedResidualCachePath is not null ? item.CleanupHint : null,
                RecommendManualCleanup = normalizedResidualCachePath is not null && item.RecommendManualCleanup,
                IssueKind = normalizedResidualCachePath is not null ? item.IssueKind : BslBackendIssueKind.None,
                SuggestedAction = normalizedResidualCachePath is not null ? item.SuggestedAction : BslBackendSuggestedAction.None,
            });
        }

        PersistTasks();
    }

    private void PersistTasks()
    {
        List<BslBackendQueueStoreItem> items = Tasks
            .Where(ShouldPersistTask)
            .Select(task => new BslBackendQueueStoreItem
            {
                Id = task.Id,
                GameKey = task.GameKey,
                DisplayName = task.DisplayName,
                ActionType = task.ActionType,
                State = task.State,
                StatusText = task.StatusText,
                DetailText = task.DetailText,
                Progress = task.Progress,
                InstallState = task.InstallState,
                CreatedAt = task.CreatedAt,
                UpdatedAt = task.UpdatedAt,
                InstallPath = task.InstallPath,
                RetryCount = task.RetryCount,
                MaxRetryCount = task.MaxRetryCount,
                HasResidualCache = task.HasResidualCache,
                ResidualCachePath = task.ResidualCachePath,
                CleanupHint = task.CleanupHint,
                RecommendManualCleanup = task.RecommendManualCleanup,
                IssueKind = task.IssueKind,
                SuggestedAction = task.SuggestedAction,
            })
            .ToList();

        BslBackendQueueStore.Save(items);
    }

    private static bool ShouldPersistTask(BslBackendTaskItem task)
    {
        return IsRecoverableQueueAction(task.ActionType)
               && (task.State is BslBackendTaskState.Pending
                   or BslBackendTaskState.Queued
                   or BslBackendTaskState.Running
                   or BslBackendTaskState.Paused
                   || task.State == BslBackendTaskState.Failed && task.HasResidualCache);
    }

    private static bool IsRecoverableQueueAction(BslGameActionType actionType)
    {
        return actionType is BslGameActionType.Install
            or BslGameActionType.Update
            or BslGameActionType.Predownload
            or BslGameActionType.Repair;
    }

    private async Task ApplyQueueTaskOverlayAsync(BslGameStatusSnapshot snapshot)
    {
        BslBackendTaskItem? task = await RunOnUiThreadAsync(() => FindStatusOverlayTask(snapshot.GameKey));
        if (task is null)
        {
            return;
        }

        snapshot.ActiveTaskAction = task.ActionType;
        snapshot.ActiveTaskState = task.State;
        snapshot.ActiveTaskInstallState = ResolveTaskInstallState(task);
        snapshot.ActiveTaskProgress = task.Progress;
        snapshot.ActiveTaskDetailText = task.DetailText;
        snapshot.ActiveTaskIssueKind = task.IssueKind;
        snapshot.ActiveTaskSuggestedAction = task.SuggestedAction;
        snapshot.ActiveTaskHasResidualCache = task.HasResidualCache;
        snapshot.ActiveTaskResidualCachePath = task.ResidualCachePath;

        bool isForegroundTask = task.State is BslBackendTaskState.Queued or BslBackendTaskState.Running or BslBackendTaskState.Paused;
        bool isRecentTerminalTask = task.State is BslBackendTaskState.Failed or BslBackendTaskState.Canceled
                                    && (task.HasResidualCache || DateTimeOffset.Now - task.UpdatedAt <= TimeSpan.FromMinutes(10));
        if (!isForegroundTask && !isRecentTerminalTask)
        {
            return;
        }

        if (isForegroundTask)
        {
            bool shouldReplaceBusyText = task.State == BslBackendTaskState.Queued || !snapshot.IsBusy;
            snapshot.IsBusy = true;

            if (shouldReplaceBusyText)
            {
                snapshot.StatusText = task.StatusText;
                snapshot.HintText = string.IsNullOrWhiteSpace(task.DetailText)
                    ? BuildQueueTaskHint(task)
                    : task.DetailText;
            }
        }
        else
        {
            snapshot.StatusText = task.StatusText;
            snapshot.HintText = string.IsNullOrWhiteSpace(task.DetailText)
                ? BuildQueueTaskHint(task)
                : task.DetailText;
        }

        if (task.ActionType == BslGameActionType.Predownload)
        {
            snapshot.HasPredownloadTask = true;
            snapshot.PredownloadButtonState = snapshot.ActiveTaskInstallState;
        }
    }

    private static bool IsRetryableIssueKind(BslBackendIssueKind issueKind)
    {
        return issueKind is BslBackendIssueKind.DownloadFailure
            or BslBackendIssueKind.ResourceUnavailable;
    }

    private static void ResetTaskForRetry(BslBackendTaskItem task, string statusText, string? detailText)
    {
        task.State = BslBackendTaskState.Queued;
        task.StatusText = statusText;
        task.DetailText = detailText;
        task.Progress = 0;
        task.InstallState = GameInstallState.Queueing;
        ClearResidualCacheMetadata(task);
        task.IssueKind = BslBackendIssueKind.None;
        task.SuggestedAction = BslBackendSuggestedAction.None;
        task.UpdatedAt = DateTimeOffset.Now;
    }

    private BslBackendTaskItem? FindStatusOverlayTask(string gameKey)
    {
        return Tasks
            .Where(x => string.Equals(x.GameKey, gameKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(GetStatusOverlayPriority)
            .ThenByDescending(x => x.UpdatedAt)
            .FirstOrDefault(x => GetStatusOverlayPriority(x) >= 0);
    }

    private static int GetStatusOverlayPriority(BslBackendTaskItem task)
    {
        return task.State switch
        {
            BslBackendTaskState.Running => 5,
            BslBackendTaskState.Paused => 4,
            BslBackendTaskState.Queued => 3,
            BslBackendTaskState.Failed => 2,
            BslBackendTaskState.Canceled => 1,
            _ => -1,
        };
    }

    private static string BuildQueueTaskHint(BslBackendTaskItem task)
    {
        if (task.State == BslBackendTaskState.Failed)
        {
            return "最近一次任务失败，可在下载队列查看详情或重试。";
        }

        if (task.State == BslBackendTaskState.Canceled)
        {
            return "最近一次任务已取消，可在下载队列重新发起。";
        }

        return task.State switch
        {
            BslBackendTaskState.Queued when task.RetryCount > 0 => $"任务已进入重试队列（{task.RetryCount}/{task.MaxRetryCount}）。",
            BslBackendTaskState.Queued => "任务已加入下载队列，等待开始执行。",
            BslBackendTaskState.Paused => "任务当前已暂停，后续可继续执行。",
            BslBackendTaskState.Running when task.Progress > 0 => $"{task.ActionText}进度 {task.Progress:P0}",
            BslBackendTaskState.Running => $"正在执行{task.ActionText}任务。",
            _ => task.ActionText,
        };
    }

    private static GameInstallState ResolveTaskInstallState(BslBackendTaskItem task)
    {
        if (task.InstallState is not GameInstallState.Stop)
        {
            return task.InstallState;
        }

        if (ContainsAny(task.StatusText, "解压"))
        {
            return GameInstallState.Decompressing;
        }

        if (ContainsAny(task.StatusText, "合并"))
        {
            return GameInstallState.Merging;
        }

        if (ContainsAny(task.StatusText, "校验"))
        {
            return GameInstallState.Verifying;
        }

        if (ContainsAny(task.StatusText, "暂停"))
        {
            return GameInstallState.Paused;
        }

        if (ContainsAny(task.StatusText, "排队", "等待恢复", "已加入队列"))
        {
            return GameInstallState.Queueing;
        }

        if (ContainsAny(task.StatusText, "下载"))
        {
            return GameInstallState.Downloading;
        }

        return task.State switch
        {
            BslBackendTaskState.Queued => GameInstallState.Queueing,
            BslBackendTaskState.Running => GameInstallState.Waiting,
            BslBackendTaskState.Paused => GameInstallState.Paused,
            BslBackendTaskState.Succeeded => GameInstallState.Finish,
            BslBackendTaskState.Failed => GameInstallState.Error,
            _ => GameInstallState.Stop,
        };
    }

    private BslBackendTaskItem? FindDuplicateTask(string gameKey, BslGameActionType actionType)
    {
        return Tasks.FirstOrDefault(x =>
            string.Equals(x.GameKey, gameKey, StringComparison.OrdinalIgnoreCase)
            && (x.ActionType == actionType
                || IsRecoverableQueueAction(x.ActionType) && IsRecoverableQueueAction(actionType))
            && x.State is BslBackendTaskState.Pending
                or BslBackendTaskState.Queued
                or BslBackendTaskState.Running
                or BslBackendTaskState.Paused);
    }

    private static bool ContainsAny(string source, params string[] fragments)
    {
        foreach (string fragment in fragments)
        {
            if (source.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        TaskCompletionSource<bool> tcs = new();
        bool queued = _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!queued)
        {
            action();
            return Task.CompletedTask;
        }

        return tcs.Task;
    }

    private Task<T> RunOnUiThreadAsync<T>(Func<T> func)
    {
        TaskCompletionSource<T> tcs = new();
        bool queued = _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!queued)
        {
            return Task.FromResult(func());
        }

        return tcs.Task;
    }
}

internal enum BslTaskControlIntent
{
    Pause = 1,
    Cancel = 2,
}
