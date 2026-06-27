namespace Starward.Features.BSL.Backend;

public sealed class BslGameLaunchSettingsSnapshot
{
    public string GameKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? InstallPath { get; set; }

    public string? InvalidInstallPath { get; set; }

    public string? DefaultExecutablePath { get; set; }

    public bool SupportsCustomExecutable { get; set; } = true;

    public bool SupportsLaunchArguments { get; set; } = true;

    public bool UseCustomExecutable { get; set; }

    public string? CustomExecutablePath { get; set; }

    public string? InvalidCustomExecutablePath { get; set; }

    public string? LaunchArgument { get; set; }

    public bool HasCustomExecutable => !string.IsNullOrWhiteSpace(CustomExecutablePath);

    public bool HasInvalidCustomExecutablePath => !string.IsNullOrWhiteSpace(InvalidCustomExecutablePath);

    public bool HasInvalidInstallPath => !string.IsNullOrWhiteSpace(InvalidInstallPath);

    public bool IsUsingCustomExecutable => UseCustomExecutable && HasCustomExecutable;

    public bool IsUsingDefaultExecutable => !IsUsingCustomExecutable;

    public string? EffectiveExecutablePath => UseCustomExecutable && HasCustomExecutable
        ? CustomExecutablePath
        : DefaultExecutablePath;

    public string InstallPathText => string.IsNullOrWhiteSpace(InstallPath) ? "未设置" : InstallPath;

    public string InvalidInstallPathText => string.IsNullOrWhiteSpace(InvalidInstallPath) ? "未设置" : InvalidInstallPath;

    public string DefaultExecutablePathText => string.IsNullOrWhiteSpace(DefaultExecutablePath) ? "未设置" : DefaultExecutablePath;

    public string CustomExecutablePathText => string.IsNullOrWhiteSpace(CustomExecutablePath) ? "未设置" : CustomExecutablePath;

    public string InvalidCustomExecutablePathText => string.IsNullOrWhiteSpace(InvalidCustomExecutablePath) ? "未设置" : InvalidCustomExecutablePath;

    public string EffectiveExecutablePathText => string.IsNullOrWhiteSpace(EffectiveExecutablePath) ? "未设置" : EffectiveExecutablePath;

    public string LaunchArgumentText => string.IsNullOrWhiteSpace(LaunchArgument) ? "未设置" : LaunchArgument;

    public string CustomExecutableStatusText => UseCustomExecutable
        ? HasCustomExecutable
            ? "使用自定义启动路径"
            : "自定义启动路径无效，已回退默认启动路径"
        : "使用默认启动路径";
}
