using Microsoft.UI.Xaml;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.BSL.Models;
using Starward.Features.BSL.Services;
using Starward.Frameworks;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.BSL;

public sealed partial class BslHomePage : PageBase
{
    private readonly BslHomeBackendBridge _backendBridge = new();
    private CancellationTokenSource? _refreshCts;

    public BslHomePage()
    {
        InitializeComponent();
        Loaded += BslHomePage_Loaded;
        Unloaded += BslHomePage_Unloaded;
    }


    private void BslHomePage_Loaded(object sender, RoutedEventArgs e)
    {
        BslHomeState.CurrentGameChanged += BslHomeState_CurrentGameChanged;
        LoadGame(BslHomeState.CurrentGame);
    }


    private void BslHomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        BslHomeState.CurrentGameChanged -= BslHomeState_CurrentGameChanged;
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
    }


    private void BslHomeState_CurrentGameChanged(object? sender, BslGameSummary game)
    {
        _ = LoadGameAsync(game);
    }


    private async void LoadGame(BslGameSummary game)
    {
        await LoadGameAsync(game);
    }


    private async Task LoadGameAsync(BslGameSummary game)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _refreshCts.Token;

        GameId? gameId = GetMiHoYoGameId(game);
        bool isMiHoYo = gameId is not null;

        ApplyGameContent(game, gameId, isMiHoYo);

        try
        {
            await _backendBridge.RefreshGameAsync(game, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
        }

        if (cancellationToken.IsCancellationRequested || !ReferenceEquals(BslHomeState.CurrentGame, game))
        {
            return;
        }

        if (!isMiHoYo)
        {
            GameBannerAndPost.UpdateContent(game);
        }
    }


    private void ApplyGameContent(BslGameSummary game, GameId? gameId, bool isMiHoYo)
    {
        GameBannerAndPost.Visibility = isMiHoYo ? Visibility.Collapsed : Visibility.Visible;
        GameBannerAndPost.IsHitTestVisible = !isMiHoYo;
        if (!isMiHoYo)
        {
            GameBannerAndPost.UpdateContent(game);
        }

        MiHoYoBannerAndPost.Visibility = isMiHoYo ? Visibility.Visible : Visibility.Collapsed;
        MiHoYoBannerAndPost.IsHitTestVisible = isMiHoYo;
        if (isMiHoYo)
        {
            _ = MiHoYoBannerAndPost.RefreshAsync(gameId);
        }

        ActionCluster.UpdateContent(game);
    }


    private static GameId? GetMiHoYoGameId(BslGameSummary game)
    {
        return game.Id switch
        {
            "genshin" => GameId.FromGameBiz(GameBiz.hk4e_cn),
            "starrail" => GameId.FromGameBiz(GameBiz.hkrpg_cn),
            "zenless" => GameId.FromGameBiz(GameBiz.nap_cn),
            _ => null,
        };
    }
}
