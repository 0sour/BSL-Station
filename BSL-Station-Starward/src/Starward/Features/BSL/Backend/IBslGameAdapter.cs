using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.BSL.Backend;

internal interface IBslGameAdapter
{
    string GameKey { get; }

    string DisplayName { get; }

    BslGameSupportLevel SupportLevel { get; }

    BslGameServerRegion Region { get; }

    BslGameCapability Capabilities { get; }

    Task<BslGameStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BslBannerEntry>> GetBannersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BslNoticeEntry>> GetNoticesAsync(CancellationToken cancellationToken = default);

    Task<BslBackendTaskItem> ExecuteAsync(BslQueuedActionRequest request, CancellationToken cancellationToken = default);

    Task<BslGameLaunchSettingsSnapshot> GetLaunchSettingsAsync(CancellationToken cancellationToken = default);

    Task<BslLaunchSettingsSaveResult> ImportGameAsync(string? installPath, CancellationToken cancellationToken = default);

    Task SaveLaunchSettingsAsync(BslGameLaunchSettingsUpdate update, CancellationToken cancellationToken = default);

    Task<string?> NormalizeInstallPathAsync(string? installPath, CancellationToken cancellationToken = default);
}
