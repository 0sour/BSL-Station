using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Features.GameLauncher;
using System;
using System.Diagnostics;
using System.IO;

namespace Starward.Features.BSL.Backend;

internal static class BslLaunchSettingsHelper
{
    public static BslGameLaunchSettingsSnapshot CreateMiHoYoSnapshot(
        string gameKey,
        string displayName,
        GameId gameId,
        string? installPath,
        string? defaultExecutablePath)
    {
        ExecutablePathState pathState = ResolveExecutablePathState(AppConfig.GetThirdPartyToolPath(gameId.GameBiz));

        return new BslGameLaunchSettingsSnapshot
        {
            GameKey = gameKey,
            DisplayName = displayName,
            InstallPath = installPath,
            DefaultExecutablePath = defaultExecutablePath,
            UseCustomExecutable = AppConfig.GetEnableThirdPartyTool(gameId.GameBiz),
            CustomExecutablePath = pathState.ValidPath,
            InvalidCustomExecutablePath = pathState.InvalidPath,
            LaunchArgument = NormalizeLaunchArgument(AppConfig.GetStartArgument(gameId.GameBiz)),
        };
    }

    public static void SaveMiHoYoSettings(GameId gameId, BslGameLaunchSettingsUpdate update)
    {
        if (update.UseCustomExecutable.HasValue)
        {
            AppConfig.SetEnableThirdPartyTool(gameId.GameBiz, update.UseCustomExecutable.Value);
        }

        if (update.UpdateCustomExecutablePath)
        {
            GameLauncherService.SetThirdPartyToolPath(gameId, update.CustomExecutablePath);
        }

        if (update.UpdateLaunchArgument)
        {
            AppConfig.SetStartArgument(gameId.GameBiz, NormalizeLaunchArgument(update.LaunchArgument));
        }
    }

    public static BslGameLaunchSettingsSnapshot CreateGenericSnapshot(
        string gameKey,
        string displayName,
        string? installPath,
        string? defaultExecutablePath)
    {
        ExecutablePathState pathState = ResolveExecutablePathState(BslBackendSetting.GetStartExePath(gameKey));

        return new BslGameLaunchSettingsSnapshot
        {
            GameKey = gameKey,
            DisplayName = displayName,
            InstallPath = installPath,
            DefaultExecutablePath = defaultExecutablePath,
            UseCustomExecutable = BslBackendSetting.GetUseCustomStartExe(gameKey),
            CustomExecutablePath = pathState.ValidPath,
            InvalidCustomExecutablePath = pathState.InvalidPath,
            LaunchArgument = NormalizeLaunchArgument(BslBackendSetting.GetStartArgument(gameKey)),
        };
    }

    public static void SaveGenericSettings(string gameKey, BslGameLaunchSettingsUpdate update)
    {
        if (update.UseCustomExecutable.HasValue)
        {
            BslBackendSetting.SetUseCustomStartExe(gameKey, update.UseCustomExecutable.Value);
        }

        if (update.UpdateCustomExecutablePath)
        {
            SetGenericExecutablePath(gameKey, update.CustomExecutablePath);
        }

        if (update.UpdateLaunchArgument)
        {
            BslBackendSetting.SetStartArgument(gameKey, NormalizeLaunchArgument(update.LaunchArgument));
        }
    }

    public static void ApplyToStatus(BslGameStatusSnapshot status, BslGameLaunchSettingsSnapshot launchSettings)
    {
        status.LaunchSettings = launchSettings;
    }

    public static BslLaunchSettingsSaveResult CreateSaveResult(
        string gameKey,
        BslGameLaunchSettingsUpdate update,
        BslGameLaunchSettingsSnapshot launchSettings,
        BslGameStatusSnapshot status)
    {
        bool installPathAccepted = !update.UpdateInstallPath
            || string.IsNullOrWhiteSpace(update.InstallPath)
            || string.Equals(
                NormalizeDirectoryPathForComparison(update.InstallPath),
                NormalizeDirectoryPathForComparison(launchSettings.InstallPath),
                StringComparison.OrdinalIgnoreCase);

        string? requestedCustomExecutablePath = NormalizeExecutablePathForComparison(update.CustomExecutablePath);
        bool customExecutablePathAccepted = !update.UpdateCustomExecutablePath
            || string.IsNullOrWhiteSpace(update.CustomExecutablePath)
            || string.Equals(
                requestedCustomExecutablePath,
                NormalizeExecutablePathForComparison(launchSettings.CustomExecutablePath),
                StringComparison.OrdinalIgnoreCase);

        string? warningMessage = null;
        if (update.UpdateInstallPath
            && !string.IsNullOrWhiteSpace(update.InstallPath)
            && !installPathAccepted)
        {
            warningMessage = "安装目录无效，已忽略本次安装路径设置。";
        }
        else if (update.UpdateCustomExecutablePath
            && !string.IsNullOrWhiteSpace(update.CustomExecutablePath)
            && !customExecutablePathAccepted)
        {
            warningMessage = "自定义启动路径无效，已回退为默认启动路径。";
        }
        else if (launchSettings.UseCustomExecutable && !launchSettings.HasCustomExecutable)
        {
            warningMessage = "当前已启用自定义启动，但没有可用的自定义启动文件，实际会回退默认启动路径。";
        }

        return new BslLaunchSettingsSaveResult
        {
            GameKey = gameKey,
            LaunchSettings = launchSettings,
            Status = status,
            InstallPathAccepted = installPathAccepted,
            CustomExecutablePathAccepted = customExecutablePathAccepted,
            WarningMessage = warningMessage,
        };
    }

    public static ProcessStartInfo CreateProcessStartInfo(string executablePath, string? launchArgument)
    {
        string extension = Path.GetExtension(executablePath);
        string verb = extension is ".exe" or ".bat" ? "runas" : string.Empty;

        return new ProcessStartInfo(executablePath)
        {
            Arguments = NormalizeLaunchArgument(launchArgument) ?? string.Empty,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
            Verb = verb,
        };
    }

    public static string? NormalizeLaunchArgument(string? launchArgument)
    {
        return string.IsNullOrWhiteSpace(launchArgument) ? null : launchArgument.Trim();
    }

    public static string? NormalizeExecutablePathForComparison(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(GameLauncherService.GetFullPathIfRelativePath(executablePath));
        }
        catch
        {
            return null;
        }
    }

    public static string? NormalizeDirectoryPathForComparison(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(directoryPath);
        }
        catch
        {
            return null;
        }
    }

    private static void SetGenericExecutablePath(string gameKey, string? executablePath)
    {
        string? fullPath = NormalizeExecutablePathForComparison(executablePath);
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            BslBackendSetting.SetStartExePath(gameKey, null);
            return;
        }

        string relativePath = GameLauncherService.GetRelativePathIfInRemovableStorage(fullPath, out _);
        BslBackendSetting.SetStartExePath(gameKey, relativePath);
    }

    private static ExecutablePathState ResolveExecutablePathState(string? executablePath)
    {
        string? normalizedPath = NormalizeExecutablePathForComparison(executablePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return default;
        }

        return File.Exists(normalizedPath)
            ? new ExecutablePathState(normalizedPath, null)
            : new ExecutablePathState(null, normalizedPath);
    }

    private readonly record struct ExecutablePathState(string? ValidPath, string? InvalidPath);
}
