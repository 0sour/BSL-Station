using System;
using System.IO;
using System.Linq;

namespace Starward.Features.BSL.Backend;

internal static class BslUninstallHelper
{
    public const string FailedStatus = "卸载失败";
    public const string NotStartedStatus = "卸载未启动";
    public const string CompletedStatus = "卸载完成";
    public const string GameRunningMessage = "游戏正在运行，请关闭后再试。";
    public const string DeleteDirectoryFailureMessage = "无法删除游戏目录，可能有文件或目录正被占用。";
    public const string RpcUninstallNotStartedMessage = "卸载服务未返回成功结果。";
    public const string CleanupCompletedDetail = "已清理安装目录记录与启动设置。";

    public static bool TryValidateInstallDirectory(
        string? installPath,
        out string? normalizedInstallPath,
        out string? failureMessage,
        params string?[] knownMarkers)
    {
        normalizedInstallPath = null;
        failureMessage = null;

        if (string.IsNullOrWhiteSpace(installPath))
        {
            failureMessage = "未找到游戏安装目录。";
            return false;
        }

        try
        {
            normalizedInstallPath = Path.GetFullPath(installPath);
        }
        catch
        {
            failureMessage = "安装目录无效。";
            return false;
        }

        if (!Directory.Exists(normalizedInstallPath))
        {
            failureMessage = "未找到游戏安装目录。";
            return false;
        }

        if (IsDriveRoot(normalizedInstallPath))
        {
            failureMessage = "安装目录不能是磁盘根目录。";
            return false;
        }

        if (ContainsProtectedLocation(normalizedInstallPath, AppConfig.UserDataFolder)
            || ContainsProtectedLocation(normalizedInstallPath, AppContext.BaseDirectory))
        {
            failureMessage = "安装目录包含启动器数据或程序目录，已取消删除。";
            return false;
        }

        if (knownMarkers.Length > 0
            && !knownMarkers.Any(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path))))
        {
            failureMessage = "当前目录未通过游戏目录校验，已取消删除。";
            return false;
        }

        return true;
    }

    public static BslGameLaunchSettingsUpdate CreateResetLaunchSettingsUpdate()
    {
        return new BslGameLaunchSettingsUpdate
        {
            UseCustomExecutable = false,
            UpdateCustomExecutablePath = true,
            CustomExecutablePath = null,
            UpdateLaunchArgument = false,
        };
    }

    private static bool IsDriveRoot(string path)
    {
        string normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? root = Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.IsNullOrWhiteSpace(root)
               && string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsProtectedLocation(string installPath, string? protectedPath)
    {
        if (string.IsNullOrWhiteSpace(protectedPath))
        {
            return false;
        }

        try
        {
            string normalizedInstallPath = Path.GetFullPath(installPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedProtectedPath = Path.GetFullPath(protectedPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return normalizedProtectedPath.StartsWith(
                normalizedInstallPath + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
