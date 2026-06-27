using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Starward.Features.BSL.Backend;

public sealed partial class BslCustomGameEntry : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public string? LaunchArgument { get; set; }

    public string? IconFilePath { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    [ObservableProperty]
    private bool isHidden;

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? System.IO.Path.GetFileNameWithoutExtension(ExecutablePath)
        : Name;
}
