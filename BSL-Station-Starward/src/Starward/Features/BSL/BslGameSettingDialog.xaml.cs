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
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;

#pragma warning disable MVVMTK0034
#pragma warning disable MVVMTK0045

namespace Starward.Features.BSL;

[INotifyPropertyChanged]
public sealed partial class BslGameSettingDialog : ContentDialog
{
    private readonly ILogger<BslGameSettingDialog> _logger = AppConfig.GetLogger<BslGameSettingDialog>();
    private readonly BslBackendService _backendService = AppConfig.GetService<BslBackendService>();
    private bool _isApplyingSettings;

    public BslGameSettingDialog()
    {
        InitializeComponent();
        Loaded += BslGameSettingDialog_Loaded;
        Unloaded += BslGameSettingDialog_Unloaded;
    }

    public BslGameSummary? CurrentGame { get; set; }

    public bool StatusChanged { get; private set; }

    public ObservableCollection<BslBackendTaskItem> ResidualCacheTasks { get; } = [];

    public ObservableCollection<BslGamePackageGroup> LatestPackageGroups { get; } = [];

    public ObservableCollection<BslGamePackageGroup> PredownloadPackageGroups { get; } = [];

    public string GameKey => CurrentGame?.Id ?? string.Empty;

    public string GameName { get; set => SetProperty(ref field, value); } = string.Empty;

    public string? IconImage { get; set => SetProperty(ref field, value); }

    public string StatusText { get; set => SetProperty(ref field, value); } = "正在读取状态";

    public string CapabilityText { get; set => SetProperty(ref field, value); } = "未读取";

    public string VersionText { get; set => SetProperty(ref field, value); } = "本地：未知    最新：未知";

    public string? InstallPath { get; set => SetProperty(ref field, value); }

    public string DefaultExecutablePathText { get; set => SetProperty(ref field, value); } = "未设置";

    public string? GameSize { get; set => SetProperty(ref field, value); }

    public bool CanRepair { get; set => SetProperty(ref field, value); }

    public bool CanUninstall { get; set => SetProperty(ref field, value); }

    public bool SupportsCustomExecutable { get; set => SetProperty(ref field, value); } = true;

    public bool SupportsLaunchArguments { get; set => SetProperty(ref field, value); } = true;

    public string? LaunchArgument { get; set => SetProperty(ref field, value); }

    public bool UseCustomExecutable
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(CustomExecutableControlsEnabled));
                _ = SaveUseCustomExecutableAsync(value);
            }
        }
    }

    public string? CustomExecutablePath { get; set => SetProperty(ref field, value); }

    public string? WarningMessage { get; set => SetProperty(ref field, value); }

    public string? ErrorMessage { get; set => SetProperty(ref field, value); }

    public string? InvalidCustomExecutableMessage { get; set => SetProperty(ref field, value); }

    public bool IsUninstallWarningShown { get; set => SetProperty(ref field, value); }

    public string PredownloadCacheStatusText { get; set => SetProperty(ref field, value); } = "正在读取";

    public string PredownloadCacheSizeText { get; set => SetProperty(ref field, value); } = "0 B";

    public string PredownloadCachePathText { get; set => SetProperty(ref field, value); } = "未设置";

    public string? PredownloadCachePath { get; set => SetProperty(ref field, value); }

    public bool HasPredownloadCache { get; set => SetProperty(ref field, value); }

    public string LatestPackageHeader { get; set => SetProperty(ref field, value); } = "正式版";

    public string PredownloadPackageHeader { get; set => SetProperty(ref field, value); } = "预下载";

    public string PackageSummaryText { get; set => SetProperty(ref field, value); } = "正在读取资源清单";

    public string PackageUsageHintText { get; set => SetProperty(ref field, value); } = "安装会使用完整包；更新会优先使用增量包，失败时回退完整包。";

    public string? PackageErrorMessage { get; set => SetProperty(ref field, value); }

    public Visibility InstallPathVisibility => string.IsNullOrWhiteSpace(InstallPath) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility NoInstallPathVisibility => string.IsNullOrWhiteSpace(InstallPath) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GameSizeVisibility => string.IsNullOrWhiteSpace(GameSize) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility CustomExecutablePathVisibility => string.IsNullOrWhiteSpace(CustomExecutablePath) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility WarningVisibility => string.IsNullOrWhiteSpace(WarningMessage) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ErrorVisibility => string.IsNullOrWhiteSpace(ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility InvalidCustomExecutableVisibility => string.IsNullOrWhiteSpace(InvalidCustomExecutableMessage) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility UninstallWarningVisibility => IsUninstallWarningShown ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PredownloadCachePathVisibility => string.IsNullOrWhiteSpace(PredownloadCachePath) ? Visibility.Collapsed : Visibility.Visible;

    public bool CanClearPredownloadCache => HasPredownloadCache && !HasActiveBslTask();

    public Visibility NoResidualCacheVisibility => ResidualCacheTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PackageErrorVisibility => string.IsNullOrWhiteSpace(PackageErrorMessage) ? Visibility.Collapsed : Visibility.Visible;

    public bool CustomExecutableControlsEnabled => SupportsCustomExecutable && UseCustomExecutable;

    private void BslGameSettingDialog_Loaded(object sender, RoutedEventArgs e)
    {
        GameName = CurrentGame?.Name ?? "游戏";
        IconImage = CurrentGame?.IconImage;
        _ = LoadAsync();
    }

    private void BslGameSettingDialog_Unloaded(object sender, RoutedEventArgs e)
    {
        FlipView_Settings.Items.Clear();
    }

    private void FlipView_Settings_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            DependencyObject grid = VisualTreeHelper.GetChild(FlipView_Settings, 0);
            int count = VisualTreeHelper.GetChildrenCount(grid);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(grid, i);
                if (child is Button button)
                {
                    button.IsHitTestVisible = false;
                    button.Opacity = 0;
                }
                else if (child is ScrollViewer scrollViewer)
                {
                    scrollViewer.PointerWheelChanged += (_, args) => args.Handled = true;
                }
            }
        }
        catch
        {
        }
    }

    private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        try
        {
            if (args.InvokedItemContainer?.Tag is string index && int.TryParse(index, out int target))
            {
                FlipView_Settings.SelectedIndex = target;
            }
        }
        catch
        {
        }
    }

    [RelayCommand]
    private void Close()
    {
        Hide();
    }

    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(GameKey))
        {
            ErrorMessage = "未选择游戏。";
            RefreshVisibilityBindings();
            return;
        }

        try
        {
            ErrorMessage = null;
            BslGameStatusSnapshot status = await _backendService.GetStatusAsync(GameKey);
            ApplyStatus(status);
            ApplyLaunchSettings(status.LaunchSettings ?? await _backendService.GetLaunchSettingsAsync(GameKey));
            await LoadCacheAsync();
            await LoadPackageManifestAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusText = "状态读取失败";
            _logger.LogError(ex, "Load BSL game settings failed: {GameKey}", GameKey);
        }
        finally
        {
            RefreshVisibilityBindings();
        }
    }

    private void ApplyStatus(BslGameStatusSnapshot status)
    {
        GameName = string.IsNullOrWhiteSpace(status.DisplayName) ? GameName : status.DisplayName;
        StatusText = BuildStatusText(status);
        CapabilityText = BuildCapabilityText(status.Capabilities);
        VersionText = $"本地：{ValueOrUnknown(status.LocalVersion)}    最新：{ValueOrUnknown(status.LatestVersion)}";
        InstallPath = status.InstallPath;
        DefaultExecutablePathText = ValueOrUnset(status.ExecutablePath);
        GameSize = GetSize(status.InstallPath);
        CanRepair = status.CanRepair;
        CanUninstall = status.IsInstalled && status.Capabilities.HasFlag(BslGameCapability.Uninstall);
        WarningMessage = status.Warnings.Count > 0 ? string.Join("；", status.Warnings) : null;
    }

    private void ApplyLaunchSettings(BslGameLaunchSettingsSnapshot settings)
    {
        InstallPath = settings.InstallPath;
        DefaultExecutablePathText = ValueOrUnset(settings.DefaultExecutablePath);
        SupportsCustomExecutable = settings.SupportsCustomExecutable;
        SupportsLaunchArguments = settings.SupportsLaunchArguments;
        _isApplyingSettings = true;
        UseCustomExecutable = settings.UseCustomExecutable;
        _isApplyingSettings = false;
        OnPropertyChanged(nameof(CustomExecutableControlsEnabled));
        CustomExecutablePath = settings.CustomExecutablePath;
        LaunchArgument = settings.LaunchArgument;
        InvalidCustomExecutableMessage = string.IsNullOrWhiteSpace(settings.InvalidCustomExecutablePath)
            ? null
            : $"自定义启动程序无效：{settings.InvalidCustomExecutablePath}";
        GameSize = GetSize(settings.InstallPath);
    }

    private async Task LoadCacheAsync()
    {
        try
        {
            BslGameCacheSnapshot? cache = await _backendService.GetCacheSnapshotAsync(GameKey);
            HasPredownloadCache = cache?.HasPredownloadCache == true;
            if (cache is null)
            {
                PredownloadCacheStatusText = "当前游戏未提供缓存状态";
                PredownloadCacheSizeText = "0 B";
                PredownloadCachePath = null;
                HasPredownloadCache = false;
                PredownloadCachePathText = "未设置";
            }
            else
            {
                PredownloadCacheStatusText = cache.PredownloadStatusText;
                PredownloadCacheSizeText = cache.PredownloadCacheSizeText;
                PredownloadCachePath = cache.PredownloadCachePath;
                HasPredownloadCache = cache.HasPredownloadCache;
                PredownloadCachePathText = string.IsNullOrWhiteSpace(cache.PredownloadCachePath) ? "未设置" : cache.PredownloadCachePath;
            }

            RefreshResidualCacheTasks();
        }
        catch (Exception ex)
        {
            PredownloadCacheStatusText = "缓存状态读取失败";
            HasPredownloadCache = false;
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Load BSL cache failed: {GameKey}", GameKey);
        }
        finally
        {
            RefreshVisibilityBindings();
        }
    }

    private async Task LoadPackageManifestAsync()
    {
        try
        {
            PackageErrorMessage = null;
            LatestPackageGroups.Clear();
            PredownloadPackageGroups.Clear();

            BslGamePackageManifest? manifest = await _backendService.GetPackageManifestAsync(GameKey);
            if (manifest is null)
            {
                PackageErrorMessage = "当前游戏未提供资源清单。";
                PackageSummaryText = "未读取到可用资源清单";
                return;
            }

            LatestPackageHeader = string.IsNullOrWhiteSpace(manifest.LatestVersion)
                ? "正式版"
                : $"正式版 {manifest.LatestVersion}";
            PredownloadPackageHeader = string.IsNullOrWhiteSpace(manifest.PredownloadVersion)
                ? "预下载"
                : $"预下载 {manifest.PredownloadVersion}";

            foreach (BslGamePackageGroup group in manifest.LatestPackageGroups)
            {
                LatestPackageGroups.Add(group);
            }

            foreach (BslGamePackageGroup group in manifest.PredownloadPackageGroups)
            {
                PredownloadPackageGroups.Add(group);
            }

            PackageSummaryText = BuildPackageSummaryText(manifest);
            PackageUsageHintText = manifest.PredownloadPackageGroups.Count > 0
                ? "安装会使用完整包；更新会优先使用增量包，失败时回退完整包；预下载资源会单独缓存。"
                : "安装会使用完整包；更新会优先使用增量包，失败时回退完整包；当前游戏没有可用预下载资源。";

            if (LatestPackageGroups.Count == 0 && PredownloadPackageGroups.Count == 0)
            {
                PackageErrorMessage = "资源清单为空。";
            }
        }
        catch (Exception ex)
        {
            PackageErrorMessage = ex.Message;
            _logger.LogError(ex, "Load BSL package manifest failed: {GameKey}", GameKey);
        }
        finally
        {
            RefreshVisibilityBindings();
        }
    }

    private void RefreshResidualCacheTasks()
    {
        ResidualCacheTasks.Clear();
        foreach (BslBackendTaskItem task in _backendService.Coordinator.Tasks
                     .Where(x => string.Equals(x.GameKey, GameKey, StringComparison.OrdinalIgnoreCase) && x.HasResidualCache)
                     .OrderByDescending(x => x.UpdatedAt))
        {
            ResidualCacheTasks.Add(task);
        }

        OnPropertyChanged(nameof(NoResidualCacheVisibility));
    }

    [RelayCommand]
    private async Task LocateGameAsync()
    {
        try
        {
            string? folder = await FileDialogHelper.PickFolderAsync(XamlRoot);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            BslLaunchSettingsSaveResult result = await _backendService.ImportGameAsync(GameKey, folder);
            StatusChanged = true;
            ApplyStatus(result.Status);
            ApplyLaunchSettings(result.LaunchSettings);
            WarningMessage = result.WarningMessage;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Locate BSL game failed: {GameKey}", GameKey);
        }
        finally
        {
            RefreshVisibilityBindings();
        }
    }

    [RelayCommand]
    private async Task OpenInstallGameFolderAsync()
    {
        try
        {
            if (Directory.Exists(InstallPath))
            {
                await Launcher.LaunchFolderPathAsync(InstallPath);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Open BSL game folder failed: {GameKey}", GameKey);
        }
    }

    [RelayCommand]
    private async Task ClearInstallPathAsync()
    {
        try
        {
            BslLaunchSettingsSaveResult result = await _backendService.SaveLaunchSettingsAndRefreshStatusAsync(
                GameKey,
                new BslGameLaunchSettingsUpdate
                {
                    UpdateInstallPath = true,
                    InstallPath = null,
                });
            StatusChanged = true;
            IsUninstallWarningShown = false;
            ApplyStatus(result.Status);
            ApplyLaunchSettings(result.LaunchSettings);
            WarningMessage = result.WarningMessage;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Clear BSL game install path failed: {GameKey}", GameKey);
        }
        finally
        {
            RefreshVisibilityBindings();
        }
    }

    [RelayCommand]
    private void ShowUninstallWarning()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(InstallPath))
        {
            ErrorMessage = "未设置安装目录。";
            RefreshVisibilityBindings();
            return;
        }

        IsUninstallWarningShown = true;
        RefreshVisibilityBindings();
    }

    [RelayCommand]
    private async Task RepairGameAsync()
    {
        try
        {
            await QueueActionAsync(BslGameActionType.Repair);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Queue BSL repair failed: {GameKey}", GameKey);
            RefreshVisibilityBindings();
        }
    }

    [RelayCommand]
    private async Task UninstallGameAsync()
    {
        try
        {
            await QueueActionAsync(BslGameActionType.Uninstall);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Queue BSL uninstall failed: {GameKey}", GameKey);
            RefreshVisibilityBindings();
        }
    }

    private async Task QueueActionAsync(BslGameActionType action)
    {
        await _backendService.QueueAsync(new BslQueuedActionRequest
        {
            GameKey = GameKey,
            ActionType = action,
        });
        StatusChanged = true;
        Hide();
        WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(typeof(BslQueuePage)));
    }

    [RelayCommand]
    private async Task SaveLaunchArgumentAsync()
    {
        try
        {
            await SaveLaunchSettingsAsync(new BslGameLaunchSettingsUpdate
            {
                UpdateLaunchArgument = true,
                LaunchArgument = LaunchArgument,
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Save BSL launch argument failed: {GameKey}", GameKey);
            RefreshVisibilityBindings();
        }
    }

    private async Task SaveUseCustomExecutableAsync(bool value)
    {
        if (_isApplyingSettings || string.IsNullOrWhiteSpace(GameKey))
        {
            return;
        }

        try
        {
            await SaveLaunchSettingsAsync(new BslGameLaunchSettingsUpdate
            {
                UseCustomExecutable = value,
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Save BSL custom executable switch failed: {GameKey}", GameKey);
            RefreshVisibilityBindings();
        }
    }

    [RelayCommand]
    private async Task ChangeCustomExecutableAsync()
    {
        try
        {
            string? file = await FileDialogHelper.PickSingleFileAsync(
                XamlRoot,
                ("Executable", ".exe"),
                ("Batch", ".bat"),
                ("Command", ".cmd"));
            if (string.IsNullOrWhiteSpace(file))
            {
                return;
            }

            await SaveLaunchSettingsAsync(new BslGameLaunchSettingsUpdate
            {
                UseCustomExecutable = true,
                UpdateCustomExecutablePath = true,
                CustomExecutablePath = file,
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Change BSL custom executable failed: {GameKey}", GameKey);
            RefreshVisibilityBindings();
        }
    }

    [RelayCommand]
    private async Task OpenCustomExecutableFolderAsync()
    {
        try
        {
            if (File.Exists(CustomExecutablePath))
            {
                string? folder = Path.GetDirectoryName(CustomExecutablePath);
                StorageFile file = await StorageFile.GetFileFromPathAsync(CustomExecutablePath);
                FolderLauncherOptions option = new();
                option.ItemsToSelect.Add(file);
                await Launcher.LaunchFolderPathAsync(folder, option);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Open BSL custom executable folder failed: {GameKey}", GameKey);
            RefreshVisibilityBindings();
        }
    }

    [RelayCommand]
    private async Task OpenPredownloadCacheFolderAsync()
    {
        try
        {
            if (Directory.Exists(PredownloadCachePath))
            {
                await Launcher.LaunchFolderPathAsync(PredownloadCachePath);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Open BSL predownload cache folder failed: {GameKey}", GameKey);
            RefreshVisibilityBindings();
        }
    }

    [RelayCommand]
    private async Task ClearPredownloadCacheAsync()
    {
        try
        {
            if (!CanClearPredownloadCache)
            {
                return;
            }

            bool cleared = await _backendService.ClearPredownloadCacheAsync(GameKey);
            if (cleared)
            {
                ErrorMessage = null;
                await LoadCacheAsync();
            }
            else
            {
                ErrorMessage = "下载缓存清理失败或当前无可清理缓存。";
                RefreshVisibilityBindings();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Clear BSL predownload cache failed: {GameKey}", GameKey);
            RefreshVisibilityBindings();
        }
    }

    [RelayCommand]
    private async Task DeleteCustomExecutablePathAsync()
    {
        try
        {
            await SaveLaunchSettingsAsync(new BslGameLaunchSettingsUpdate
            {
                UpdateCustomExecutablePath = true,
                CustomExecutablePath = null,
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Delete BSL custom executable failed: {GameKey}", GameKey);
            RefreshVisibilityBindings();
        }
    }

    private async Task SaveLaunchSettingsAsync(BslGameLaunchSettingsUpdate update)
    {
        BslLaunchSettingsSaveResult result = await _backendService.SaveLaunchSettingsAndRefreshStatusAsync(GameKey, update);
        StatusChanged = true;
        ApplyStatus(result.Status);
        ApplyLaunchSettings(result.LaunchSettings);
        WarningMessage = result.WarningMessage;
        ErrorMessage = null;
        RefreshVisibilityBindings();
    }

    private void RefreshVisibilityBindings()
    {
        OnPropertyChanged(nameof(InstallPathVisibility));
        OnPropertyChanged(nameof(NoInstallPathVisibility));
        OnPropertyChanged(nameof(GameSizeVisibility));
        OnPropertyChanged(nameof(CustomExecutablePathVisibility));
        OnPropertyChanged(nameof(WarningVisibility));
        OnPropertyChanged(nameof(ErrorVisibility));
        OnPropertyChanged(nameof(InvalidCustomExecutableVisibility));
        OnPropertyChanged(nameof(UninstallWarningVisibility));
        OnPropertyChanged(nameof(PredownloadCachePathVisibility));
        OnPropertyChanged(nameof(CanClearPredownloadCache));
        OnPropertyChanged(nameof(NoResidualCacheVisibility));
        OnPropertyChanged(nameof(PackageErrorVisibility));
        OnPropertyChanged(nameof(CustomExecutableControlsEnabled));
    }

    private async void CleanupResidualCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is not Guid taskId)
        {
            return;
        }

        try
        {
            await _backendService.CleanupResidualCacheAsync(taskId);
            RefreshResidualCacheTasks();
            await LoadCacheAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Cleanup BSL residual cache failed: {GameKey}", GameKey);
            RefreshVisibilityBindings();
        }
    }

    private async void Button_CopyPackageUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button button)
            {
                return;
            }

            string? url = null;
            if (button.DataContext is BslGamePackageGroup group)
            {
                url = string.Join(Environment.NewLine, group.Items
                    .Select(x => x.Url)
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
            }
            else if (button.DataContext is BslGamePackageItem item)
            {
                url = item.Url;
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                ClipboardHelper.SetText(url);
                await CopySuccessAsync(button);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy BSL package url failed: {GameKey}", GameKey);
        }
    }

    private static async Task CopySuccessAsync(Button button)
    {
        try
        {
            button.IsEnabled = false;
            if (button.Content is FontIcon icon)
            {
                icon.Glyph = "\uF78C";
                await Task.Delay(1000);
            }
        }
        finally
        {
            button.IsEnabled = true;
            if (button.Content is FontIcon icon)
            {
                icon.Glyph = "\uE71B";
            }
        }
    }

    private static string BuildStatusText(BslGameStatusSnapshot status)
    {
        string installState = status.IsInstalled ? "已安装" : "未安装";
        if (status.IsBusy)
        {
            installState = "任务处理中";
        }
        else if (status.CanUpdate)
        {
            installState = "可更新";
        }
        else if (status.CanPredownload)
        {
            installState = "可预下载";
        }

        return $"{installState} / {BuildRegionText(status.Region)} / {BuildSupportLevelText(status.SupportLevel)}";
    }

    private static string BuildRegionText(BslGameServerRegion region)
    {
        return region switch
        {
            BslGameServerRegion.China => "国服",
            BslGameServerRegion.Global => "官服",
            BslGameServerRegion.Bilibili => "B服",
            _ => "未指定",
        };
    }

    private static string BuildSupportLevelText(BslGameSupportLevel supportLevel)
    {
        return supportLevel switch
        {
            BslGameSupportLevel.Verified => "完整适配",
            BslGameSupportLevel.Partial => "部分适配",
            _ => "规划中",
        };
    }

    private static string BuildCapabilityText(BslGameCapability capabilities)
    {
        string[] values =
        [
            capabilities.HasFlag(BslGameCapability.Import) ? "导入" : string.Empty,
            capabilities.HasFlag(BslGameCapability.Launch) ? "启动" : string.Empty,
            capabilities.HasFlag(BslGameCapability.Download) ? "下载" : string.Empty,
            capabilities.HasFlag(BslGameCapability.Update) ? "更新" : string.Empty,
            capabilities.HasFlag(BslGameCapability.Predownload) ? "预下载" : string.Empty,
            capabilities.HasFlag(BslGameCapability.Repair) ? "修复" : string.Empty,
            capabilities.HasFlag(BslGameCapability.Uninstall) ? "卸载" : string.Empty,
            capabilities.HasFlag(BslGameCapability.Notices) ? "公告" : string.Empty,
            capabilities.HasFlag(BslGameCapability.Background) ? "背景" : string.Empty,
        ];

        string text = string.Join(" / ", values.Where(x => !string.IsNullOrEmpty(x)));
        return string.IsNullOrWhiteSpace(text) ? "暂无" : text;
    }

    private static string BuildPackageSummaryText(BslGamePackageManifest manifest)
    {
        int latestCount = manifest.LatestPackageGroups.Sum(group => group.Items.Count);
        long latestBytes = manifest.LatestPackageGroups.Sum(group => group.Items.Sum(item => item.PackageSize));
        int predownloadCount = manifest.PredownloadPackageGroups.Sum(group => group.Items.Count);
        long predownloadBytes = manifest.PredownloadPackageGroups.Sum(group => group.Items.Sum(item => item.PackageSize));

        string latestText = latestCount > 0
            ? $"正式资源 {latestCount} 个文件，约 {BslDownloadHelper.FormatBytes(latestBytes)}"
            : "正式资源为空";
        string predownloadText = predownloadCount > 0
            ? $"预下载资源 {predownloadCount} 个文件，约 {BslDownloadHelper.FormatBytes(predownloadBytes)}"
            : "无预下载资源";
        return $"{latestText}；{predownloadText}";
    }

    private static string ValueOrUnknown(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "未知" : value;
    }

    private static string ValueOrUnset(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "未设置" : value;
    }

    private static string? GetSize(string? path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            long size = new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(x => x.Length);
            double gb = size / 1024d / 1024d / 1024d;
            return $"{gb:F2}GB";
        }
        catch
        {
            return null;
        }
    }

    private void TextBlock_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args)
    {
        if (sender.FontSize > 12)
        {
            sender.FontSize -= 1;
        }
    }

    private bool HasActiveBslTask()
    {
        return _backendService.Coordinator.Tasks.Any(task =>
            string.Equals(task.GameKey, GameKey, StringComparison.OrdinalIgnoreCase)
            && task.State is BslBackendTaskState.Pending or BslBackendTaskState.Queued or BslBackendTaskState.Running or BslBackendTaskState.Paused);
    }
}
