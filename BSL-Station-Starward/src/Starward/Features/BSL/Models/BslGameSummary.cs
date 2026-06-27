using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace Starward.Features.BSL.Models;

public sealed partial class BslGameSummary : ObservableObject
{
    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isPinned;

    [ObservableProperty]
    private double maskOpacity = 1.0;

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Subtitle { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string CapabilitySummary { get; set; } = string.Empty;

    public string BackgroundImage { get; set; } = string.Empty;

    public string PosterImage { get; set; } = string.Empty;

    public string IconImage { get; set; } = string.Empty;

    public string AccentHex { get; set; } = string.Empty;

    public bool CanDownload { get; set; }

    public bool CanUpdate { get; set; }

    public bool CanPredownload { get; set; }

    public bool CanImport { get; set; }

    public bool CanRepair { get; set; }

    public string MainActionText { get; set; } = string.Empty;

    public string MainActionHint { get; set; } = string.Empty;

    public string InfoPillText { get; set; } = string.Empty;

    public string PredownloadText { get; set; } = string.Empty;

    public string FooterLeadText { get; set; } = string.Empty;

    public string FooterLinkText { get; set; } = string.Empty;

    public string FooterLinkTarget { get; set; } = string.Empty;

    public string? Warning { get; set; }

    public ObservableCollection<string> Highlights { get; } = [];

    public ObservableCollection<BslBannerItem> Banners { get; } = [];

    public ObservableCollection<BslPostGroup> PostGroups { get; } = [];

    public ObservableCollection<BslGameSummary> SelectorEntries { get; } = [];


    partial void OnIsSelectedChanged(bool value)
    {
        MaskOpacity = value ? 0 : 1;
    }
}
