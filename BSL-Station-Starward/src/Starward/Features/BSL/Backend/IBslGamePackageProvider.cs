using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.BSL.Backend;

internal interface IBslGamePackageProvider
{
    Task<BslGamePackageManifest> GetPackageManifestAsync(CancellationToken cancellationToken = default);
}
