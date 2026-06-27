using System;

namespace Starward.Features.BSL.Backend;

public sealed class BslCustomGameUpdateRequest
{
    public Guid? EntryId { get; init; }

    public string ExecutablePath { get; init; } = string.Empty;

    public string? Name { get; init; }

    public string? LaunchArgument { get; init; }

    public bool IsHidden { get; init; }
}
