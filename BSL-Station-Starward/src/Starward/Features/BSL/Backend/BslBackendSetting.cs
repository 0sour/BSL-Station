namespace Starward.Features.BSL.Backend;

internal static class BslBackendSetting
{
    public static string? GetInstallPath(string gameKey)
    {
        return AppConfig.GetValue<string>(default, $"bsl_install_path_{gameKey}");
    }

    public static void SetInstallPath(string gameKey, string? value)
    {
        AppConfig.SetValue(value, $"bsl_install_path_{gameKey}");
    }

    public static string? GetStartExePath(string gameKey)
    {
        return AppConfig.GetValue<string>(default, $"bsl_start_exe_{gameKey}");
    }

    public static void SetStartExePath(string gameKey, string? value)
    {
        AppConfig.SetValue(value, $"bsl_start_exe_{gameKey}");
    }

    public static bool GetUseCustomStartExe(string gameKey)
    {
        return AppConfig.GetValue<bool>(default, $"bsl_use_custom_start_exe_{gameKey}");
    }

    public static void SetUseCustomStartExe(string gameKey, bool value)
    {
        AppConfig.SetValue(value, $"bsl_use_custom_start_exe_{gameKey}");
    }

    public static string? GetStartArgument(string gameKey)
    {
        return AppConfig.GetValue<string>(default, $"bsl_start_argument_{gameKey}");
    }

    public static void SetStartArgument(string gameKey, string? value)
    {
        AppConfig.SetValue(value, $"bsl_start_argument_{gameKey}");
    }

    public static string? GetServerRegion(string gameKey)
    {
        return AppConfig.GetValue<string>(default, $"bsl_server_region_{gameKey}");
    }

    public static void SetServerRegion(string gameKey, string? value)
    {
        AppConfig.SetValue(value, $"bsl_server_region_{gameKey}");
    }
}
