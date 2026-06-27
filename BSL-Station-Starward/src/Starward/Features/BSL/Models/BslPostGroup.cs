using System.Collections.ObjectModel;

namespace Starward.Features.BSL.Models;

public sealed class BslPostGroup
{
    public string Header { get; set; } = string.Empty;

    public ObservableCollection<BslPostItem> Items { get; } = [];
}
