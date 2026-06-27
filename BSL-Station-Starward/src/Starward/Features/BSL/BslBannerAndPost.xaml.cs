using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starward.Features.BSL.Models;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;

namespace Starward.Features.BSL;

public sealed partial class BslBannerAndPost : UserControl
{
    private const double DefaultPanelHeight = 304;
    private const double DefaultBannerHeight = 176;
    private const double WideBannerPanelHeight = 342;
    private const double WideBannerHeight = 214;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer _bannerTimer;

    public ObservableCollection<BslBannerItem> Banners { get; } = [];

    public ObservableCollection<BslPostGroup> PostGroups { get; } = [];

    public BslBannerAndPost()
    {
        InitializeComponent();
        Loaded += BslBannerAndPost_Loaded;
        Unloaded += BslBannerAndPost_Unloaded;
        _bannerTimer = DispatcherQueue.CreateTimer();
        _bannerTimer.Interval = TimeSpan.FromSeconds(5);
        _bannerTimer.IsRepeating = true;
        _bannerTimer.Tick += BannerTimer_Tick;
    }


    public void UpdateContent(BslGameSummary game)
    {
        ApplyLayoutForGame(game);
        Banners.Clear();
        PostGroups.Clear();

        foreach (BslBannerItem item in game.Banners)
        {
            Banners.Add(item);
        }

        foreach (BslPostGroup group in game.PostGroups)
        {
            PostGroups.Add(group);
        }

        FlipView_Banner.SelectedIndex = 0;
        UpdateBannerIndexText();
        Opacity = Banners.Count > 0 || PostGroups.Count > 0 ? 1 : 0;
        IsHitTestVisible = Opacity > 0;
    }


    private void ApplyLayoutForGame(BslGameSummary game)
    {
        bool useWideBanner = IsWideBannerGame(game.Id);
        Grid_BannerAndPost.Height = useWideBanner ? WideBannerPanelHeight : DefaultPanelHeight;
        Row_Banner.Height = new GridLength(useWideBanner ? WideBannerHeight : DefaultBannerHeight);
    }


    private static bool IsWideBannerGame(string gameId)
    {
        return string.Equals(gameId, "wuthering-waves", StringComparison.OrdinalIgnoreCase)
               || string.Equals(gameId, "arknights", StringComparison.OrdinalIgnoreCase)
               || string.Equals(gameId, "arknights-endfield", StringComparison.OrdinalIgnoreCase);
    }


    private void BslBannerAndPost_Loaded(object sender, RoutedEventArgs e)
    {
        if (Banners.Count > 1)
        {
            _bannerTimer.Start();
        }

        UpdateBannerIndexText();
    }


    private void BslBannerAndPost_Unloaded(object sender, RoutedEventArgs e)
    {
        _bannerTimer.Stop();
        _bannerTimer.Tick -= BannerTimer_Tick;
    }


    private void BannerTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (Banners.Count <= 1)
        {
            return;
        }

        FlipView_Banner.SelectedIndex = (FlipView_Banner.SelectedIndex + 1) % Banners.Count;
        UpdateBannerIndexText();
    }


    private void FlipView_Banner_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateBannerIndexText();
    }


    private void Grid_BannerContainer_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _bannerTimer.Stop();
        Border_PipsPager.Visibility = Visibility.Visible;
    }


    private void Grid_BannerContainer_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (Banners.Count > 1)
        {
            _bannerTimer.Start();
        }

        Border_PipsPager.Visibility = Visibility.Collapsed;
    }


    private void BannerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is BslBannerItem banner)
        {
            OpenLink(banner.Link);
        }
    }


    private void PostButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is BslPostItem post)
        {
            OpenLink(post.Link);
        }
    }


    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        string? link = Banners.FirstOrDefault()?.Link ?? PostGroups.FirstOrDefault()?.Items.FirstOrDefault()?.Link;
        if (!string.IsNullOrWhiteSpace(link))
        {
            OpenLink(link);
        }
    }


    private static void OpenLink(string link)
    {
        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch { }
    }

    private void UpdateBannerIndexText()
    {
        int count = Math.Max(Banners.Count, 1);
        int index = Math.Clamp(FlipView_Banner.SelectedIndex, 0, count - 1) + 1;
        TextBlock_BannerIndex.Text = $"{index}/{count}";
    }
}
