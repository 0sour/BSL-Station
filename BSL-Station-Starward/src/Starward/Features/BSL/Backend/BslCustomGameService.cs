using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.BSL.Backend;

internal sealed class BslCustomGameService
{
    private readonly ILogger<BslCustomGameService> _logger = AppConfig.GetLogger<BslCustomGameService>();

    public ObservableCollection<BslCustomGameEntry> Games { get; } = [];

    public BslCustomGameService()
    {
        foreach (BslCustomGameEntry entry in BslCustomGameStore.Load())
        {
            Games.Add(entry);
        }
    }

    public IReadOnlyList<BslCustomGameEntry> GetAll()
    {
        return Games.ToList();
    }

    public async Task<BslCustomGameEntry> AddOrUpdateAsync(
        string executablePath,
        string? name = null,
        string? launchArgument = null,
        bool isHidden = false,
        Guid? entryId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("未找到可执行文件。", fullPath);
        }

        BslCustomGameEntry? existing = entryId.HasValue
            ? Games.FirstOrDefault(x => x.Id == entryId.Value)
            : Games.FirstOrDefault(x => string.Equals(x.ExecutablePath, fullPath, StringComparison.OrdinalIgnoreCase));

        BslCustomGameEntry entry = existing ?? new BslCustomGameEntry();
        entry.ExecutablePath = fullPath;
        entry.WorkingDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        entry.Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(fullPath) : name.Trim();
        entry.LaunchArgument = BslLaunchSettingsHelper.NormalizeLaunchArgument(launchArgument);
        entry.IsHidden = isHidden;
        entry.IconFilePath = await ExtractIconAsync(fullPath, entry.Id, cancellationToken);

        if (existing is null)
        {
            Games.Add(entry);
        }

        Persist();
        return entry;
    }

    public bool Remove(Guid entryId)
    {
        BslCustomGameEntry? entry = Games.FirstOrDefault(x => x.Id == entryId);
        if (entry is null)
        {
            return false;
        }

        Games.Remove(entry);
        if (!string.IsNullOrWhiteSpace(entry.IconFilePath))
        {
            BslDownloadHelper.DeleteFileIfExists(entry.IconFilePath);
        }

        Persist();
        return true;
    }

    public void SetHidden(Guid entryId, bool isHidden)
    {
        BslCustomGameEntry? entry = Games.FirstOrDefault(x => x.Id == entryId);
        if (entry is null)
        {
            return;
        }

        entry.IsHidden = isHidden;
        Persist();
    }

    public Process? Launch(Guid entryId)
    {
        BslCustomGameEntry? entry = Games.FirstOrDefault(x => x.Id == entryId);
        if (entry is null)
        {
            throw new InvalidOperationException("未找到自定义游戏条目。");
        }

        if (!File.Exists(entry.ExecutablePath))
        {
            throw new FileNotFoundException("未找到可执行文件。", entry.ExecutablePath);
        }

        ProcessStartInfo startInfo = BslLaunchSettingsHelper.CreateProcessStartInfo(entry.ExecutablePath, entry.LaunchArgument);
        startInfo.WorkingDirectory = string.IsNullOrWhiteSpace(entry.WorkingDirectory)
            ? Path.GetDirectoryName(entry.ExecutablePath) ?? Environment.CurrentDirectory
            : entry.WorkingDirectory;
        return Process.Start(startInfo);
    }

    private void Persist()
    {
        BslCustomGameStore.Save(Games.ToList());
    }

    private async Task<string?> ExtractIconAsync(string executablePath, Guid entryId, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using Icon? icon = Icon.ExtractAssociatedIcon(executablePath);
            if (icon is null)
            {
                return null;
            }

            string iconDirectory = Path.Combine(AppConfig.CacheFolder, "BSL", "CustomGameIcons");
            Directory.CreateDirectory(iconDirectory);

            string iconPath = Path.Combine(iconDirectory, $"{entryId:N}.png");
            using Bitmap bitmap = icon.ToBitmap();
            await Task.Run(() => bitmap.Save(iconPath, System.Drawing.Imaging.ImageFormat.Png), cancellationToken);
            return iconPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Extract custom game icon failed: {ExecutablePath}", executablePath);
            return null;
        }
    }
}
