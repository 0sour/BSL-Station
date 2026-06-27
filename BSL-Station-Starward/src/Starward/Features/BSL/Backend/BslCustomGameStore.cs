using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Starward.Features.BSL.Backend;

internal static class BslCustomGameStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static string StorePath => Path.Combine(AppConfig.CacheFolder, "BSL", "custom_games.json");

    public static IReadOnlyList<BslCustomGameEntry> Load()
    {
        try
        {
            if (!File.Exists(StorePath))
            {
                return [];
            }

            string json = File.ReadAllText(StorePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<BslCustomGameEntry>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(IReadOnlyCollection<BslCustomGameEntry> entries)
    {
        try
        {
            if (entries.Count == 0)
            {
                BslDownloadHelper.DeleteFileIfExists(StorePath);
                return;
            }

            string? directory = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(entries, JsonOptions);
            File.WriteAllText(StorePath, json, Encoding.UTF8);
        }
        catch
        {
        }
    }
}
