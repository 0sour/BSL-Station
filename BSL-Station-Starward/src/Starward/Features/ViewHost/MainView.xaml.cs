using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.BSL;
using Starward.Features.BSL.Models;
using Starward.Features.BSL.Services;
using Starward.Features.Setting;
using System;

namespace Starward.Features.ViewHost;

public sealed partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        Loaded += MainView_Loaded;
        WeakReferenceMessenger.Default.Register<MainViewNavigateMessage>(this, OnMainViewNavigateMessageReceived);
    }


    private void MainView_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyBackgroundContext(BslHomeState.CurrentGame);
        NavigateTo(typeof(BslHomePage));
    }


    private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            NavigateTo(typeof(SettingPage));
            return;
        }

        if (args.InvokedItemContainer is not NavigationViewItem item)
        {
            return;
        }

        Type? page = item.Tag switch
        {
            nameof(BslHomePage) => typeof(BslHomePage),
            nameof(BslQueuePage) => typeof(BslQueuePage),
            _ => null,
        };

        if (page is not null)
        {
            NavigateTo(page);
        }
    }


    private void NavigateTo(Type page)
    {
        MainView_Frame.Navigate(page, null, new SuppressNavigationTransitionInfo());
        MainView_NavigationView.SelectedItem = page.Name switch
        {
            nameof(BslHomePage) => NavigationViewItem_Home,
            nameof(BslQueuePage) => NavigationViewItem_Queue,
            nameof(SettingPage) => MainView_NavigationView.SettingsItem,
            _ => MainView_NavigationView.SelectedItem,
        };
        OverlayMask.Opacity = page == typeof(BslHomePage) ? 0 : 1;
    }


    private void OnMainViewNavigateMessageReceived(object _, MainViewNavigateMessage message)
    {
        NavigateTo(message.Page);
    }


    private void GameSelector_CurrentGameChanged(object sender, BslGameSummary game)
    {
        ApplyBackgroundContext(game);
        if (MainView_Frame.SourcePageType == typeof(BslHomePage))
        {
            MainView_Frame.Navigate(typeof(BslHomePage), null, new SuppressNavigationTransitionInfo());
        }
    }


    private void ApplyBackgroundContext(BslGameSummary game)
    {
        GameId? gameId = game.Id switch
        {
            "genshin" => GameId.FromGameBiz(GameBiz.hk4e_cn),
            "starrail" => GameId.FromGameBiz(GameBiz.hkrpg_cn),
            "zenless" => GameId.FromGameBiz(GameBiz.nap_cn),
            _ => null,
        };

        string? staticBackground = gameId is null && !string.IsNullOrWhiteSpace(game.BackgroundImage)
            ? game.BackgroundImage
            : null;

        AppBackground.SetBackgroundContext(gameId, staticBackground, game.Id);
    }
}
