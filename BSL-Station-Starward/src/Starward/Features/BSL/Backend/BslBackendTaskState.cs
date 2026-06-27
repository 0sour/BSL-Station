namespace Starward.Features.BSL.Backend;

public enum BslBackendTaskState
{
    Pending = 0,
    Queued = 1,
    Running = 2,
    Paused = 3,
    Succeeded = 4,
    Failed = 5,
    Canceled = 6,
}
