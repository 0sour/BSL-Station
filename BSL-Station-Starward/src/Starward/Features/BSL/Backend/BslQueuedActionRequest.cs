using System;
using System.Threading.Tasks;

namespace Starward.Features.BSL.Backend;

public sealed class BslQueuedActionRequest
{
    public const int DefaultMaxRetryCount = 2;

    public string GameKey { get; init; } = string.Empty;

    public BslGameActionType ActionType { get; init; }

    public string? InstallPath { get; init; }

    public int MaxRetryCount { get; init; } = DefaultMaxRetryCount;

    public Func<BslBackendTaskItem, Task>? ProgressCallback { get; init; }
}
