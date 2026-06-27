namespace Starward.Features.BSL.Backend;

[System.Flags]
public enum BslGameCapability
{
    None = 0,
    Import = 1 << 0,
    Launch = 1 << 1,
    Download = 1 << 2,
    Update = 1 << 3,
    Predownload = 1 << 4,
    Repair = 1 << 5,
    Uninstall = 1 << 6,
    Notices = 1 << 7,
    Background = 1 << 8,
}
