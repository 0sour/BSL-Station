using System.Collections.Generic;

namespace Starward.Features.BSL.Backend;

public sealed class BslGamePackageManifest
{
    public string GameKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? LatestVersion { get; set; }

    public string? PredownloadVersion { get; set; }

    public List<BslGamePackageGroup> LatestPackageGroups { get; set; } = [];

    public List<BslGamePackageGroup> PredownloadPackageGroups { get; set; } = [];
}

public sealed class BslGamePackageGroup
{
    public string Name { get; set; } = string.Empty;

    public List<BslGamePackageItem> Items { get; set; } = [];
}

public sealed class BslGamePackageItem
{
    public string FileName { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Md5 { get; set; } = string.Empty;

    public long PackageSize { get; set; }

    public string PackageSizeString => BslDownloadHelper.FormatBytes(PackageSize);
}
