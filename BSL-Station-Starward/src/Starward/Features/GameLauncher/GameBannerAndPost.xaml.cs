using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starward.Core.HoYoPlay;
using Starward.Features.HoYoPlay;
using Starward.Features.ViewHost;
using Starward.Helpers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Vanara.PInvoke;
using Windows.System;

namespace Starward.Features.GameLauncher;

[INotifyPropertyChanged]
public sealed partial class GameBannerAndPost : UserControl
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _bannerTimer;

    private bool _isLoaded;

    private readonly ILogger<GameBannerAndPost> _logger = AppConfig.GetLogger<GameBannerAndPost>();

    private readonly HoYoPlayService _hoYoPlayService = AppConfig.GetService<HoYoPlayService>();

    private readonly GameNoticeService _gameNoticeService = AppConfig.GetService<GameNoticeService>();

    public GameId? CurrentGameId { get; set; }

    public GameBannerAndPost()
    {
        InitializeComponent();
        Loaded += GameBannerAndPost_Loaded;
        Unloaded += GameBannerAndPost_Unloaded;
        _bannerTimer = DispatcherQueue.CreateTimer();
        _bannerTimer.Interval = TimeSpan.FromSeconds(5);
        _bannerTimer.IsRepeating = true;
        _bannerTimer.Tick += BannerTimer_Tick;
    }

    private async void GameBannerAndPost_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        WeakReferenceMessenger.Default.Register<MainWindowStateChangedMessage>(this, OnMainWindowStateChanged);
        WeakReferenceMessenger.Default.Register<GameNoticeWindowClosedMessage>(this, OnGameNoticeWindowClosed);
        WeakReferenceMessenger.Default.Register<GameAnnouncementSettingChangedMessage>(this, OnGameAnnouncementSettingChanged);
        await RefreshAsync(CurrentGameId);
    }

    private void GameBannerAndPost_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _bannerTimer.Stop();
        _bannerTimer.Tick -= BannerTimer_Tick;
        Banners = null;
        PostGroups = null;
    }

    private void OnMainWindowStateChanged(object _, MainWindowStateChangedMessage message)
    {
        try
        {
            if (message.Activate)
            {
                _bannerTimer.Start();
            }
            else if (message.Hide || message.SessionLock)
            {
                _bannerTimer.Stop();
            }
        }
        catch
        {
        }
    }

    private async void OnGameNoticeWindowClosed(object _, GameNoticeWindowClosedMessage message)
    {
        User32.SetForegroundWindow(XamlRoot.GetWindowHandle());
        await UpdateGameNoticeAlertAsync();
    }

    private async void OnGameAnnouncementSettingChanged(object _, GameAnnouncementSettingChangedMessage message)
    {
        if (CurrentGameId is null)
        {
            ClearContent();
            return;
        }

        if (AppConfig.EnableBannerAndPost)
        {
            ShowBannerAndPost = true;
            await UpdateGameContentAsync();
            if (AppConfig.DisableGameNoticeRedHot)
            {
                IsGameNoticesAlert = false;
            }
            else
            {
                await UpdateGameNoticeAlertAsync();
            }
        }
        else
        {
            ShowBannerAndPost = false;
        }
    }

    public List<GameBanner>? Banners { get; set => SetProperty(ref field, value); }

    public List<GamePostGroup>? PostGroups { get; set => SetProperty(ref field, value); }

    public bool IsGameNoticesAlert { get; set => SetProperty(ref field, value); }

    public bool ShowBannerAndPost
    {
        get => Opacity == 1;
        set
        {
            if (value && Banners?.Count > 0 && PostGroups?.Count > 0)
            {
                _bannerTimer.Start();
                Opacity = 1;
                IsHitTestVisible = true;
            }
            else
            {
                _bannerTimer.Stop();
                Opacity = 0;
                IsHitTestVisible = false;
            }
        }
    }

    public async Task RefreshAsync(GameId? gameId)
    {
        CurrentGameId = gameId;

        if (!_isLoaded)
        {
            return;
        }

        if (CurrentGameId is null)
        {
            ClearContent();
            return;
        }

        await UpdateGameContentAsync();
        await UpdateGameNoticeAlertAsync();
    }

    private async Task UpdateGameContentAsync()
    {
        try
        {
            if (CurrentGameId is null)
            {
                ClearContent();
                return;
            }

            var content = await _hoYoPlayService.GetGameContentAsync(CurrentGameId);
            if (content is null || !AppConfig.EnableBannerAndPost)
            {
                ShowBannerAndPost = false;
                return;
            }

            Banners = content.Banners;
            PostGroups = GamePostGroup.FromGameContent(content);
            ShowBannerAndPost = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get game launcher content ({CurrentGameId})", CurrentGameId);
        }
    }

    private async Task UpdateGameNoticeAlertAsync()
    {
        try
        {
            if (CurrentGameId is null)
            {
                Button_InGameNotices.Visibility = Visibility.Collapsed;
                IsGameNoticesAlert = false;
                return;
            }

            if (GameFeatureConfig.FromGameId(CurrentGameId).InGameNoticesWindow)
            {
                Button_InGameNotices.Visibility = Visibility.Visible;
            }
            else
            {
                Button_InGameNotices.Visibility = Visibility.Collapsed;
                return;
            }

            if (AppConfig.DisableGameNoticeRedHot)
            {
                IsGameNoticesAlert = false;
            }
            else
            {
                IsGameNoticesAlert = await _gameNoticeService.IsNoticeAlertAsync(CurrentGameId.GameBiz);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get game launcher content ({CurrentGameId})", CurrentGameId);
        }
    }

    private void BannerTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        try
        {
            if (Banners?.Count > 0)
            {
                FlipView_Banner.SelectedIndex = (FlipView_Banner.SelectedIndex + 1) % Banners.Count;
            }
        }
        catch
        {
        }
    }

    private async void Image_Banner_Tapped(object sender, TappedRoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement fe && fe.DataContext is GameBanner banner)
            {
                await Launcher.LaunchUriAsync(new Uri(banner.Image.Link));
            }
        }
        catch
        {
        }
    }

    private void FlipView_Banner_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var grid = VisualTreeHelper.GetChild(FlipView_Banner, 0);
            if (grid != null)
            {
                var count = VisualTreeHelper.GetChildrenCount(grid);
                if (count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        var child = VisualTreeHelper.GetChild(grid, i);
                        if (child is Button button)
                        {
                            button.IsHitTestVisible = false;
                            button.Opacity = 0;
                        }
                    }
                }
            }
        }
        catch
        {
        }
    }

    private void Grid_BannerContainer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _bannerTimer.Stop();
        Border_PipsPager.Visibility = Visibility.Visible;
    }

    private void Grid_BannerContainer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _bannerTimer.Start();
        Border_PipsPager.Visibility = Visibility.Collapsed;
    }

    [RelayCommand]
    private void OpenGameNoticeWindow()
    {
        try
        {
            if (CurrentGameId is null)
            {
                return;
            }

            new GameNoticeWindow
            {
                CurrentGameBiz = CurrentGameId.GameBiz,
                ParentWindowHandle = (nint)XamlRoot.ContentIslandEnvironment.AppWindowId.Value
            }.Activate();
        }
        catch
        {
        }
    }

    public static string AddOne(int number)
    {
        return (number + 1).ToString();
    }

    private void ClearContent()
    {
        _bannerTimer.Stop();
        Banners = null;
        PostGroups = null;
        IsGameNoticesAlert = false;
        Button_InGameNotices.Visibility = Visibility.Collapsed;
        ShowBannerAndPost = false;
    }
}
