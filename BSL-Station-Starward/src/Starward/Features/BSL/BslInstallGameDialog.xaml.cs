using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starward.Features.BSL.Backend;
using Starward.Features.BSL.Models;
using Starward.Features.ViewHost;
using Starward.Helpers;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Starward.Features.BSL;

[INotifyPropertyChanged]
public sealed partial class BslInstallGameDialog : ContentDialog
{
    private const string WutheringWavesGameKey = "wuthering-waves";
    private const string WutheringWavesFolderName = "Wuthering Waves Game";

    private readonly ILogger<BslInstallGameDialog> _logger = AppConfig.GetLogger<BslInstallGameDialog>();
    private readonly BslBackendService _backendService = AppConfig.GetService<BslBackendService>();

    private string? _selectedBasePath;
    private bool _isApplyingPath;
    private bool _requiresExistingInstallPath;

    public BslInstallGameDialog()
    {
        InitializeComponent();
        Loaded += BslInstallGameDialog_Loaded;
    }

    public BslGameSummary? CurrentGame { get; set; }

    public BslGameActionType ActionType { get; set; }

    public bool TaskQueued { get; private set; }

    public string DialogTitle { get; private set => SetProperty(ref field, value); } = "安装游戏";

    public string StartButtonText { get; private set => SetProperty(ref field, value); } = "开始";

    public string InstallationPath { get; private set => SetProperty(ref field, value); } = string.Empty;

    public string VersionText { get; private set => SetProperty(ref field, value); } = "读取中...";

    public string PackageSizeText { get; private set => SetProperty(ref field, value); } = "读取中...";

    public string RequiredSpaceText { get; private set => SetProperty(ref field, value); } = "读取中...";

    public string AvailableSpaceText { get; private set => SetProperty(ref field, value); } = "读取中...";

    public string? HintText { get; private set => SetProperty(ref field, value); }

    public string? ErrorMessage { get; private set => SetProperty(ref field, value); }

    public bool CanStartAction { get; private set => SetProperty(ref field, value); }

    public bool CanChangeInstallationPath => ActionType is BslGameActionType.Install;

    public Visibility AutoCreateSubfolderVisibility => ActionType is BslGameActionType.Install ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PackageInfoVisibility => string.IsNullOrWhiteSpace(ErrorMessage) || PackageSizeBytes > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HintVisibility => string.IsNullOrWhiteSpace(HintText) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ErrorVisibility => string.IsNullOrWhiteSpace(ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;

    public bool AutomaticallyCreateSubfolderForInstall
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnAutomaticallyCreateSubfolderForInstallChanged(value);
            }
        }
    } = AppConfig.AutomaticallyCreateSubfolderForInstall;

    public long PackageSizeBytes { get; private set => SetProperty(ref field, value); }

    public long RequiredSpaceBytes { get; private set => SetProperty(ref field, value); }

    public long AvailableSpaceBytes { get; private set => SetProperty(ref field, value); }

    private void OnAutomaticallyCreateSubfolderForInstallChanged(bool value)
    {
        AppConfig.AutomaticallyCreateSubfolderForInstall = value;
        if (_isApplyingPath || string.IsNullOrWhiteSpace(_selectedBasePath) || ActionType is not BslGameActionType.Install)
        {
            return;
        }

        SetInstallationPath(value ? Path.Combine(_selectedBasePath, GetDefaultFolderName()) : _selectedBasePath);
    }

    private void BslInstallGameDialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (CurrentGame is null)
        {
            Hide();
            return;
        }

        ApplyActionText();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            ErrorMessage = null;
            _requiresExistingInstallPath = false;
            CanStartAction = false;
            RefreshVisibilityBindings();

            BslGameStatusSnapshot status = await _backendService.GetStatusAsync(CurrentGame!.Id);
            SetDefaultInstallationPath(status);

            BslGamePackageManifest? manifest = await _backendService.GetPackageManifestAsync(CurrentGame.Id);
            if (manifest is null)
            {
                ErrorMessage = "当前游戏未提供可用的资源清单。";
                RefreshCanStartAction();
                return;
            }

            LoadPackageInfo(manifest);
            RefreshDiskSpace();
            RefreshCanStartAction();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Load BSL install dialog failed: {GameKey} {ActionType}", CurrentGame?.Id, ActionType);
        }
        finally
        {
            RefreshVisibilityBindings();
        }
    }

    private void ApplyActionText()
    {
        string gameName = CurrentGame?.Name ?? "游戏";
        (DialogTitle, StartButtonText, HintText) = ActionType switch
        {
            BslGameActionType.Update => ($"更新{gameName}", "开始更新", "将使用当前安装目录进行更新。"),
            BslGameActionType.Predownload => ($"预下载{gameName}", "开始预下载", "预下载资源会缓存到当前游戏目录，正式更新时会尝试直接应用。"),
            _ => ($"安装{gameName}", "开始安装", null),
        };
    }

    private void SetDefaultInstallationPath(BslGameStatusSnapshot status)
    {
        if (ActionType is BslGameActionType.Update or BslGameActionType.Predownload)
        {
            SetInstallationPath(status.InstallPath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(status.InstallPath))
            {
                _requiresExistingInstallPath = true;
                string gameName = CurrentGame?.Name ?? "游戏";
                ErrorMessage = $"未检测到{gameName}安装目录，请先导入已安装游戏或完成安装。";
                RefreshVisibilityBindings();
            }

            return;
        }

        string? defaultRoot = AppConfig.DefaultGameInstallationPath;
        if (!string.IsNullOrWhiteSpace(defaultRoot) && Directory.Exists(defaultRoot))
        {
            _selectedBasePath = defaultRoot;
            SetInstallationPath(AutomaticallyCreateSubfolderForInstall ? Path.Combine(defaultRoot, GetDefaultFolderName()) : defaultRoot);
            return;
        }

        string basePath = Path.Combine(AppConfig.CacheFolder, "Games");
        _selectedBasePath = basePath;
        SetInstallationPath(AutomaticallyCreateSubfolderForInstall ? Path.Combine(basePath, GetDefaultFolderName()) : basePath);
    }

    private void LoadPackageInfo(BslGamePackageManifest manifest)
    {
        bool isPredownload = ActionType is BslGameActionType.Predownload;
        var groups = isPredownload ? manifest.PredownloadPackageGroups : manifest.LatestPackageGroups;

        VersionText = isPredownload
            ? ValueOrUnknown(manifest.PredownloadVersion)
            : ValueOrUnknown(manifest.LatestVersion);
        PackageSizeBytes = groups.Sum(group => group.Items.Sum(item => item.PackageSize));
        PackageSizeText = BslDownloadHelper.FormatBytes(PackageSizeBytes);

        if (PackageSizeBytes <= 0)
        {
            ErrorMessage = isPredownload ? "当前没有可用的预下载资源。" : "当前没有可用的下载资源。";
        }
    }

    private void RefreshDiskSpace()
    {
        if (string.IsNullOrWhiteSpace(InstallationPath) || PackageSizeBytes <= 0)
        {
            RequiredSpaceText = PackageSizeBytes <= 0 ? "未知" : "读取中...";
            AvailableSpaceText = "读取中...";
            RequiredSpaceBytes = 0;
            AvailableSpaceBytes = 0;
            return;
        }

        try
        {
            BslDiskSpacePlan plan = BuildDiskSpacePlan(InstallationPath, PackageSizeBytes, ActionType is BslGameActionType.Predownload);
            BslDiskSpaceCheckResult diskCheck = BslDownloadHelper.CheckDiskSpace(plan);
            RequiredSpaceBytes = plan.RequiredFreeBytes;
            AvailableSpaceBytes = diskCheck.AvailableFreeSpace;
            RequiredSpaceText = BslDownloadHelper.FormatBytes(RequiredSpaceBytes);
            AvailableSpaceText = diskCheck.AvailableFreeSpace > 0 ? BslDownloadHelper.FormatBytes(diskCheck.AvailableFreeSpace) : "未知";

            if (!string.IsNullOrWhiteSpace(diskCheck.ErrorMessage))
            {
                ErrorMessage = diskCheck.ErrorMessage;
            }
            else if (!diskCheck.IsSatisfied)
            {
                ErrorMessage = $"可用空间不足，需要约 {RequiredSpaceText}，当前仅剩 {AvailableSpaceText}。";
            }

            TextBlock_AvailableSpace.Foreground = diskCheck.IsSatisfied
                ? App.Current.Resources["TextFillColorSecondaryBrush"] as Brush
                : App.Current.Resources["SystemFillColorCautionBrush"] as Brush;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Refresh BSL install dialog disk space failed: {GameKey}", CurrentGame?.Id);
        }
    }

    private void SetInstallationPath(string path)
    {
        _isApplyingPath = true;
        try
        {
            TextBlock_InstallationPath.FontSize = 14;
            InstallationPath = path;
            ErrorMessage = null;
            RefreshDiskSpace();
            RefreshCanStartAction();
            RefreshVisibilityBindings();
        }
        finally
        {
            _isApplyingPath = false;
        }
    }

    private void RefreshCanStartAction()
    {
        CanStartAction = string.IsNullOrWhiteSpace(ErrorMessage)
                         && !_requiresExistingInstallPath
                         && !string.IsNullOrWhiteSpace(InstallationPath)
                         && Path.IsPathFullyQualified(InstallationPath)
                         && PackageSizeBytes > 0;
    }

    [RelayCommand]
    private async Task ChangeInstallationPathAsync()
    {
        try
        {
            string? path = await FileDialogHelper.PickFolderAsync(XamlRoot);
            if (Directory.Exists(path))
            {
                _selectedBasePath = path;
                SetInstallationPath(AutomaticallyCreateSubfolderForInstall ? Path.Combine(path, GetDefaultFolderName()) : path);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Change BSL installation path failed: {GameKey}", CurrentGame?.Id);
            RefreshVisibilityBindings();
        }
    }

    [RelayCommand]
    private async Task StartActionAsync()
    {
        if (CurrentGame is null || !CanStartAction)
        {
            return;
        }

        try
        {
            if (ActionType is BslGameActionType.Install && !string.IsNullOrWhiteSpace(_selectedBasePath))
            {
                AppConfig.DefaultGameInstallationPath = _selectedBasePath;
                BslBackendSetting.SetInstallPath(CurrentGame.Id, InstallationPath);
            }

            await _backendService.Coordinator.QueueAsync(new BslQueuedActionRequest
            {
                GameKey = CurrentGame.Id,
                ActionType = ActionType,
                InstallPath = InstallationPath,
            });
            TaskQueued = true;
            Hide();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Queue BSL install action failed: {GameKey} {ActionType}", CurrentGame.Id, ActionType);
            RefreshVisibilityBindings();
        }
    }

    [RelayCommand]
    private void Close()
    {
        Hide();
    }

    private void RefreshVisibilityBindings()
    {
        RefreshCanStartAction();
        OnPropertyChanged(nameof(CanStartAction));
        OnPropertyChanged(nameof(CanChangeInstallationPath));
        OnPropertyChanged(nameof(AutoCreateSubfolderVisibility));
        OnPropertyChanged(nameof(PackageInfoVisibility));
        OnPropertyChanged(nameof(HintVisibility));
        OnPropertyChanged(nameof(ErrorVisibility));
        Bindings.Update();
    }

    private string GetDefaultFolderName()
    {
        return string.Equals(CurrentGame?.Id, WutheringWavesGameKey, StringComparison.OrdinalIgnoreCase)
            ? WutheringWavesFolderName
            : CurrentGame?.Name ?? "Game";
    }

    private static BslDiskSpacePlan BuildDiskSpacePlan(string targetPath, long bytesToDownload, bool predownload)
    {
        long normalizedBytes = Math.Max(0, bytesToDownload);
        return BslDownloadHelper.CreateDiskSpacePlan(
            targetPath,
            downloadBytes: normalizedBytes,
            stagingBytes: normalizedBytes,
            patchBytes: predownload ? normalizedBytes / 5 : normalizedBytes / 10,
            finalWriteBytes: 0,
            safetyRatio: 0.08d);
    }

    private static string ValueOrUnknown(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "未知" : value;
    }

    private void TextBlock_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args)
    {
        if (sender.FontSize > 12)
        {
            sender.FontSize--;
        }
    }
}
