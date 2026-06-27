using Starward.Features.BSL.Models;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace Starward.Features.BSL.Services;

internal static class BslHomeState
{
    static BslHomeState()
    {
        Games = BslGameCatalog.CreateGames();
        PinnedGames = [];

        foreach (BslGameSummary game in Games)
        {
            game.SelectorEntries.Add(game);
        }

        string[] pinnedGameIds = (AppConfig.BslPinnedGameIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        BslGameSummary[] pinnedGames = pinnedGameIds
            .Select(id => Games.FirstOrDefault(game => string.Equals(game.Id, id, StringComparison.OrdinalIgnoreCase)))
            .Where(game => game is not null)
            .Cast<BslGameSummary>()
            .Distinct()
            .ToArray();

        if (pinnedGames.Length == 0)
        {
            pinnedGames = Games.Take(3).ToArray();
        }

        foreach (BslGameSummary game in pinnedGames)
        {
            game.IsPinned = true;
            PinnedGames.Add(game);
        }

        CurrentGame = Games.FirstOrDefault(game => string.Equals(game.Id, AppConfig.BslCurrentGameId, StringComparison.OrdinalIgnoreCase))
            ?? PinnedGames.FirstOrDefault()
            ?? Games.First();
        CurrentGame.IsSelected = true;
    }


    public static ObservableCollection<BslGameSummary> Games { get; }

    public static ObservableCollection<BslGameSummary> PinnedGames { get; }

    public static BslGameSummary CurrentGame { get; private set; }

    public static event EventHandler<BslGameSummary>? CurrentGameChanged;


    public static void SelectGame(BslGameSummary game)
    {
        if (ReferenceEquals(CurrentGame, game))
        {
            return;
        }

        foreach (BslGameSummary item in Games)
        {
            item.IsSelected = ReferenceEquals(item, game);
        }

        CurrentGame = game;
        AppConfig.BslCurrentGameId = game.Id;
        CurrentGameChanged?.Invoke(null, game);
    }


    public static void TogglePinned(BslGameSummary game)
    {
        if (game.IsPinned)
        {
            game.IsPinned = false;
            PinnedGames.Remove(game);
            SavePinnedGames();
            return;
        }

        game.IsPinned = true;
        if (!PinnedGames.Contains(game))
        {
            PinnedGames.Add(game);
        }

        SavePinnedGames();
    }


    private static void SavePinnedGames()
    {
        AppConfig.BslPinnedGameIds = string.Join(',', PinnedGames.Select(game => game.Id));
    }
}
