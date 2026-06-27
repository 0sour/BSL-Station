namespace Starward.Features.BSL.Backend;

public enum BslBackendSuggestedAction
{
    None = 0,
    Retry = 1,
    CheckDiskSpace = 2,
    ChooseInstallPath = 3,
    CleanResidualCache = 4,
    CloseGame = 5,
    CheckCapabilityNotice = 6,
    RefreshResource = 7,
    RepairFiles = 8,
    InspectDetails = 9,
}
