using Starward.Features.BSL.Backend;
using Starward.Features.BSL.Models;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.BSL.Services;

internal sealed class BslHomeBackendBridge
{
    private readonly BslBackendService _backendService = AppConfig.GetService<BslBackendService>();

    public async Task RefreshGameAsync(BslGameSummary game, CancellationToken cancellationToken = default)
    {
        if (_backendService.GetAdapter(game.Id) is null)
        {
            return;
        }

        BslGameStatusSnapshot status = await _backendService.GetStatusAsync(game.Id, cancellationToken);
        game.Status = status.SupportLevelText;
        game.CapabilitySummary = status.CapabilitiesText;
        game.MainActionHint = BuildMainActionHint(status);
        game.InfoPillText = $"{status.RegionText} / {status.StatusText}";
        game.PredownloadText = status.PredownloadText;
        game.Warning = status.HasWarnings ? status.WarningText : null;

        if (status.Region != BslGameServerRegion.China)
        {
            game.Warning = string.IsNullOrWhiteSpace(game.Warning)
                ? "当前首页先按国服方案落地，其他区服后续单独扩展。"
                : $"{game.Warning}；当前首页先按国服方案落地，其他区服后续单独扩展。";
        }

        IBslGameAdapter adapter = _backendService.GetAdapter(game.Id)!;
        var banners = await adapter.GetBannersAsync(cancellationToken);
        if (banners.Count > 0)
        {
            game.Banners.Clear();
            foreach (BslBannerEntry banner in banners)
            {
                if (!string.IsNullOrWhiteSpace(banner.ImageUrl))
                {
                    game.Banners.Add(new BslBannerItem
                    {
                        Title = banner.Title,
                        Image = banner.ImageUrl,
                        Link = banner.Link,
                    });
                }
            }
        }

        var notices = await adapter.GetNoticesAsync(cancellationToken);
        if (notices.Count > 0)
        {
            game.PostGroups.Clear();
            foreach (var group in notices
                         .Where(x => !string.IsNullOrWhiteSpace(x.Title))
                         .GroupBy(x => string.IsNullOrWhiteSpace(x.Category) ? "公告" : x.Category)
                         .Take(3))
            {
                BslPostGroup postGroup = new() { Header = group.Key };
                foreach (BslNoticeEntry item in group.Take(8))
                {
                    postGroup.Items.Add(new BslPostItem
                    {
                        Title = item.Title,
                        Date = item.DateText,
                        Link = item.Link,
                    });
                }

                if (postGroup.Items.Count > 0)
                {
                    game.PostGroups.Add(postGroup);
                }
            }
        }
    }

    private static string BuildMainActionHint(BslGameStatusSnapshot status)
    {
        if (!string.IsNullOrWhiteSpace(status.HintText))
        {
            return status.HintText;
        }

        if (status.CanInstall)
        {
            return "可直接进入安装链路。";
        }

        if (status.CanUpdate)
        {
            return "检测到新版本，可直接更新。";
        }

        if (status.CanLaunch)
        {
            return "已检测到安装目录，可直接启动。";
        }

        return "当前先保留已验证能力入口。";
    }
}
