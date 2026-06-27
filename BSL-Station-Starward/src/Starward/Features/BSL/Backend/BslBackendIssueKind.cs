namespace Starward.Features.BSL.Backend;

public enum BslBackendIssueKind
{
    None = 0,
    DownloadFailure = 1,
    DiskSpaceInsufficient = 2,
    PathIssue = 3,
    ResidualCache = 4,
    GameRunning = 5,
    CapabilityUnavailable = 6,
    ResourceUnavailable = 7,
    VerificationFailure = 8,
    Unknown = 9,
    OperationFailure = 10,
}
