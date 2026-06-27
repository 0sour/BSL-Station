using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.BSL.Backend;
using Starward.Features.GameInstall;
using Starward.Features.GameLauncher;
using Starward.Frameworks;
using Starward.RPC.GameInstall;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Starward.Features.BSL;

public sealed partial class BslQueuePage : PageBase
{
    private readonly BslBackendService _backendService = AppConfig.GetService<BslBackendService>();
    private readonly GameInstallService _gameInstallService = AppConfig.GetService<GameInstallService>();
    private readonly DispatcherQueueTimer _refreshTimer;

    public BslDownloadCenterItem ActiveTask { get; private set; } = BslDownloadCenterItem.Empty;

    public ObservableCollection<BslDownloadCenterItem> QueuedTasks { get; } = [];

    public ObservableCollection<BslDownloadCenterItem> CompletedTasks { get; } = [];

    public string ActiveCountText { get; private set; } = "0";

    public string QueuedCountText { get; private set; } = "0";

    public string CompletedCountText { get; private set; } = "0";

    public BslQueuePage()
    {
        InitializeComponent();
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(1);
        _refreshTimer.Tick += RefreshTimer_Tick;
    }

    protected override async void OnLoaded()
    {
        _backendService.Coordinator.Tasks.CollectionChanged += Tasks_CollectionChanged;
        await _gameInstallService.SyncGameInstallTasksFromRPCAsync();
        RefreshDownloadCenter();
        _refreshTimer.Start();
    }

    protected override void OnUnloaded()
    {
        _refreshTimer.Stop();
        _backendService.Coordinator.Tasks.CollectionChanged -= Tasks_CollectionChanged;
    }

    private void RefreshTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        RefreshDownloadCenter();
    }

    private void Tasks_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RefreshDownloadCenter();
    }

    private void RefreshDownloadCenter()
    {
        List<BslDownloadCenterItem> nativeItems = GetNativeTasks();
        HashSet<string> activeNativeHistoryIds = nativeItems
            .Select(x => x.NativeHistoryId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<BslDownloadCenterItem> nativeHistoryItems = GetNativeHistoryTasks(activeNativeHistoryIds);
        List<BslDownloadCenterItem> bslItems = _backendService.Coordinator.Tasks
            .Select(BslDownloadCenterItem.FromBslTask)
            .ToList();

        List<BslDownloadCenterItem> activeCandidates = nativeItems
            .Concat(bslItems.Where(x => x.IsActive))
            .OrderByDescending(x => x.IsRunning)
            .ThenBy(x => x.CreatedAt)
            .ToList();

        ActiveTask = activeCandidates.FirstOrDefault() ?? BslDownloadCenterItem.Empty;

        List<BslDownloadCenterItem> queued = nativeItems
            .Concat(bslItems.Where(x => x.IsQueued))
            .Where(x => x.Id != ActiveTask.Id)
            .OrderBy(x => x.CreatedAt)
            .ToList();

        List<BslDownloadCenterItem> completed = nativeHistoryItems
            .Concat(bslItems.Where(x => x.IsCompleted))
            .OrderByDescending(x => x.UpdatedAt)
            .ToList();

        ReplaceItems(QueuedTasks, queued);
        ReplaceItems(CompletedTasks, completed);

        ActiveCountText = ActiveTask.IsEmpty ? "0" : "1";
        QueuedCountText = QueuedTasks.Count.ToString();
        CompletedCountText = CompletedTasks.Count.ToString();

        UpdateVisibility();
        Bindings.Update();
    }

    private List<BslDownloadCenterItem> GetNativeTasks()
    {
        List<BslDownloadCenterItem> items = [];
        AddNativeTask(items, "genshin", "\u539F\u795E", GameBiz.hk4e_cn);
        AddNativeTask(items, "starrail", "\u5D29\u574F\uFF1A\u661F\u7A79\u94C1\u9053", GameBiz.hkrpg_cn);
        AddNativeTask(items, "zenless", "\u7EDD\u533A\u96F6", GameBiz.nap_cn);
        return items;
    }

    private List<BslDownloadCenterItem> GetNativeHistoryTasks(HashSet<string> activeNativeHistoryIds)
    {
        List<BslDownloadCenterItem> items = [];
        foreach (GameInstallHistoryItem history in _gameInstallService.GetGameInstallHistory())
        {
            if (!TryGetNativeGameKey(history.GameBizValue, out string gameKey, out string displayName))
            {
                continue;
            }

            BslDownloadCenterItem item = BslDownloadCenterItem.FromNativeHistory(gameKey, displayName, history);
            if (!activeNativeHistoryIds.Contains(history.Id))
            {
                items.Add(item);
            }
        }

        return items;
    }

    private void AddNativeTask(List<BslDownloadCenterItem> items, string gameKey, string displayName, GameBiz gameBiz)
    {
        GameId? gameId = GameId.FromGameBiz(gameBiz);
        if (gameId is null)
        {
            return;
        }

        GameInstallContext? task = _gameInstallService.GetGameInstallTask(gameId);
        if (task is null)
        {
            return;
        }

        items.Add(BslDownloadCenterItem.FromNativeTask(gameKey, displayName, task));
    }

    private void UpdateVisibility()
    {
        bool hasAny = !ActiveTask.IsEmpty || QueuedTasks.Count > 0 || CompletedTasks.Count > 0;
        AllEmptyStateBorder.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;
        MainScrollViewer.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
        ActiveSection.Visibility = ActiveTask.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        QueuedSection.Visibility = QueuedTasks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompletedSection.Visibility = CompletedTasks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void ReplaceItems(ObservableCollection<BslDownloadCenterItem> target, IReadOnlyList<BslDownloadCenterItem> source)
    {
        target.Clear();
        foreach (BslDownloadCenterItem item in source)
        {
            target.Add(item);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await _gameInstallService.SyncGameInstallTasksFromRPCAsync();
        RefreshDownloadCenter();
    }

    private async void PauseTaskButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteTaskCommandAsync(sender, TaskCenterCommand.Pause);
    }

    private async void ContinueTaskButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteTaskCommandAsync(sender, TaskCenterCommand.Continue);
    }

    private async void StopTaskButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteTaskCommandAsync(sender, TaskCenterCommand.StopOrCancel);
    }

    private async System.Threading.Tasks.Task ExecuteTaskCommandAsync(object sender, TaskCenterCommand command)
    {
        if (sender is not Button button || button.CommandParameter is not BslDownloadCenterItem item)
        {
            return;
        }

        if (item.IsNative)
        {
            await ExecuteNativeTaskCommandAsync(item, command);
        }
        else
        {
            await ExecuteBslTaskCommandAsync(item, command);
        }
    }

    private async System.Threading.Tasks.Task ExecuteNativeTaskCommandAsync(BslDownloadCenterItem item, TaskCenterCommand command)
    {
        GameId? gameId = GetMiHoYoGameId(item.GameKey);
        if (gameId is null)
        {
            return;
        }

        GameInstallContext? task = _gameInstallService.GetGameInstallTask(gameId);
        if (task is null)
        {
            RefreshDownloadCenter();
            return;
        }

        switch (command)
        {
            case TaskCenterCommand.Pause:
                await _gameInstallService.PauseTaskAsync(task);
                break;
            case TaskCenterCommand.Continue:
                await _gameInstallService.ContinueTaskAsync(task);
                break;
            case TaskCenterCommand.StopOrCancel:
                GameInstallContext stoppedTask = await _gameInstallService.StopTaskAsync(task);
                await CleanupCanceledNativeInstallAsync(gameId, stoppedTask);
                break;
        }

        RefreshDownloadCenter();
    }

    private static async System.Threading.Tasks.Task CleanupCanceledNativeInstallAsync(GameId gameId, GameInstallContext task)
    {
        if (task.Operation is not GameInstallOperation.Install
            || task.State is not GameInstallState.Stop
            || !CanDeleteCanceledNativeInstallPath(gameId, task.InstallPath, out string? installPath))
        {
            return;
        }

        if (await TryDeleteDirectoryWithRetryAsync(installPath))
        {
            GameLauncherService.ChangeGameInstallPath(gameId, null);
        }
    }

    private static async System.Threading.Tasks.Task<bool> TryDeleteDirectoryWithRetryAsync(string installPath)
    {
        int[] delays = [0, 300, 900];
        foreach (int delay in delays)
        {
            if (delay > 0)
            {
                await System.Threading.Tasks.Task.Delay(delay);
            }

            if (BslDownloadHelper.DeleteDirectoryIfExists(installPath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CanDeleteCanceledNativeInstallPath(GameId gameId, string? installPath, out string normalizedInstallPath)
    {
        normalizedInstallPath = string.Empty;
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return false;
        }

        try
        {
            normalizedInstallPath = Path.GetFullPath(installPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return false;
        }

        if (!Directory.Exists(normalizedInstallPath) || IsDriveRoot(normalizedInstallPath))
        {
            return false;
        }

        string? folderName = Path.GetFileName(normalizedInstallPath);
        string expectedFolderName = $"{gameId.GameBiz}";
        if (!string.Equals(folderName, expectedFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsSameDirectory(normalizedInstallPath, AppConfig.DefaultGameInstallationPath)
            || ContainsProtectedLocation(normalizedInstallPath, AppConfig.UserDataFolder)
            || ContainsProtectedLocation(normalizedInstallPath, AppContext.BaseDirectory))
        {
            return false;
        }

        return true;
    }

    private static bool IsDriveRoot(string path)
    {
        string normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? root = Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.IsNullOrWhiteSpace(root)
               && string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsProtectedLocation(string installPath, string? protectedPath)
    {
        if (string.IsNullOrWhiteSpace(protectedPath))
        {
            return false;
        }

        try
        {
            string normalizedInstallPath = Path.GetFullPath(installPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedProtectedPath = Path.GetFullPath(protectedPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return normalizedProtectedPath.StartsWith(
                normalizedInstallPath + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSameDirectory(string path, string? otherPath)
    {
        if (string.IsNullOrWhiteSpace(otherPath))
        {
            return false;
        }

        try
        {
            string normalizedPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedOtherPath = Path.GetFullPath(otherPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normalizedPath, normalizedOtherPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async System.Threading.Tasks.Task ExecuteBslTaskCommandAsync(BslDownloadCenterItem item, TaskCenterCommand command)
    {
        if (item.BslTaskId is not Guid taskId || taskId == Guid.Empty)
        {
            return;
        }

        switch (command)
        {
            case TaskCenterCommand.Pause:
                await _backendService.PauseTaskAsync(taskId);
                break;
            case TaskCenterCommand.Continue:
                await _backendService.ContinueTaskAsync(taskId);
                break;
            case TaskCenterCommand.StopOrCancel:
                await _backendService.CancelTaskAsync(taskId);
                break;
        }

        RefreshDownloadCenter();
    }

    private async void RetryTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetBslTaskId(sender, out Guid taskId))
        {
            await _backendService.RetryTaskAsync(taskId);
            RefreshDownloadCenter();
        }
    }

    private async void RemoveTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetBslTaskId(sender, out Guid taskId))
        {
            await _backendService.RemoveTaskAsync(taskId);
            RefreshDownloadCenter();
        }
    }

    private async void CleanupTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetBslTaskId(sender, out Guid taskId))
        {
            await _backendService.CleanupResidualCacheAsync(taskId);
            RefreshDownloadCenter();
        }
    }

    private async void ClearFinishedButton_Click(object sender, RoutedEventArgs e)
    {
        await _backendService.ClearFinishedTasksAsync();
        _gameInstallService.ClearGameInstallHistory();
        RefreshDownloadCenter();
    }

    private static bool TryGetBslTaskId(object sender, out Guid taskId)
    {
        taskId = Guid.Empty;
        if (sender is Button button && button.CommandParameter is BslDownloadCenterItem item && item.BslTaskId is Guid id && id != Guid.Empty)
        {
            taskId = id;
            return true;
        }

        return false;
    }

    private static GameId? GetMiHoYoGameId(string gameKey)
    {
        return gameKey switch
        {
            "genshin" => GameId.FromGameBiz(GameBiz.hk4e_cn),
            "starrail" => GameId.FromGameBiz(GameBiz.hkrpg_cn),
            "zenless" => GameId.FromGameBiz(GameBiz.nap_cn),
            _ => null,
        };
    }

    private static bool TryGetNativeGameKey(GameBiz gameBiz, out string gameKey, out string displayName)
    {
        if (gameBiz == GameBiz.hk4e_cn)
        {
            gameKey = "genshin";
            displayName = "\u539F\u795E";
            return true;
        }

        if (gameBiz == GameBiz.hkrpg_cn)
        {
            gameKey = "starrail";
            displayName = "\u5D29\u574F\uFF1A\u661F\u7A79\u94C1\u9053";
            return true;
        }

        if (gameBiz == GameBiz.nap_cn)
        {
            gameKey = "zenless";
            displayName = "\u7EDD\u533A\u96F6";
            return true;
        }

        gameKey = string.Empty;
        displayName = string.Empty;
        return false;
    }

    private enum TaskCenterCommand
    {
        Pause,
        Continue,
        StopOrCancel,
    }
}

public sealed class BslDownloadCenterItem
{
    private static readonly Regex ByteProgressRegex = new(@"(?<done>\d+(?:\.\d+)?)\s*(?<doneUnit>[KMGT]?B)\s*/\s*(?<total>\d+(?:\.\d+)?)\s*(?<totalUnit>[KMGT]?B)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static BslDownloadCenterItem Empty { get; } = new()
    {
        Id = Guid.Empty,
        DisplayName = string.Empty,
        Subtitle = string.Empty,
        StateText = string.Empty,
        DetailText = string.Empty,
        IsEmpty = true,
    };

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? BslTaskId { get; set; }

    public string? NativeHistoryId { get; set; }

    public string GameKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Subtitle { get; set; } = string.Empty;

    public string StateText { get; set; } = string.Empty;

    public string DetailText { get; set; } = string.Empty;

    public double Progress { get; set; }

    public string DownloadBytesText { get; set; } = string.Empty;

    public string StageProgressText { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public bool IsNative { get; set; }

    public bool IsBsl => !IsNative && BslTaskId is Guid id && id != Guid.Empty;

    public bool IsRunning { get; set; }

    public bool IsQueued { get; set; }

    public bool IsCompleted { get; set; }

    public bool IsActive => IsRunning || IsNative;

    public bool IsEmpty { get; set; }

    public bool CanPause { get; set; }

    public bool CanContinue { get; set; }

    public bool CanStop { get; set; }

    public bool CanRetry { get; set; }

    public bool CanRemove { get; set; }

    public bool CanCleanup { get; set; }

    public string StopButtonText => IsNative ? "停止" : "取消";

    public string ProgressText => string.IsNullOrWhiteSpace(DownloadBytesText) ? $"{Progress:P0}" : DownloadBytesText;

    public static BslDownloadCenterItem FromNativeTask(string gameKey, string displayName, GameInstallContext task)
    {
        return new BslDownloadCenterItem
        {
            Id = DeterministicGuid($"native:{gameKey}"),
            NativeHistoryId = GameInstallHistoryItem.CreateId(task.GameId, task.Operation, task.InstallPath),
            GameKey = gameKey,
            DisplayName = displayName,
            Subtitle = $"米哈游任务 · {GetOperationText(task.Operation)}",
            StateText = GetStateText(task.State),
            DetailText = BuildNativeDetailText(task),
            Progress = GetNativeDownloadProgress(task),
            DownloadBytesText = BuildNativeDownloadBytesText(task),
            StageProgressText = BuildNativeStageProgressText(task),
            IsNative = true,
            IsRunning = task.State is not GameInstallState.Queueing and not GameInstallState.Waiting,
            IsQueued = task.State is GameInstallState.Queueing or GameInstallState.Waiting,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
            CanPause = task.State is GameInstallState.Waiting or GameInstallState.Downloading,
            CanContinue = task.State is GameInstallState.Paused or GameInstallState.Error or GameInstallState.Queueing,
            CanStop = task.State is not GameInstallState.Stop and not GameInstallState.Finish,
        };
    }

    public static BslDownloadCenterItem FromNativeHistory(string gameKey, string displayName, GameInstallHistoryItem history)
    {
        return new BslDownloadCenterItem
        {
            Id = DeterministicGuid($"native-history:{history.Id}"),
            NativeHistoryId = history.Id,
            GameKey = gameKey,
            DisplayName = displayName,
            Subtitle = $"米哈游历史 · {GetOperationText(history.Operation)}",
            StateText = GetStateText(history.State),
            DetailText = BuildNativeHistoryDetailText(history),
            Progress = GetDownloadProgress(history.DownloadFinishBytes, history.DownloadTotalBytes),
            DownloadBytesText = BuildBytesText(history.DownloadFinishBytes, history.DownloadTotalBytes),
            StageProgressText = history.Percent > 0 ? $"{history.Percent:P0}" : string.Empty,
            IsNative = true,
            IsCompleted = true,
            CreatedAt = history.UpdatedAt,
            UpdatedAt = history.UpdatedAt,
        };
    }

    public static BslDownloadCenterItem FromBslTask(BslBackendTaskItem task)
    {
        return new BslDownloadCenterItem
        {
            Id = task.Id,
            BslTaskId = task.Id,
            GameKey = task.GameKey,
            DisplayName = task.DisplayName,
            Subtitle = $"BSL \u961F\u5217 · {task.ActionText}",
            StateText = task.StateText,
            DetailText = string.IsNullOrWhiteSpace(task.DetailText) ? task.StatusText : $"{task.StatusText} · {task.DetailText}",
            Progress = GetBslDownloadProgress(task),
            DownloadBytesText = BuildBslDownloadBytesText(task),
            StageProgressText = $"{Math.Clamp(task.Progress, 0, 1):P0}",
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt,
            CanPause = task.CanPauseFromQueue,
            CanContinue = task.CanContinueFromQueue,
            CanStop = task.CanCancelFromQueue,
            IsRunning = task.State is BslBackendTaskState.Running,
            IsQueued = task.State is BslBackendTaskState.Pending or BslBackendTaskState.Queued or BslBackendTaskState.Paused,
            IsCompleted = task.State is BslBackendTaskState.Succeeded or BslBackendTaskState.Failed or BslBackendTaskState.Canceled,
            CanRetry = task.CanRetryFromQueue,
            CanRemove = task.CanRemoveFromQueue,
            CanCleanup = task.CanCleanupResidualCache,
        };
    }

    private static string GetOperationText(GameInstallOperation operation)
    {
        return operation switch
        {
            GameInstallOperation.Install => "\u5B89\u88C5",
            GameInstallOperation.Predownload => "\u9884\u4E0B\u8F7D",
            GameInstallOperation.Update => "\u66F4\u65B0",
            GameInstallOperation.Repair => "\u4FEE\u590D",
            GameInstallOperation.Uninstall => "\u5378\u8F7D",
            _ => "\u4EFB\u52A1",
        };
    }

    private static string GetStateText(GameInstallState state)
    {
        return state switch
        {
            GameInstallState.Waiting => "\u7B49\u5F85\u4E2D",
            GameInstallState.Downloading => "\u4E0B\u8F7D\u4E2D",
            GameInstallState.Decompressing => "\u89E3\u538B\u4E2D",
            GameInstallState.Merging => "\u5408\u5E76\u4E2D",
            GameInstallState.Verifying => "\u6821\u9A8C\u4E2D",
            GameInstallState.Paused => "\u5DF2\u6682\u505C",
            GameInstallState.Finish => "\u5DF2\u5B8C\u6210",
            GameInstallState.Error => "\u5931\u8D25",
            GameInstallState.Queueing => "\u6392\u961F\u4E2D",
            GameInstallState.Stop => "\u5DF2\u505C\u6B62",
            _ => "\u672A\u77E5",
        };
    }

    private static double GetNativeDownloadProgress(GameInstallContext task)
    {
        if (task.Progress_DownloadTotalBytes > 0)
        {
            return GetDownloadProgress(task.Progress_DownloadFinishBytes, task.Progress_DownloadTotalBytes);
        }

        return task.State is GameInstallState.Finish ? 1 : 0;
    }

    private static string BuildNativeDetailText(GameInstallContext task)
    {
        if (!string.IsNullOrWhiteSpace(task.ErrorMessage)
            && task.State is GameInstallState.Error or GameInstallState.Stop)
        {
            return task.ErrorMessage;
        }

        string bytesText = BuildNativeDownloadBytesText(task);
        if (!string.IsNullOrWhiteSpace(bytesText))
        {
            string speed = task.NetworkDownloadSpeed > 0 ? $" · {BslDownloadHelper.FormatBytes(task.NetworkDownloadSpeed)}/s" : string.Empty;
            string remain = task.RemainTimeSeconds > 0 ? $" · \u5269\u4F59 {TimeSpan.FromSeconds(task.RemainTimeSeconds):hh\\:mm\\:ss}" : string.Empty;
            string stage = BuildNativeStageProgressText(task);
            string stageText = string.IsNullOrWhiteSpace(stage) ? string.Empty : $" · 阶段 {stage}";
            return $"{bytesText}{speed}{remain}{stageText}";
        }

        if (task.State is GameInstallState.Decompressing or GameInstallState.Merging or GameInstallState.Verifying)
        {
            return $"{task.Progress_Percent:P0}";
        }

        return string.IsNullOrWhiteSpace(task.InstallPath) ? GetStateText(task.State) : task.InstallPath;
    }

    private static string BuildNativeHistoryDetailText(GameInstallHistoryItem history)
    {
        if (!string.IsNullOrWhiteSpace(history.ErrorMessage)
            && history.State is GameInstallState.Error or GameInstallState.Stop)
        {
            return history.ErrorMessage;
        }

        string bytesText = BuildBytesText(history.DownloadFinishBytes, history.DownloadTotalBytes);
        if (!string.IsNullOrWhiteSpace(bytesText))
        {
            return bytesText;
        }

        return string.IsNullOrWhiteSpace(history.InstallPath) ? GetStateText(history.State) : history.InstallPath;
    }

    private static string BuildNativeDownloadBytesText(GameInstallContext task)
    {
        return BuildBytesText(task.Progress_DownloadFinishBytes, task.Progress_DownloadTotalBytes);
    }

    private static string BuildNativeStageProgressText(GameInstallContext task)
    {
        if (task.State is GameInstallState.Decompressing or GameInstallState.Merging or GameInstallState.Verifying)
        {
            return $"{Math.Clamp(task.Progress_Percent, 0, 1):P0}";
        }

        if (task.Progress_WriteTotalBytes > 0 && task.State is GameInstallState.Downloading)
        {
            return $"{GetDownloadProgress(task.Progress_WriteFinishBytes, task.Progress_WriteTotalBytes):P0}";
        }

        return string.Empty;
    }

    private static double GetBslDownloadProgress(BslBackendTaskItem task)
    {
        return TryExtractByteProgress(task.DetailText, out long done, out long total)
            ? GetDownloadProgress(done, total)
            : Math.Clamp(task.Progress, 0, 1);
    }

    private static string BuildBslDownloadBytesText(BslBackendTaskItem task)
    {
        return TryExtractByteProgress(task.DetailText, out long done, out long total)
            ? BuildBytesText(done, total)
            : string.Empty;
    }

    private static bool TryExtractByteProgress(string? text, out long done, out long total)
    {
        done = 0;
        total = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Match match = ByteProgressRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        done = ParseBytes(match.Groups["done"].Value, match.Groups["doneUnit"].Value);
        total = ParseBytes(match.Groups["total"].Value, match.Groups["totalUnit"].Value);
        return total > 0;
    }

    private static long ParseBytes(string value, string unit)
    {
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number))
        {
            return 0;
        }

        double multiplier = unit.ToUpperInvariant() switch
        {
            "KB" => 1L << 10,
            "MB" => 1L << 20,
            "GB" => 1L << 30,
            "TB" => 1L << 40,
            _ => 1,
        };

        return (long)Math.Round(number * multiplier);
    }

    private static string BuildBytesText(long done, long total)
    {
        if (total <= 0)
        {
            return string.Empty;
        }

        return $"{BslDownloadHelper.FormatBytes(Math.Clamp(done, 0, total))} / {BslDownloadHelper.FormatBytes(total)}";
    }

    private static double GetDownloadProgress(long done, long total)
    {
        if (total <= 0)
        {
            return 0;
        }

        return Math.Clamp((double)Math.Clamp(done, 0, total) / total, 0, 1);
    }

    private static Guid DeterministicGuid(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        byte[] hash = MD5.HashData(bytes);
        return new Guid(hash);
    }
}
