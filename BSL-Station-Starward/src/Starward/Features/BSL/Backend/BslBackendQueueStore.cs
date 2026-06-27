using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Starward.RPC.GameInstall;

namespace Starward.Features.BSL.Backend;

internal static class BslBackendQueueStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static string StorePath => Path.Combine(AppConfig.CacheFolder, "BSL", "queue_state.json");

    public static IReadOnlyList<BslBackendQueueStoreItem> Load()
    {
        try
        {
            if (!File.Exists(StorePath))
            {
                return [];
            }

            string json = File.ReadAllText(StorePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<BslBackendQueueStoreItem>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(IReadOnlyCollection<BslBackendQueueStoreItem> items)
    {
        try
        {
            if (items.Count == 0)
            {
                BslDownloadHelper.DeleteFileIfExists(StorePath);
                return;
            }

            string? directory = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(items, JsonOptions);
            File.WriteAllText(StorePath, json, Encoding.UTF8);
        }
        catch
        {
        }
    }
}

internal sealed class BslBackendQueueStoreItem
{
    public Guid Id { get; set; }

    public string GameKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public BslGameActionType ActionType { get; set; }

    public BslBackendTaskState State { get; set; }

    public string StatusText { get; set; } = string.Empty;

    public string? DetailText { get; set; }

    public double Progress { get; set; }

    public GameInstallState InstallState { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string? InstallPath { get; set; }

    public int RetryCount { get; set; }

    public int MaxRetryCount { get; set; }

    public bool HasResidualCache { get; set; }

    public string? ResidualCachePath { get; set; }

    public string? CleanupHint { get; set; }

    public bool RecommendManualCleanup { get; set; }

    public BslBackendIssueKind IssueKind { get; set; }

    public BslBackendSuggestedAction SuggestedAction { get; set; }
}
