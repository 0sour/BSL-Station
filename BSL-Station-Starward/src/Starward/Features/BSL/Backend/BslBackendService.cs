using Microsoft.Extensions.Logging;
using Starward.Features.BSL.Backend.Adapters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.BSL.Backend;

internal sealed class BslBackendService
{
    private readonly ILogger<BslBackendService> _logger = AppConfig.GetLogger<BslBackendService>();
    private readonly Dictionary<string, IBslGameAdapter> _adapters;

    public BslBackendService(IEnumerable<IBslGameAdapter> adapters)
    {
        _adapters = adapters.ToDictionary(x => x.GameKey, StringComparer.OrdinalIgnoreCase);
        Coordinator = new BslBackendCoordinator(adapters);
    }

    public BslBackendCoordinator Coordinator { get; }

    public IReadOnlyCollection<IBslGameAdapter> Adapters => _adapters.Values;

    public IBslGameAdapter? GetAdapter(string gameKey)
    {
        _adapters.TryGetValue(gameKey, out IBslGameAdapter? adapter);
        return adapter;
    }

    public Task<bool> CleanupResidualCacheAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        return Coordinator.CleanupResidualCacheAsync(taskId, cancellationToken);
    }

    public Task<BslBackendTaskItem> QueueAsync(BslQueuedActionRequest request, CancellationToken cancellationToken = default)
    {
        return Coordinator.QueueAsync(request, cancellationToken);
    }

    public Task<BslGameLaunchSettingsSnapshot> GetLaunchSettingsAsync(string gameKey, CancellationToken cancellationToken = default)
    {
        return Coordinator.GetLaunchSettingsAsync(gameKey, cancellationToken);
    }

    public Task<BslLaunchSettingsSaveResult> ImportGameAsync(
        string gameKey,
        string? installPath,
        CancellationToken cancellationToken = default)
    {
        return Coordinator.ImportGameAsync(gameKey, installPath, cancellationToken);
    }

    public Task<BslGameStatusSnapshot> GetStatusAsync(string gameKey, CancellationToken cancellationToken = default)
    {
        return Coordinator.GetStatusAsync(gameKey, cancellationToken);
    }

    public async Task<BslGameCacheSnapshot?> GetCacheSnapshotAsync(string gameKey, CancellationToken cancellationToken = default)
    {
        if (GetAdapter(gameKey) is not IBslGameCacheProvider provider)
        {
            return null;
        }

        return await provider.GetCacheSnapshotAsync(cancellationToken);
    }

    public async Task<bool> ClearPredownloadCacheAsync(string gameKey, CancellationToken cancellationToken = default)
    {
        if (GetAdapter(gameKey) is not IBslGameCacheProvider provider)
        {
            return false;
        }

        return await provider.ClearPredownloadCacheAsync(cancellationToken);
    }

    public async Task<BslGamePackageManifest?> GetPackageManifestAsync(string gameKey, CancellationToken cancellationToken = default)
    {
        if (GetAdapter(gameKey) is not IBslGamePackageProvider provider)
        {
            return null;
        }

        return await provider.GetPackageManifestAsync(cancellationToken);
    }

    public Task<BslGameLaunchSettingsSnapshot> SaveLaunchSettingsAsync(
        string gameKey,
        BslGameLaunchSettingsUpdate update,
        CancellationToken cancellationToken = default)
    {
        return Coordinator.SaveLaunchSettingsAsync(gameKey, update, cancellationToken);
    }

    public Task<BslLaunchSettingsSaveResult> SaveLaunchSettingsAndRefreshStatusAsync(
        string gameKey,
        BslGameLaunchSettingsUpdate update,
        CancellationToken cancellationToken = default)
    {
        return Coordinator.SaveLaunchSettingsAndRefreshStatusAsync(gameKey, update, cancellationToken);
    }

    public Task<bool> RemoveTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        return Coordinator.RemoveTaskAsync(taskId, cancellationToken);
    }

    public Task<bool> RetryTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        return Coordinator.RetryTaskAsync(taskId, cancellationToken);
    }

    public Task<bool> PauseTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        return Coordinator.PauseTaskAsync(taskId, cancellationToken);
    }

    public Task<bool> ContinueTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        return Coordinator.ContinueTaskAsync(taskId, cancellationToken);
    }

    public Task<bool> CancelTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        return Coordinator.CancelTaskAsync(taskId, cancellationToken);
    }

    public Task<int> ClearFinishedTasksAsync(CancellationToken cancellationToken = default)
    {
        return Coordinator.ClearFinishedTasksAsync(cancellationToken);
    }
}
