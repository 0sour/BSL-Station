using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.BSL.Backend;

internal interface IBslGameCacheProvider
{
    Task<BslGameCacheSnapshot> GetCacheSnapshotAsync(CancellationToken cancellationToken = default);

    Task<bool> ClearPredownloadCacheAsync(CancellationToken cancellationToken = default);
}
