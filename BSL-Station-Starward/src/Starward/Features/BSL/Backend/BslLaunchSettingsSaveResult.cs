namespace Starward.Features.BSL.Backend;

public sealed class BslLaunchSettingsSaveResult
{
    public string GameKey { get; set; } = string.Empty;

    public BslGameLaunchSettingsSnapshot LaunchSettings { get; set; } = new();

    public BslGameStatusSnapshot Status { get; set; } = new();

    public bool InstallPathAccepted { get; set; } = true;

    public bool CustomExecutablePathAccepted { get; set; } = true;

    public string? WarningMessage { get; set; }

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningMessage);
}
