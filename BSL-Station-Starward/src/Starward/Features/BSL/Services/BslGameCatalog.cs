using Starward.Features.BSL.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Starward.Features.BSL.Services;

internal static class BslGameCatalog
{
    public static ObservableCollection<BslGameSummary> CreateGames()
    {
        return
        [
            CreateGenshin(),
            CreateStarRail(),
            CreateZenless(),
            CreateWutheringWaves(),
            CreateArknights(),
            CreateArknightsEndfield(),
        ];
    }

    private static BslGameSummary CreateGenshin()
    {
        return CreateBase(
            id: "genshin",
            name: "原神",
            subtitle: "首发优先级 1",
            description: "沿用 Starward 现成链路，优先完成下载、更新、预下载、修复与启动。",
            status: "完整适配",
            capabilitySummary: "下载 / 更新 / 预下载 / 修复 / 启动",
            backgroundImage: "ms-appx:///Assets/BSL/Games/genshin/bg.webp",
            posterImage: "ms-appx:///Assets/BSL/Games/genshin/poster.png",
            iconImage: "ms-appx:///Assets/BSL/Games/genshin/icon.png",
            accentHex: "#C9AF8B",
            canDownload: true,
            canUpdate: true,
            canPredownload: true,
            canImport: true,
            canRepair: true,
            mainActionText: "安装游戏",
            mainActionHint: "首页保留 Starward 原有交互，能力直接复用现成实现。",
            infoPillText: "国服主链",
            predownloadText: "预下载",
            footerLeadText: "当前状态",
            footerLinkText: "查看任务中心",
            footerLinkTarget: "queue",
            highlights:
            [
                "首页按 Starward 原始结构继续细化。",
                "米哈游三游戏共用主下载与启动链。",
                "状态和版本信息以真实后端结果为准。",
            ],
            banners:
            [
                Banner("Starward 主页参考", "ms-appx:///Assets/BSL/Games/genshin/bg.webp", "https://github.com/Scighost/Starward"),
                Banner("实现链路参考", "ms-appx:///Assets/BSL/Games/genshin/poster.png", "https://github.com/CollapseLauncher/Collapse"),
            ],
            postGroups:
            [
                Group("公告",
                [
                    Post("原神首发按完整适配目标推进。", "2026-06-23", "https://github.com/Scighost/Starward"),
                    Post("下载、更新、预下载优先复用现成能力。", "2026-06-23", "https://github.com/CollapseLauncher/Collapse"),
                ]),
            ]);
    }

    private static BslGameSummary CreateStarRail()
    {
        return CreateBase(
            id: "starrail",
            name: "崩坏：星穹铁道",
            subtitle: "首发优先级 2",
            description: "与原神一样优先走 Starward 主链，保持统一体验。",
            status: "完整适配",
            capabilitySummary: "下载 / 更新 / 预下载 / 修复 / 启动",
            backgroundImage: "ms-appx:///Assets/BSL/Games/starrail/bg.webp",
            posterImage: "ms-appx:///Assets/BSL/Games/starrail/poster.png",
            iconImage: "ms-appx:///Assets/BSL/Games/starrail/icon.png",
            accentHex: "#D5B38A",
            canDownload: true,
            canUpdate: true,
            canPredownload: true,
            canImport: true,
            canRepair: true,
            mainActionText: "安装游戏",
            mainActionHint: "继续直接复用 Starward 的任务链和版本链。",
            infoPillText: "国服主链",
            predownloadText: "预下载",
            footerLeadText: "当前状态",
            footerLinkText: "查看下载队列",
            footerLinkTarget: "queue",
            highlights:
            [
                "统一单任务队列。",
                "版本状态和可用操作按真实后端显示。",
                "不另做一套交互逻辑。",
            ],
            banners:
            [
                Banner("Starward 主页参考", "ms-appx:///Assets/BSL/Games/starrail/bg.webp", "https://github.com/Scighost/Starward"),
                Banner("实现链路参考", "ms-appx:///Assets/BSL/Games/starrail/poster.png", "https://github.com/CollapseLauncher/Collapse"),
            ],
            postGroups:
            [
                Group("公告",
                [
                    Post("星穹铁道与原神共用主任务链。", "2026-06-23", "https://github.com/Scighost/Starward"),
                    Post("首发目标仍是完整适配。", "2026-06-23", "https://github.com/CollapseLauncher/Collapse"),
                ]),
            ]);
    }

    private static BslGameSummary CreateZenless()
    {
        return CreateBase(
            id: "zenless",
            name: "绝区零",
            subtitle: "首发优先级 3",
            description: "保持米哈游三游戏统一首页与统一任务模型。",
            status: "完整适配",
            capabilitySummary: "下载 / 更新 / 预下载 / 修复 / 启动",
            backgroundImage: "ms-appx:///Assets/BSL/Games/zenless/bg.webp",
            posterImage: "ms-appx:///Assets/BSL/Games/zenless/poster.png",
            iconImage: "ms-appx:///Assets/BSL/Games/zenless/icon.png",
            accentHex: "#D8B58E",
            canDownload: true,
            canUpdate: true,
            canPredownload: true,
            canImport: true,
            canRepair: true,
            mainActionText: "安装游戏",
            mainActionHint: "UI 保持 Starward 风格，功能入口直接按真实状态开放。",
            infoPillText: "国服主链",
            predownloadText: "预下载",
            footerLeadText: "当前状态",
            footerLinkText: "查看任务中心",
            footerLinkTarget: "queue",
            highlights:
            [
                "与其他米系游戏共用任务链。",
                "首页只保留统一结构，不另造特殊布局。",
                "以真实后端结果控制按钮状态。",
            ],
            banners:
            [
                Banner("Starward 主页参考", "ms-appx:///Assets/BSL/Games/zenless/bg.webp", "https://github.com/Scighost/Starward"),
                Banner("实现链路参考", "ms-appx:///Assets/BSL/Games/zenless/poster.png", "https://github.com/CollapseLauncher/Collapse"),
            ],
            postGroups:
            [
                Group("公告",
                [
                    Post("绝区零首发按完整适配推进。", "2026-06-23", "https://github.com/Scighost/Starward"),
                    Post("首页仍以 Starward 风格为准。", "2026-06-23", "https://github.com/Scighost/Starward"),
                ]),
            ]);
    }

    private static BslGameSummary CreateWutheringWaves()
    {
        return CreateBase(
            id: "wuthering-waves",
            name: "鸣潮",
            subtitle: "首发优先级 5",
            description: "优先完成导入、启动、版本检测与资源展示，再补下载更新链。",
            status: "部分适配",
            capabilitySummary: "下载 / 更新 / 预下载 / 修复 / 导入 / 启动",
            backgroundImage: "ms-appx:///Assets/BSL/Games/wuwa/bg.jpg",
            posterImage: "ms-appx:///Assets/BSL/Games/wuwa/poster.jpg",
            iconImage: "ms-appx:///Assets/BSL/Games/wuwa/icon.png",
            accentHex: "#8BC5FF",
            canDownload: true,
            canUpdate: true,
            canPredownload: true,
            canImport: true,
            canRepair: true,
            mainActionText: "检查游戏状态",
            mainActionHint: "已接入鸣潮下载、更新、预下载、修复、导入和启动链路。",
            infoPillText: "国服 / BSL 自建队列",
            predownloadText: "预下载",
            footerLeadText: "当前状态",
            footerLinkText: "查看任务中心",
            footerLinkTarget: "queue",
            warning: "大文件下载、更新和预下载链路已接入，仍建议继续做实机验证。",
            highlights:
            [
                "官方 launcher API 已接入下载、公告和背景链路。",
                "下载缓存可在设置页查看和清理。",
                "任务进入 BSL 自建队列，控制项按真实能力显示。",
            ],
            banners:
            [
                Banner("鸣潮", "ms-appx:///Assets/BSL/Games/wuwa/bg.jpg", "https://mc.kurogames.com/"),
                Banner("实现参考", "ms-appx:///Assets/BSL/Games/wuwa/poster.jpg", "https://github.com/JamXi233/WaveTools"),
            ],
            postGroups:
            [
                Group("公告",
                [
                    Post("鸣潮下载、更新、预下载、修复、导入和启动链已接入。", "2026-06-24", "https://github.com/timetetng/wutheringwaves-cli-manager"),
                    Post("任务进入 BSL 自建队列，失败残留缓存可清理。", "2026-06-24", "https://github.com/xiaobai01111/SSMT4-Linux"),
                ]),
            ]);
    }

    private static BslGameSummary CreateArknights()
    {
        return CreateBase(
            id: "arknights",
            name: "明日方舟",
            subtitle: "首发优先级 4",
            description: "已接官方启动器接口，下载、更新、修复、导入、启动和卸载走 BSL 自建队列；预下载 V1 不开放。",
            status: "部分适配",
            capabilitySummary: "下载 / 更新 / 修复 / 导入 / 启动",
            backgroundImage: "ms-appx:///Assets/BSL/Games/arknights/bg.png",
            posterImage: "ms-appx:///Assets/BSL/Games/arknights/poster.png",
            iconImage: "ms-appx:///Assets/BSL/Games/arknights/icon.png",
            accentHex: "#5BC9FF",
            canDownload: true,
            canUpdate: true,
            canPredownload: false,
            canImport: true,
            canRepair: true,
            mainActionText: "安装游戏",
            mainActionHint: "已接官方资源直连和安装检测，真实大文件链路仍需继续实机验证。",
            infoPillText: "官方接口已接入",
            predownloadText: "预下载 V1 不可用",
            footerLeadText: "当前状态",
            footerLinkText: "查看下载队列",
            footerLinkTarget: "queue",
            warning: "预下载当前缺少稳定可复用直连接口，V1 不开放。",
            highlights:
            [
                "公告、Banner 和资源清单走官方接口。",
                "安装、更新、修复、导入、启动和卸载已进入 BSL 后端链路。",
                "安装、更新、修复仍需用真实大文件任务补验证记录。",
            ],
            banners:
            [
                Banner("明日方舟", "ms-appx:///Assets/BSL/Games/arknights/bg.png", "https://ak.hypergryph.com/"),
                Banner("实现参考", "ms-appx:///Assets/BSL/Games/arknights/poster.png", "https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph"),
            ],
            postGroups:
            [
                Group("公告",
                [
                    Post("明日方舟优先按官方接口推进。", "2026-06-23", "https://ak.hypergryph.com/"),
                    Post("下载、更新、修复链继续复用 Hypergryph 方案。", "2026-06-23", "https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph"),
                ]),
            ]);
    }

    private static BslGameSummary CreateArknightsEndfield()
    {
        return CreateBase(
            id: "arknights-endfield",
            name: "明日方舟：终末地",
            subtitle: "首发优先级 6",
            description: "已接 Hypergryph 官方启动器接口，下载、更新、修复、导入、启动和卸载走 BSL 自建队列；预下载 V1 不开放。",
            status: "部分适配",
            capabilitySummary: "下载 / 更新 / 修复 / 导入 / 启动",
            backgroundImage: "ms-appx:///Assets/BSL/Games/endfield/bg.png",
            posterImage: "ms-appx:///Assets/BSL/Games/endfield/poster.png",
            iconImage: "ms-appx:///Assets/BSL/Games/endfield/icon.png",
            accentHex: "#C8C9D3",
            canDownload: true,
            canUpdate: true,
            canPredownload: false,
            canImport: true,
            canRepair: true,
            mainActionText: "安装游戏",
            mainActionHint: "主链已接入，仍需真实大文件安装、更新和修复验证。",
            infoPillText: "官方接口已接入",
            predownloadText: "预下载 V1 不可用",
            footerLeadText: "当前状态",
            footerLinkText: "查看任务中心",
            footerLinkTarget: "queue",
            warning: "官方存在预下载活动，但当前未发现稳定可复用直连接口，V1 不开放。",
            highlights:
            [
                "公告、Banner 和资源清单走官方接口。",
                "安装、更新、修复、导入、启动和卸载已进入 BSL 后端链路。",
                "安装、更新、修复仍需用真实大文件任务补验证记录。",
            ],
            banners:
            [
                Banner("终末地", "ms-appx:///Assets/BSL/Games/endfield/bg.png", "https://endfield.hypergryph.com/"),
                Banner("接口参考", "ms-appx:///Assets/BSL/Games/endfield/poster.png", "https://github.com/MashiroSaber/ak-endfield-api-archive"),
            ],
            postGroups:
            [
                Group("公告",
                [
                    Post("终末地下载、更新、修复、导入、启动和卸载主链已接入。", "2026-06-24", "https://endfield.hypergryph.com/"),
                    Post("预下载 V1 不开放，后续仅在接口稳定后再接入。", "2026-06-24", "https://github.com/MashiroSaber/ak-endfield-api-archive"),
                ]),
            ]);
    }

    private static BslGameSummary CreateBase(
        string id,
        string name,
        string subtitle,
        string description,
        string status,
        string capabilitySummary,
        string backgroundImage,
        string posterImage,
        string iconImage,
        string accentHex,
        bool canDownload,
        bool canUpdate,
        bool canPredownload,
        bool canImport,
        bool canRepair,
        string mainActionText,
        string mainActionHint,
        string infoPillText,
        string predownloadText,
        string footerLeadText,
        string footerLinkText,
        string footerLinkTarget,
        string? warning = null,
        IEnumerable<string>? highlights = null,
        IEnumerable<BslBannerItem>? banners = null,
        IEnumerable<BslPostGroup>? postGroups = null)
    {
        var item = new BslGameSummary
        {
            Id = id,
            Name = name,
            Subtitle = subtitle,
            Description = description,
            Status = status,
            CapabilitySummary = capabilitySummary,
            BackgroundImage = backgroundImage,
            PosterImage = posterImage,
            IconImage = iconImage,
            AccentHex = accentHex,
            CanDownload = canDownload,
            CanUpdate = canUpdate,
            CanPredownload = canPredownload,
            CanImport = canImport,
            CanRepair = canRepair,
            MainActionText = mainActionText,
            MainActionHint = mainActionHint,
            InfoPillText = infoPillText,
            PredownloadText = predownloadText,
            FooterLeadText = footerLeadText,
            FooterLinkText = footerLinkText,
            FooterLinkTarget = footerLinkTarget,
            Warning = warning,
        };

        if (highlights is not null)
        {
            foreach (string line in highlights)
            {
                item.Highlights.Add(line);
            }
        }

        if (banners is not null)
        {
            foreach (BslBannerItem banner in banners)
            {
                item.Banners.Add(banner);
            }
        }

        if (postGroups is not null)
        {
            foreach (BslPostGroup group in postGroups)
            {
                item.PostGroups.Add(group);
            }
        }

        return item;
    }

    private static BslBannerItem Banner(string title, string image, string link)
    {
        return new BslBannerItem
        {
            Title = title,
            Image = image,
            Link = link,
        };
    }

    private static BslPostGroup Group(string header, IEnumerable<BslPostItem> items)
    {
        var group = new BslPostGroup
        {
            Header = header,
        };

        foreach (BslPostItem item in items)
        {
            group.Items.Add(item);
        }

        return group;
    }

    private static BslPostItem Post(string title, string date, string link)
    {
        return new BslPostItem
        {
            Title = title,
            Date = date,
            Link = link,
        };
    }
}
