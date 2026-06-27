namespace Starward.Features.BSL;

internal sealed class BslGameSelectorPinnedChangedMessage
{
    public bool IsPinned { get; }

    public BslGameSelectorPinnedChangedMessage(bool isPinned)
    {
        IsPinned = isPinned;
    }
}
