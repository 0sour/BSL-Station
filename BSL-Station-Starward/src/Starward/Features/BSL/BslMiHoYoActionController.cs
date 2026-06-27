using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.GameInstall;
using Starward.Features.GameLauncher;
using Starward.Features.Overlay;
using Starward.Features.ViewHost;
using Starward.Helpers;
using Starward.RPC.GameInstall;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Timers;

namespace Starward.Features.BSL;

internal sealed class BslMiHoYoActionController : IDisposable
{
    private readonly ILogger<BslMiHoYoActionController> _logger = AppConfig.GetLogger<BslMiHoYoActionController>();
    private readonly GameLauncherService _gameLauncherService = AppConfig.GetService<GameLauncherService>();
    private readonly GamePackageService _gamePackageService = AppConfig.GetService<GamePackageService>();
    private readonly GameInstallService _gameInstallService = AppConfig.GetService<GameInstallService>();
    private readonly DispatcherQueueTimer _timer;
    private readonly PreDownloadButton _preDownloadButton;
    private readonly StartGameButton _startGameButton;
    private readonly Func<bool> _isCurrent;
    private readonly Action _updateBindings;

    private GameId _gameId;
    private Version? _localGameVersion;
    private Version? _latestGameVersion;
    private Version? _predownloadGameVersion;
    private string? _gameInstallPath;
    private GameInstallContext? _gameInstallTask;
    private Process? _gameProcess;
    private Timer? _processTimer;
    private bool _disposed;
    private int _refreshVersion;

    public BslMiHoYoActionController(
        PreDownloadButton preDownloadButton,
        StartGameButton startGameButton,
        Func<bool> isCurrent,
        Action updateBindings)
    {
        _preDownloadButton = preDownloadButton;
        _startGameButton = startGameButton;
        _isCurrent = isCurrent;
        _updateBindings = updateBindings;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += UpdateGameInstallTaskProgress;
    }

    public bool IsPredownloadFinished { get; private set; }

    public async void SetGame(GameId gameId)
    {
        int version = ++_refreshVersion;
        _gameId = gameId;
        _gameInstallTask = null;
        _timer.Stop();
        HidePredownloadButton();
        _startGameButton.OverrideText = null;
        _startGameButton.RunningGameInfo = null;
        _startGameButton.InstallState = GameInstallState.Stop;
        _updateBindings();

        await RefreshAsync(version);
    }

    public async Task RefreshAsync()
    {
        await RefreshAsync(_refreshVersion);
    }

    private async Task RefreshAsync(int version)
    {
        if (!IsCurrentRefresh(version))
        {
            return;
        }

        try
        {
            _gameInstallTask ??= _gameInstallService.GetGameInstallTask(_gameId);
            if (!IsCurrentRefresh(version))
            {
                return;
            }

            if (_gameInstallTask is not null)
            {
                if (_gameInstallTask.Operation is GameInstallOperation.Predownload)
                {
                    ShowPredownloadButton(false, _gameInstallTask.State);
                }
                else
                {
                    HidePredownloadButton();
                    _startGameButton.GameState = GameState.Installing;
                    _startGameButton.InstallState = _gameInstallTask.State;
                }

                _timer.Start();
                _updateBindings();
                return;
            }

            _gameInstallPath = GameLauncherService.GetGameInstallPath(_gameId, out bool storageRemoved);
            if (!IsCurrentRefresh(version))
            {
                return;
            }

            if (_gameInstallPath is null || storageRemoved)
            {
                _startGameButton.GameState = GameState.InstallGame;
                _startGameButton.RunningGameInfo = null;
                HidePredownloadButton();
                _updateBindings();
                return;
            }

            bool exeExists = await _gameLauncherService.IsGameExeExistsAsync(_gameId, _gameInstallPath);
            _localGameVersion = await _gameLauncherService.GetLocalGameVersionAsync(_gameId, _gameInstallPath);
            if (!IsCurrentRefresh(version))
            {
                return;
            }

            if (exeExists && _localGameVersion is not null)
            {
                _startGameButton.GameState = GameState.StartGame;
            }
            else
            {
                _startGameButton.GameState = GameState.ResumeDownload;
                HidePredownloadButton();
                _updateBindings();
                return;
            }

            await CheckGameRunningAsync(version);
            (_latestGameVersion, _predownloadGameVersion) = await _gameLauncherService.GetLatestGameVersionAsync(_gameId);
            if (!IsCurrentRefresh(version))
            {
                return;
            }

            if (_latestGameVersion > _localGameVersion)
            {
                _startGameButton.GameState = GameState.UpdateGame;
                HidePredownloadButton();
                _updateBindings();
                return;
            }

            if (_predownloadGameVersion > _localGameVersion)
            {
                bool isFinished = await _gamePackageService.CheckPreDownloadFinishedAsync(_gameId);
                if (!IsCurrentRefresh(version))
                {
                    return;
                }

                ShowPredownloadButton(isFinished, isFinished ? GameInstallState.Finish : GameInstallState.Stop);
            }
            else
            {
                HidePredownloadButton();
            }

            _updateBindings();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh miHoYo action cluster failed: {GameBiz}", _gameId?.GameBiz);
        }
    }

    public async Task ExecuteMainActionAsync(XamlRoot xamlRoot)
    {
        if (_gameId is null)
        {
            return;
        }

        switch (_startGameButton.GameState)
        {
            case GameState.StartGame:
                await StartGameAsync();
                break;
            case GameState.InstallGame:
                await InstallGameAsync(xamlRoot);
                break;
            case GameState.UpdateGame:
                await UpdateGameAsync();
                break;
            case GameState.Installing:
                await ChangeGameInstallTaskStateAsync();
                break;
            case GameState.ResumeDownload:
                await ResumeDownloadAsync();
                break;
            case GameState.GameIsRunning:
            case GameState.ComingSoon:
            case GameState.None:
            default:
                break;
        }
    }

    public async Task ExecutePredownloadAsync(XamlRoot xamlRoot)
    {
        try
        {
            if (_gameInstallTask is null)
            {
                await new PreDownloadDialog
                {
                    CurrentGameId = _gameId,
                    XamlRoot = xamlRoot,
                }.ShowAsync();

                _gameInstallTask = _gameInstallService.GetGameInstallTask(_gameId);
                if (_gameInstallTask is not null)
                {
                    _timer.Start();
                    WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(typeof(BslQueuePage)));
                }
                await RefreshAsync();
            }
            else if (_gameInstallTask.Operation is GameInstallOperation.Predownload)
            {
                if (_gameInstallTask.State is GameInstallState.Stop or GameInstallState.Paused or GameInstallState.Error or GameInstallState.Queueing)
                {
                    await _gameInstallService.ContinueTaskAsync(_gameInstallTask);
                }
                else if (_gameInstallTask.State is GameInstallState.Waiting or GameInstallState.Downloading or GameInstallState.Decompressing or GameInstallState.Merging or GameInstallState.Verifying)
                {
                    await _gameInstallService.PauseTaskAsync(_gameInstallTask);
                }

                _timer.Start();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Execute miHoYo predownload action failed: {GameBiz}", _gameId?.GameBiz);
            if (_gameInstallTask?.Operation is GameInstallOperation.Predownload)
            {
                _gameInstallTask.State = GameInstallState.Error;
                _gameInstallTask.ErrorMessage = ex.Message;
            }
        }
    }

    public async Task OpenSettingsAsync(XamlRoot xamlRoot)
    {
        if (_gameId is null)
        {
            return;
        }

        await new GameLauncherSettingDialog
        {
            CurrentGameId = _gameId,
            XamlRoot = xamlRoot,
        }.ShowAsync();

        await RefreshAsync();
    }

    private async Task InstallGameAsync(XamlRoot xamlRoot)
    {
        try
        {
            if (_gameInstallTask is null)
            {
                await new InstallGameDialog
                {
                    CurrentGameId = _gameId,
                    XamlRoot = xamlRoot,
                }.ShowAsync();

                _gameInstallTask = _gameInstallService.GetGameInstallTask(_gameId);
                if (_gameInstallTask is not null)
                {
                    _timer.Start();
                    WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(typeof(BslQueuePage)));
                }
                await RefreshAsync();
            }
            else
            {
                await ChangeGameInstallTaskStateAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Install miHoYo game failed: {GameBiz}", _gameId?.GameBiz);
        }
    }

    private async Task ResumeDownloadAsync()
    {
        try
        {
            if (!Directory.Exists(_gameInstallPath))
            {
                await RefreshAsync();
                return;
            }

            AudioLanguage audio = await _gamePackageService.GetAudioLanguageAsync(_gameId, _gameInstallPath);
            _gameInstallTask = await _gameInstallService.StartInstallAsync(_gameId, _gameInstallPath, audio);
            if (_gameInstallTask is not null)
            {
                _timer.Start();
                WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(typeof(BslQueuePage)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resume miHoYo download failed: {GameBiz}", _gameId?.GameBiz);
        }
    }

    private async Task UpdateGameAsync()
    {
        try
        {
            if (_localGameVersion is not null && _latestGameVersion > _localGameVersion)
            {
                AudioLanguage audio = await _gamePackageService.GetAudioLanguageAsync(_gameId, _gameInstallPath);
                _gameInstallTask = await _gameInstallService.StartUpdateAsync(_gameId, _gameInstallPath!, audio);
                if (_gameInstallTask is not null)
                {
                    _timer.Start();
                    WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(typeof(BslQueuePage)));
                }
            }
            else
            {
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update miHoYo game failed: {GameBiz}", _gameId?.GameBiz);
        }
    }

    private async Task StartGameAsync()
    {
        try
        {
            Process? process = await _gameLauncherService.StartGameAsync(_gameId);
            if (process is not null)
            {
                _startGameButton.GameState = GameState.GameIsRunning;
                SetGameProcess(process);
                WeakReferenceMessenger.Default.Send(new GameStartedMessage());
            }
        }
        catch (FileNotFoundException)
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Start miHoYo game failed: {GameBiz}", _gameId?.GameBiz);
        }
    }

    private async Task<bool> CheckGameRunningAsync(int version)
    {
        try
        {
            Process? process = await _gameLauncherService.GetGameProcessAsync(_gameId);
            if (!IsCurrentRefresh(version))
            {
                return false;
            }

            if (process is not null)
            {
                _startGameButton.GameState = GameState.GameIsRunning;
                SetGameProcess(process);
                return true;
            }
        }
        catch
        {
        }

        SetGameProcess(null);
        return false;
    }

    private void SetGameProcess(Process? process)
    {
        _processTimer?.Stop();
        _gameProcess = process;
        if (process is null)
        {
            _startGameButton.RunningGameInfo = null;
            return;
        }

        _processTimer ??= new Timer(1000);
        _processTimer.Elapsed -= ProcessTimer_Elapsed;
        _processTimer.Elapsed += ProcessTimer_Elapsed;
        _startGameButton.RunningGameInfo = $"{process.ProcessName}.exe ({process.Id})";
        RunningGameService.AddRuninngGame(_gameId.GameBiz, process);
        _processTimer.Start();
    }

    private void ProcessTimer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        try
        {
            if (_gameProcess is not null && _gameProcess.HasExited)
            {
                _processTimer?.Stop();
                _gameProcess = null;
                _ = RefreshAsync();
            }
        }
        catch
        {
        }
    }

    private async Task ChangeGameInstallTaskStateAsync()
    {
        try
        {
            if (_gameInstallTask is null)
            {
                await RefreshAsync();
            }
            else if (_gameInstallTask.Operation is not GameInstallOperation.Predownload)
            {
                if (_gameInstallTask.State is GameInstallState.Stop or GameInstallState.Paused or GameInstallState.Error or GameInstallState.Queueing)
                {
                    await _gameInstallService.ContinueTaskAsync(_gameInstallTask);
                }
                else if (_gameInstallTask.State is GameInstallState.Waiting or GameInstallState.Downloading or GameInstallState.Decompressing or GameInstallState.Merging or GameInstallState.Verifying)
                {
                    await _gameInstallService.PauseTaskAsync(_gameInstallTask);
                }

                _timer.Start();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Change miHoYo install task state failed: {GameBiz}", _gameId?.GameBiz);
        }
    }

    private void UpdateGameInstallTaskProgress(DispatcherQueueTimer sender, object args)
    {
        if (_gameInstallTask is null)
        {
            _timer.Stop();
            return;
        }

        try
        {
            if (_gameInstallTask.Operation is GameInstallOperation.Predownload)
            {
                _preDownloadButton.Visibility = Visibility.Visible;
                IsPredownloadFinished = false;
                _preDownloadButton.UpdateGameInstallTaskState(_gameInstallTask);
                _updateBindings();
            }
            else
            {
                _startGameButton.GameState = GameState.Installing;
                _startGameButton.UpdateGameInstallTaskState(_gameInstallTask);
            }

            if (_gameInstallTask.State is GameInstallState.Error)
            {
                _timer.Stop();
            }
            else if (_gameInstallTask.State is GameInstallState.Stop or GameInstallState.Finish)
            {
                _timer.Stop();
                _gameInstallTask = null;
                _ = RefreshAsync();
            }
        }
        catch
        {
        }
    }

    private void HidePredownloadButton()
    {
        IsPredownloadFinished = false;
        _preDownloadButton.State = GameInstallState.Stop;
        _preDownloadButton.Visibility = Visibility.Collapsed;
    }

    private void ShowPredownloadButton(bool isFinished, GameInstallState state)
    {
        IsPredownloadFinished = isFinished;
        _preDownloadButton.State = state;
        _preDownloadButton.Visibility = Visibility.Visible;
    }

    private bool IsCurrentRefresh(int version)
    {
        return !_disposed && _gameId is not null && _isCurrent() && version == _refreshVersion;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Tick -= UpdateGameInstallTaskProgress;
        _timer.Stop();
        _processTimer?.Stop();
        _processTimer?.Dispose();
        _processTimer = null;
    }
}
