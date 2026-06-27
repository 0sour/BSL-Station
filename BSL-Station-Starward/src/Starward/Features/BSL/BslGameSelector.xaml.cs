using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starward.Core;
using Starward.Features.BSL.Models;
using Starward.Features.BSL.Services;
using Starward.Features.BSL.Backend;
using Starward.Features.GameLauncher;
using Starward.Helpers;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.BSL;

public sealed partial class BslGameSelector : UserControl
{
    public event EventHandler<BslGameSummary>? CurrentGameChanged;

    public ObservableCollection<BslGameSummary> Games { get; } = BslHomeState.Games;

    public ObservableCollection<BslGameSummary> PinnedGames { get; } = BslHomeState.PinnedGames;

    public BslGameSummary CurrentGame => BslHomeState.CurrentGame;

    public bool IsPinned { get; private set; }

    public string? InstalledGamesActualSize { get; private set; } = "0.00GB";

    public string? InstalledGamesSavedSize { get; private set; }

    private bool _isGameDisplayPressed;

    private CancellationTokenSource? _initializeInstalledGamesCancellationTokenSource;


    public BslGameSelector()
    {
        InitializeComponent();
        Loaded += BslGameSelector_Loaded;
        Unloaded += BslGameSelector_Unloaded;
    }


    private void BslGameSelector_Loaded(object sender, RoutedEventArgs e)
    {
        BslHomeState.CurrentGameChanged -= BslHomeState_CurrentGameChanged;
        BslHomeState.CurrentGameChanged += BslHomeState_CurrentGameChanged;
        WeakReferenceMessenger.Default.Unregister<BslGameSelectorPinnedChangedMessage>(this);
        WeakReferenceMessenger.Default.Register<BslGameSelectorPinnedChangedMessage>(this, OnPinnedSettingChanged);

        IsPinned = AppConfig.BslIsGameSelectorPinned;
        if (IsPinned)
        {
            GameIconsAreaVisible = true;
        }

        if (!Games.Any(x => x.IsSelected))
        {
            BslHomeState.SelectGame(Games.First());
        }

        if (PinnedGames.Count == 0 && Games.Count > 0)
        {
            BslHomeState.TogglePinned(Games.First());
        }

        UpdateCurrentGameSelectionState();
        UpdatePinBorderVisibleState();
        _ = InitializeInstalledGamesAsync();
        UpdateBindingsAndNotify();
    }


    private void BslGameSelector_Unloaded(object sender, RoutedEventArgs e)
    {
        _initializeInstalledGamesCancellationTokenSource?.Cancel();
        BslHomeState.CurrentGameChanged -= BslHomeState_CurrentGameChanged;
        WeakReferenceMessenger.Default.Unregister<BslGameSelectorPinnedChangedMessage>(this);
    }


    private void BslHomeState_CurrentGameChanged(object? sender, BslGameSummary game)
    {
        UpdateCurrentGameSelectionState();
        UpdateBindingsAndNotify();
    }


    private void OnPinnedSettingChanged(object _, BslGameSelectorPinnedChangedMessage message)
    {
        IsPinned = message.IsPinned;
        if (IsPinned)
        {
            GameIconsAreaVisible = true;
        }
        else if (!FullBackgroundVisible)
        {
            GameIconsAreaVisible = false;
        }

        Bindings.Update();
    }


    private bool GameIconsAreaVisible
    {
        get => Grid_GameIconsArea.Translation == Vector3.Zero;
        set
        {
            Grid_GameIconsArea.Translation = value ? Vector3.Zero : new Vector3(0, -100, 0);
            UpdateDragRectangles();
        }
    }


    private bool FullBackgroundVisible => Border_FullBackground.Opacity > 0;


    private void UpdateBindingsAndNotify()
    {
        Bindings.Update();
        CurrentGameChanged?.Invoke(this, CurrentGame);
    }


    private void UpdateCurrentGameSelectionState()
    {
        foreach (BslGameSummary game in Games)
        {
            game.IsSelected = ReferenceEquals(game, CurrentGame);
        }
    }


    private void UpdatePinBorderVisibleState()
    {
        bool visible = FullBackgroundVisible;
        Border_Pin.Opacity = visible ? 1 : 0;
        Border_Pin.IsHitTestVisible = visible;
        Border_Pin.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }


    private void UpdateDragRectangles()
    {
        try
        {
            double x = Border_CurrentGameIcon.ActualWidth;
            if (GameIconsAreaVisible)
            {
                x = Border_CurrentGameIcon.ActualWidth + Grid_GameIconsArea.ActualWidth;
            }

            XamlRoot?.SetWindowDragRectangles([new Windows.Foundation.Rect(x, 0, 10000, 48)]);
        }
        catch
        {
        }
    }


    private void Border_CurrentGameIcon_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        GameIconsAreaVisible = true;
    }


    private void Border_CurrentGameIcon_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (FullBackgroundVisible || IsPinned)
        {
            return;
        }

        if (sender is UIElement element)
        {
            var position = e.GetCurrentPoint(element).Position;
            if (position.X > element.ActualSize.X - 1 && position.Y > 0 && position.Y < element.ActualSize.Y)
            {
                return;
            }
        }

        GameIconsAreaVisible = false;
    }


    private void Grid_GameIconsArea_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (FullBackgroundVisible || IsPinned)
        {
            return;
        }

        GameIconsAreaVisible = false;
    }


    private void Grid_GameIconsArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateDragRectangles();
    }


    private void CurrentGameButton_Click(object sender, RoutedEventArgs e)
    {
        ShowFullBackground();
    }


    private void ShowFullBackground()
    {
        Border_FullBackground.Opacity = 1;
        Border_FullBackground.IsHitTestVisible = true;
        Border_FullBackground.Visibility = Visibility.Visible;
        GameIconsAreaVisible = true;
        UpdatePinBorderVisibleState();
    }


    private void HideFullBackground()
    {
        Border_FullBackground.Opacity = 0;
        Border_FullBackground.IsHitTestVisible = false;
        Border_FullBackground.Visibility = Visibility.Collapsed;
        UpdatePinBorderVisibleState();

        if (!IsPinned)
        {
            GameIconsAreaVisible = false;
        }
    }


    private void Border_FullBackground_Tapped(object sender, TappedRoutedEventArgs e)
    {
        _isGameDisplayPressed = false;
        var position = e.GetPosition(sender as UIElement);
        if (position.X <= Border_CurrentGameIcon.ActualWidth && position.Y <= Border_CurrentGameIcon.ActualHeight)
        {
            Border_FullBackground.Opacity = 0;
            Border_FullBackground.IsHitTestVisible = false;
            Border_FullBackground.Visibility = Visibility.Collapsed;
            UpdatePinBorderVisibleState();
            return;
        }

        HideFullBackground();
    }


    private void SelectGame(BslGameSummary game, bool hideBackground, bool doubleTapped = false)
    {
        BslHomeState.SelectGame(game);

        if (hideBackground)
        {
            HideFullBackground();
        }

        if (doubleTapped)
        {
            _isGameDisplayPressed = false;
        }
    }


    private void Grid_GameIcon_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is BslGameSummary game)
        {
            SelectGame(game, hideBackground: false);
        }
    }


    private void Grid_GameIcon_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is BslGameSummary game)
        {
            SelectGame(game, hideBackground: true, doubleTapped: true);
        }
    }


    private void Grid_GameIcon_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is BslGameSummary game && !game.IsSelected)
        {
            game.MaskOpacity = 0;
        }
    }


    private void Grid_GameIcon_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is BslGameSummary game && !game.IsSelected)
        {
            game.MaskOpacity = 1;
        }
    }


    private void Grid_GameDisplay_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            e.Handled = true;
            _isGameDisplayPressed = !_isGameDisplayPressed;
            FlyoutBase.GetAttachedFlyout(element)?.ShowAt(element, new FlyoutShowOptions
            {
                Placement = FlyoutPlacementMode.Bottom,
                ShowMode = FlyoutShowMode.Transient,
            });
        }
    }


    private void Grid_GameDisplay_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && _isGameDisplayPressed)
        {
            FlyoutBase.GetAttachedFlyout(element)?.ShowAt(element, new FlyoutShowOptions
            {
                Placement = FlyoutPlacementMode.Bottom,
                ShowMode = FlyoutShowMode.Transient,
            });
        }
    }


    private void Button_GameEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is BslGameSummary game)
        {
            SelectGame(game, hideBackground: true);
            CloseFirstPopup();
        }
    }


    private void Button_PinGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is BslGameSummary game)
        {
            BslHomeState.TogglePinned(game);
            if (!PinnedGames.Any())
            {
                IsPinned = false;
                if (!FullBackgroundVisible)
                {
                    GameIconsAreaVisible = false;
                }
            }

            UpdateBindingsAndNotify();
        }
    }


    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        IsPinned = !IsPinned;
        AppConfig.BslIsGameSelectorPinned = IsPinned;
        if (IsPinned)
        {
            GameIconsAreaVisible = true;
        }
        else
        {
            if (!FullBackgroundVisible)
            {
                GameIconsAreaVisible = false;
            }
        }

        Bindings.Update();
    }


    private void CloseFirstPopup()
    {
        if (XamlRoot is null)
        {
            return;
        }

        if (VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).FirstOrDefault() is Popup popup)
        {
            popup.IsOpen = false;
        }
    }


    private void TeachTip_ActionButtonClick(TeachingTip sender, object args)
    {
        TeachTip_SelectGame.IsOpen = false;
    }


    [RelayCommand]
    private async Task InitializeInstalledGamesAsync()
    {
        try
        {
            _initializeInstalledGamesCancellationTokenSource?.Cancel();
            _initializeInstalledGamesCancellationTokenSource = new();
            CancellationToken token = _initializeInstalledGamesCancellationTokenSource.Token;

            InstalledGamesActualSize = null;
            InstalledGamesSavedSize = null;
            Bindings.Update();

            List<string> installPaths = [];
            BslBackendService backendService = AppConfig.GetService<BslBackendService>();
            foreach (BslGameSummary game in Games)
            {
                string? installPath = await GetInstallPathAsync(game, backendService, token);
                if (!string.IsNullOrWhiteSpace(installPath) && Directory.Exists(installPath))
                {
                    installPaths.Add(Path.GetFullPath(installPath));
                }
            }

            installPaths = installPaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            long totalSize = await Task.Run(() => installPaths.Sum(path => GetDirectorySize(path, token)), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            InstalledGamesActualSize = BslDownloadHelper.FormatBytes(totalSize);
            InstalledGamesSavedSize = null;
            Bindings.Update();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            InstalledGamesActualSize = "统计失败";
            InstalledGamesSavedSize = null;
            Bindings.Update();
        }
    }


    private static async Task<string?> GetInstallPathAsync(BslGameSummary game, BslBackendService backendService, CancellationToken token)
    {
        GameBiz? gameBiz = game.Id switch
        {
            "genshin" => GameBiz.hk4e_cn,
            "starrail" => GameBiz.hkrpg_cn,
            "zenless" => GameBiz.nap_cn,
            _ => null,
        };

        if (gameBiz is not null)
        {
            return GameLauncherService.GetGameInstallPath(gameBiz.Value);
        }

        if (backendService.GetAdapter(game.Id) is null)
        {
            return null;
        }

        BslGameStatusSnapshot status = await backendService.GetStatusAsync(game.Id, token);
        return status.InstallPath;
    }


    private static long GetDirectorySize(string folder, CancellationToken token)
    {
        long size = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    size += new FileInfo(file).Length;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return size;
    }


    private void Expander_InstalledGamesActualSize_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }
}
