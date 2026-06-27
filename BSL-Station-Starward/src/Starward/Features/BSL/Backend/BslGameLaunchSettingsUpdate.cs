namespace Starward.Features.BSL.Backend;

public sealed class BslGameLaunchSettingsUpdate
{
    public bool UpdateInstallPath { get; init; }

    public string? InstallPath { get; init; }

    public bool? UseCustomExecutable { get; init; }

    public bool UpdateCustomExecutablePath { get; init; }

    public string? CustomExecutablePath { get; init; }

    public bool UpdateLaunchArgument { get; init; }

    public string? LaunchArgument { get; init; }
}
