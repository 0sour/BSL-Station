using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.BSL.Backend;
using Starward.Features.BSL.Models;
using Starward.Features.GameLauncher;
using Starward.Features.PlayTime;
using Starward.Features.ViewHost;
using Starward.RPC.GameInstall;
using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Starward.Features.BSL;

public sealed partial class BslActionCluster : UserControl
{
    private readonly BslBackendService _backendService = AppConfig.GetService<BslBackendService>();
    private readonly BslMiHoYoActionController _miHoYoController;
    private BslGameSummary? _currentGame;
    private BslGameStatusSnapshot? _currentStatus;
    private bool _currentGameIsMiHoYo;
    private int _statusRefreshVersion;

    public bool IsPredownloadFinished { get; private set; }

    public BslActionCluster()
    {
        InitializeComponent();
        Unloaded += BslActionCluster_Unloaded;
        BslMiHoYoActionController? miHoYoController = null;
        miHoYoController = new BslMiHoYoActionController(
            Button_Predownload,
            Button_StartGame,
            () => _currentGameIsMiHoYo,
            () =>
            {
                IsPredownloadFinished = miHoYoController?.IsPredownloadFinished ?? false;
                Bindings.Update();
            });
        _miHoYoController = miHoYoController;
        Button_StartGame.GameCommand = new RelayCommandAdapter(OnMainAction);
        Button_StartGame.SettingCommand = new RelayCommandAdapter(OnSettingsAction);
        Button_Predownload.PredownloadCommand = new RelayCommandAdapter(OnPredownloadAction);
        Button_StartGame.GameState = GameState.StartGame;
        Button_PlayTime.CurrentGameBiz = GameBiz.hk4e_cn;
    }

    public void UpdateContent(BslGameSummary game)
    {
        int version = ++_statusRefreshVersion;
        _currentGame = game;
        _currentGameIsMiHoYo = TryGetMiHoYoGameId(game.Id) is not null;
        IsPredownloadFinished = false;

        Button_Predownload.State = GameInstallState.Stop;
        Button_Predownload.Visibility = Visibility.Collapsed;
        Button_PlayTime.CurrentGameBiz = GuessPlayTimeBiz(game.Id);
        Button_PlayTime.Visibility = _currentGameIsMiHoYo ? Visibility.Visible : Visibility.Collapsed;
        Button_StartGame.RunningGameInfo = game.MainActionHint;
        Grid_Root.Visibility = Visibility.Visible;

        if (TryGetMiHoYoGameId(game.Id) is GameId gameId)
        {
            _currentStatus = null;
            Button_StartGame.OverrideText = null;
            _miHoYoController.SetGame(gameId);
        }
        else
        {
            Button_StartGame.GameState = GameState.StartGame;
            Button_StartGame.OverrideText = "查看详情";
            _ = RefreshStatusAsync(game, version);
        }
    }

    private static GameBiz GuessPlayTimeBiz(string gameId)
    {
        return gameId switch
        {
            "genshin" => GameBiz.hk4e_cn,
            "starrail" => GameBiz.hkrpg_cn,
            "zenless" => GameBiz.nap_cn,
            _ => GameBiz.hk4e_cn,
        };
    }

    private async void OnMainAction()
    {
        if (_currentGame is null)
        {
            return;
        }

        if (_currentGameIsMiHoYo)
        {
            await _miHoYoController.ExecuteMainActionAsync(XamlRoot);
            return;
        }

        string gameKey = _currentGame.Id;
        BslGameActionType action = ResolveMainAction();
        if (action == BslGameActionType.Refresh)
        {
            if (_currentStatus?.IsBusy == true)
            {
                WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(typeof(BslQueuePage)));
            }
            else
            {
                await OpenBslSettingsAsync();
            }

            return;
        }

        if (ShouldShowInstallDialog(gameKey, action))
        {
            await ShowInstallDialogAsync(action);
            return;
        }

        await _backendService.Coordinator.QueueAsync(new BslQueuedActionRequest
        {
            GameKey = gameKey,
            ActionType = action,
        });

        WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(typeof(BslQueuePage)));
    }

    private async void OnSettingsAction()
    {
        if (_currentGameIsMiHoYo)
        {
            await _miHoYoController.OpenSettingsAsync(XamlRoot);
            return;
        }

        if (_currentGame is null)
        {
            return;
        }

        await OpenBslSettingsAsync();
    }


    private async Task OpenBslSettingsAsync()
    {
        if (_currentGame is null)
        {
            return;
        }

        BslGameSettingDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            CurrentGame = _currentGame,
        };
        await dialog.ShowAsync();
        if (dialog.StatusChanged)
        {
            await RefreshStatusAsync(_currentGame, ++_statusRefreshVersion);
        }
    }

    private async void OnPredownloadAction()
    {
        if (_currentGameIsMiHoYo)
        {
            await _miHoYoController.ExecutePredownloadAsync(XamlRoot);
            return;
        }

        if (_currentGame is not null)
        {
            if (ShouldShowInstallDialog(_currentGame.Id, BslGameActionType.Predownload))
            {
                await ShowInstallDialogAsync(BslGameActionType.Predownload);
                return;
            }

            await _backendService.Coordinator.QueueAsync(new BslQueuedActionRequest
            {
                GameKey = _currentGame.Id,
                ActionType = BslGameActionType.Predownload,
            });
        }

        WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(typeof(BslQueuePage)));
    }

    private async Task RefreshStatusAsync(BslGameSummary game, int version)
    {
        if (_backendService.GetAdapter(game.Id) is null)
        {
            if (!IsCurrentRefresh(game, version))
            {
                return;
            }

            Button_StartGame.GameState = GameState.StartGame;
            Button_StartGame.OverrideText = "查看详情";
            Bindings.Update();
            return;
        }

        try
        {
            _currentStatus = await _backendService.GetStatusAsync(game.Id);
        }
        catch
        {
            _currentStatus = null;
        }

        if (!IsCurrentRefresh(game, version))
        {
            return;
        }

        ApplyStatusToButtons();
    }

    private void ApplyStatusToButtons()
    {
        if (_currentStatus is null)
        {
            Button_StartGame.GameState = GameState.StartGame;
            Button_StartGame.OverrideText = "查看详情";
            Bindings.Update();
            return;
        }

        Button_StartGame.GameState = _currentStatus.PrimaryButtonState;
        Button_StartGame.InstallState = _currentStatus.ActiveTaskInstallState;
        Button_StartGame.OverrideText = _currentStatus.PrimaryButtonOverrideText;
        Button_Predownload.State = _currentStatus.PredownloadButtonState;
        Button_Predownload.Visibility = ShouldShowBslPredownloadButton(_currentStatus) ? Visibility.Visible : Visibility.Collapsed;
        IsPredownloadFinished = false;
        Button_StartGame.RunningGameInfo = _currentStatus.HintText;
        Bindings.Update();
    }

    private static bool ShouldShowBslPredownloadButton(BslGameStatusSnapshot status)
    {
        return status.CanPredownload
               || status.HasPredownloadTask
               || status.ActiveTaskAction == BslGameActionType.Predownload;
    }

    private async Task ShowInstallDialogAsync(BslGameActionType action)
    {
        if (_currentGame is null)
        {
            return;
        }

        BslInstallGameDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            CurrentGame = _currentGame,
            ActionType = action,
        };
        await dialog.ShowAsync();
        if (dialog.TaskQueued)
        {
            WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(typeof(BslQueuePage)));
        }
    }

    private static bool ShouldShowInstallDialog(string gameKey, BslGameActionType action)
    {
        if (action is not (BslGameActionType.Install or BslGameActionType.Update or BslGameActionType.Predownload))
        {
            return false;
        }

        return string.Equals(gameKey, "wuthering-waves", StringComparison.OrdinalIgnoreCase)
               || string.Equals(gameKey, "arknights", StringComparison.OrdinalIgnoreCase)
               || string.Equals(gameKey, "arknights-endfield", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCurrentRefresh(BslGameSummary game, int version)
    {
        return version == _statusRefreshVersion && ReferenceEquals(_currentGame, game);
    }

    private BslGameActionType ResolveMainAction()
    {
        if (_currentStatus is null)
        {
            return BslGameActionType.Refresh;
        }

        return _currentStatus.PrimaryAction;
    }

    private static GameId? TryGetMiHoYoGameId(string gameId)
    {
        return gameId switch
        {
            "genshin" => GameId.FromGameBiz(GameBiz.hk4e_cn),
            "starrail" => GameId.FromGameBiz(GameBiz.hkrpg_cn),
            "zenless" => GameId.FromGameBiz(GameBiz.nap_cn),
            _ => null,
        };
    }

    private void BslActionCluster_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _miHoYoController.Dispose();
    }

    private sealed class RelayCommandAdapter : ICommand
    {
        private readonly System.Action _action;

        public RelayCommandAdapter(System.Action action)
        {
            _action = action;
        }

        public event System.EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _action();
    }
}
