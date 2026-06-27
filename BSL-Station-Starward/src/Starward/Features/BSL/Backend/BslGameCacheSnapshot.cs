namespace Starward.Features.BSL.Backend;

public sealed class BslGameCacheSnapshot
{
    public string GameKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool HasPredownloadCache { get; set; }

    public string? PredownloadVersion { get; set; }

    public string? PredownloadCachePath { get; set; }

    public long PredownloadCacheSize { get; set; }

    public string PredownloadStatusText => HasPredownloadCache
        ? $"已保留下载缓存 {PredownloadVersion ?? "未知版本"}"
        : "暂无下载缓存";

    public string PredownloadCacheSizeText => BslDownloadHelper.FormatBytes(PredownloadCacheSize);
}
