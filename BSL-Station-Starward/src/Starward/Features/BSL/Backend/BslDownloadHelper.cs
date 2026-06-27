using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.BSL.Backend;

internal static class BslDownloadHelper
{
    private const long DefaultMinimumSafetyMarginBytes = 512L * 1024 * 1024;
    private const int DefaultDownloadRetryCount = 3;

    public static async Task DownloadFileAsync(
        HttpClient httpClient,
        string downloadUrl,
        string destinationPath,
        Func<long, long?, Task>? progressCallback = null,
        long? expectedTotalBytes = null,
        bool allowResume = false,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        string tempPath = $"{destinationPath}.part";
        long existingLength = 0;

        try
        {
            if (allowResume && File.Exists(tempPath))
            {
                existingLength = new FileInfo(tempPath).Length;
                if (expectedTotalBytes > 0 && existingLength > expectedTotalBytes.Value)
                {
                    DeleteFileIfExists(tempPath);
                    existingLength = 0;
                }
                else if (expectedTotalBytes > 0 && existingLength == expectedTotalBytes.Value)
                {
                    if (File.Exists(destinationPath))
                    {
                        DeleteFileIfExists(destinationPath);
                    }

                    File.Move(tempPath, destinationPath, true);
                    if (progressCallback is not null)
                    {
                        await progressCallback(existingLength, expectedTotalBytes);
                    }

                    return;
                }
            }
            else if (File.Exists(tempPath))
            {
                DeleteFileIfExists(tempPath);
            }

            using HttpResponseMessage response = await SendDownloadRequestAsync(
                httpClient,
                downloadUrl,
                tempPath,
                existingLength,
                allowResume,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            long? totalBytes = ResolveTotalBytes(response, existingLength, expectedTotalBytes);
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream output = new(
                tempPath,
                existingLength > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 64,
                useAsync: true);

            byte[] buffer = new byte[1024 * 64];
            long downloadedBytes = existingLength;
            if (downloadedBytes > 0 && progressCallback is not null)
            {
                await progressCallback(downloadedBytes, totalBytes);
            }

            int bytesRead;

            while ((bytesRead = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                downloadedBytes += bytesRead;

                if (progressCallback is not null)
                {
                    await progressCallback(downloadedBytes, totalBytes);
                }
            }

            await output.FlushAsync(cancellationToken);

            if (expectedTotalBytes > 0 && output.Length != expectedTotalBytes.Value)
            {
                throw new InvalidOperationException($"Download size mismatch. Expected {expectedTotalBytes.Value}, got {output.Length}.");
            }

            if (File.Exists(destinationPath))
            {
                DeleteFileIfExists(destinationPath);
            }

            File.Move(tempPath, destinationPath, true);
        }
        catch
        {
            if (!allowResume && File.Exists(tempPath))
            {
                DeleteFileIfExists(tempPath);
            }

            throw;
        }
    }

    public static async Task DownloadFileWithRetryAsync(
        HttpClient httpClient,
        string downloadUrl,
        string destinationPath,
        Func<long, long?, Task>? progressCallback = null,
        Action<int, Exception>? retryCallback = null,
        int maxAttempts = DefaultDownloadRetryCount,
        long? expectedTotalBytes = null,
        bool allowResume = false,
        CancellationToken cancellationToken = default)
    {
        Exception? lastException = null;
        int attemptLimit = Math.Max(1, maxAttempts);

        for (int attempt = 1; attempt <= attemptLimit; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadFileAsync(
                    httpClient,
                    downloadUrl,
                    destinationPath,
                    progressCallback,
                    expectedTotalBytes,
                    allowResume,
                    cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A pause/cancel was requested. Propagate without retrying and without firing the
                // retry callback, so the queue does not show a misleading "准备重试" message.
                throw;
            }
            catch (Exception ex) when (attempt < attemptLimit && IsRetryableException(ex))
            {
                lastException = ex;
                retryCallback?.Invoke(attempt, ex);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt * 2, 6)), cancellationToken);
            }
            catch (Exception ex)
            {
                lastException = ex;
                break;
            }
        }

        throw lastException ?? new InvalidOperationException("Download failed.");
    }

    private static async Task<HttpResponseMessage> SendDownloadRequestAsync(
        HttpClient httpClient,
        string downloadUrl,
        string tempPath,
        long existingLength,
        bool allowResume,
        CancellationToken cancellationToken)
    {
        if (allowResume && existingLength > 0)
        {
            HttpResponseMessage resumeResponse = await SendCoreAsync(httpClient, downloadUrl, existingLength, cancellationToken);
            if (resumeResponse.StatusCode == HttpStatusCode.PartialContent)
            {
                return resumeResponse;
            }

            resumeResponse.Dispose();
            DeleteFileIfExists(tempPath);
        }

        return await SendCoreAsync(httpClient, downloadUrl, null, cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendCoreAsync(
        HttpClient httpClient,
        string downloadUrl,
        long? rangeStart,
        CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(HttpMethod.Get, downloadUrl);
        if (rangeStart > 0)
        {
            request.Headers.Range = new RangeHeaderValue(rangeStart.Value, null);
        }

        try
        {
            return await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private static long? ResolveTotalBytes(HttpResponseMessage response, long existingLength, long? expectedTotalBytes)
    {
        if (expectedTotalBytes > 0)
        {
            return expectedTotalBytes;
        }

        if (response.Content.Headers.ContentRange?.Length is long contentRangeLength && contentRangeLength > 0)
        {
            return contentRangeLength;
        }

        if (response.Content.Headers.ContentLength is long contentLength && contentLength > 0)
        {
            return existingLength + contentLength;
        }

        return null;
    }

    public static BslDiskSpacePlan CreateDiskSpacePlan(
        string targetPath,
        long downloadBytes = 0,
        long stagingBytes = 0,
        long patchBytes = 0,
        long finalWriteBytes = 0,
        double safetyRatio = 0.1d,
        long minimumSafetyMarginBytes = DefaultMinimumSafetyMarginBytes)
    {
        long normalizedDownloadBytes = Math.Max(0, downloadBytes);
        long normalizedStagingBytes = Math.Max(0, stagingBytes);
        long normalizedPatchBytes = Math.Max(0, patchBytes);
        long normalizedFinalWriteBytes = Math.Max(0, finalWriteBytes);
        long payloadBytes = normalizedDownloadBytes
                            + normalizedStagingBytes
                            + normalizedPatchBytes
                            + normalizedFinalWriteBytes;

        return new BslDiskSpacePlan
        {
            TargetPath = targetPath,
            DownloadBytes = normalizedDownloadBytes,
            StagingBytes = normalizedStagingBytes,
            PatchBytes = normalizedPatchBytes,
            FinalWriteBytes = normalizedFinalWriteBytes,
            SafetyBytes = CalculateSafetyMargin(payloadBytes, safetyRatio, minimumSafetyMarginBytes),
        };
    }

    public static BslDiskSpaceCheckResult CheckDiskSpace(BslDiskSpacePlan plan)
    {
        string resolvedPath = Path.GetFullPath(string.IsNullOrWhiteSpace(plan.TargetPath) ? Environment.CurrentDirectory : plan.TargetPath);
        string? rootPath = Path.GetPathRoot(resolvedPath);

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return new BslDiskSpaceCheckResult
            {
                Plan = plan,
                ResolvedPath = resolvedPath,
                ErrorMessage = "无法解析目标目录所在磁盘。",
            };
        }

        try
        {
            DriveInfo drive = new(rootPath);
            if (!drive.IsReady)
            {
                return new BslDiskSpaceCheckResult
                {
                    Plan = plan,
                    DriveName = drive.Name,
                    ResolvedPath = resolvedPath,
                    ErrorMessage = "目标磁盘当前不可用。",
                };
            }

            long availableFreeSpace = drive.AvailableFreeSpace;
            return new BslDiskSpaceCheckResult
            {
                Plan = plan,
                DriveName = drive.Name,
                ResolvedPath = resolvedPath,
                AvailableFreeSpace = availableFreeSpace,
                IsSatisfied = availableFreeSpace >= plan.RequiredFreeBytes,
            };
        }
        catch (Exception ex)
        {
            return new BslDiskSpaceCheckResult
            {
                Plan = plan,
                DriveName = rootPath,
                ResolvedPath = resolvedPath,
                ErrorMessage = ex.Message,
            };
        }
    }

    public static string BuildDiskSpaceFailureStatus(BslGameActionType actionType)
    {
        return $"{GetActionDisplayName(actionType)}空间不足";
    }

    public static string BuildDiskSpaceFailureMessage(BslGameActionType actionType, BslDiskSpaceCheckResult result)
    {
        string actionName = GetActionDisplayName(actionType);
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            return $"{actionName}前无法完成磁盘检查：{result.ErrorMessage}";
        }

        return $"{actionName}前可用空间不足，需要约 {FormatBytes(result.Plan.RequiredFreeBytes)}，当前 {result.DriveName} 仅剩 {FormatBytes(result.AvailableFreeSpace)}。目标目录：{result.ResolvedPath}";
    }

    public static string FormatBytes(long bytes)
    {
        const double KB = 1024d;
        const double MB = KB * 1024d;
        const double GB = MB * 1024d;

        if (bytes >= GB)
        {
            return $"{bytes / GB:F2} GB";
        }

        if (bytes >= MB)
        {
            return $"{bytes / MB:F2} MB";
        }

        if (bytes >= KB)
        {
            return $"{bytes / KB:F2} KB";
        }

        return $"{bytes} B";
    }

    public static double ToProgress(long downloadedBytes, long? totalBytes)
    {
        if (totalBytes is null || totalBytes <= 0)
        {
            return 0;
        }

        return Math.Clamp((double)downloadedBytes / totalBytes.Value, 0, 1);
    }

    public static bool DeleteFileIfExists(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool DeleteDirectoryIfExists(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return true;
            }

            DirectoryInfo directory = new(path);
            ClearDirectoryAttributes(directory);
            directory.Delete(recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? NormalizeExistingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) || Directory.Exists(fullPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool TryDeletePaths(params string?[] paths)
    {
        bool success = true;
        foreach (string? path in paths)
        {
            string? normalizedPath = NormalizeExistingPath(path);
            if (normalizedPath is null)
            {
                continue;
            }

            bool deleted = Directory.Exists(normalizedPath)
                ? DeleteDirectoryIfExists(normalizedPath)
                : DeleteFileIfExists(normalizedPath);
            success &= deleted;
        }

        return success;
    }

    public static string BuildResidualCacheHint(BslGameActionType actionType, string cachePath)
    {
        string actionName = GetActionDisplayName(actionType);
        return $"{actionName}后处理失败，已保留残留缓存。可在确认不再继续任务后手动清理：{cachePath}";
    }

    public static BslBackendIssueKind ClassifyIssue(string? status, string? detail)
    {
        string text = $"{status} {detail}".Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return BslBackendIssueKind.Unknown;
        }

        if (ContainsAny(text, "空间不足", "磁盘", "可用空间不足"))
        {
            return BslBackendIssueKind.DiskSpaceInsufficient;
        }

        if (ContainsAny(text, "残留缓存", "手动清理", "保留残留缓存"))
        {
            return BslBackendIssueKind.ResidualCache;
        }

        if (ContainsAny(text, "缺少安装路径", "目录不存在", "安装目录", "路径", "可执行文件", "启动路径"))
        {
            return BslBackendIssueKind.PathIssue;
        }

        if (ContainsAny(text, "正在运行", "请关闭", "游戏运行"))
        {
            return BslBackendIssueKind.GameRunning;
        }

        if (ContainsAny(text, "暂不可用", "暂未接入", "未开放", "未提供可复用", "未发现稳定可复用"))
        {
            return BslBackendIssueKind.CapabilityUnavailable;
        }

        if (ContainsAny(text, "获取资源失败", "未获取到", "资源清单", "版本信息", "修复清单"))
        {
            return BslBackendIssueKind.ResourceUnavailable;
        }

        if (ContainsAny(text, "校验失败", "文件校验失败", "损坏文件", "修复失败"))
        {
            return BslBackendIssueKind.VerificationFailure;
        }

        if (ContainsAny(text, "卸载失败", "卸载未启动", "卸载服务", "无法删除游戏目录", "删除失败", "文件或目录正被占用", "访问被拒绝", "拒绝访问"))
        {
            return BslBackendIssueKind.OperationFailure;
        }

        if (ContainsAny(text, "下载失败", "更新失败", "安装失败", "同步失败", "超时", "timeout", "network", "http", "io"))
        {
            return BslBackendIssueKind.DownloadFailure;
        }

        return BslBackendIssueKind.Unknown;
    }

    public static BslBackendSuggestedAction SuggestAction(BslBackendIssueKind issueKind)
    {
        return issueKind switch
        {
            BslBackendIssueKind.DownloadFailure => BslBackendSuggestedAction.Retry,
            BslBackendIssueKind.DiskSpaceInsufficient => BslBackendSuggestedAction.CheckDiskSpace,
            BslBackendIssueKind.PathIssue => BslBackendSuggestedAction.ChooseInstallPath,
            BslBackendIssueKind.ResidualCache => BslBackendSuggestedAction.CleanResidualCache,
            BslBackendIssueKind.GameRunning => BslBackendSuggestedAction.CloseGame,
            BslBackendIssueKind.CapabilityUnavailable => BslBackendSuggestedAction.CheckCapabilityNotice,
            BslBackendIssueKind.ResourceUnavailable => BslBackendSuggestedAction.RefreshResource,
            BslBackendIssueKind.VerificationFailure => BslBackendSuggestedAction.RepairFiles,
            BslBackendIssueKind.OperationFailure => BslBackendSuggestedAction.Retry,
            _ => BslBackendSuggestedAction.InspectDetails,
        };
    }

    private static long CalculateSafetyMargin(long payloadBytes, double safetyRatio, long minimumSafetyMarginBytes)
    {
        long normalizedPayloadBytes = Math.Max(0, payloadBytes);
        long normalizedMinimumSafetyBytes = Math.Max(0, minimumSafetyMarginBytes);
        double normalizedSafetyRatio = double.IsFinite(safetyRatio) ? Math.Max(0, safetyRatio) : 0.1d;
        long ratioBytes = normalizedPayloadBytes <= 0
            ? 0
            : (long)Math.Ceiling(normalizedPayloadBytes * normalizedSafetyRatio);

        return Math.Max(normalizedMinimumSafetyBytes, ratioBytes);
    }

    private static bool IsRetryableException(Exception ex)
    {
        return ex is HttpRequestException
               || ex is IOException
               || ex is TaskCanceledException
               || ex is TimeoutException;
    }

    private static bool ContainsAny(string source, params string[] values)
    {
        foreach (string value in values)
        {
            if (source.Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ClearDirectoryAttributes(DirectoryInfo directory)
    {
        foreach (FileInfo file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            file.Attributes = FileAttributes.Normal;
        }

        foreach (DirectoryInfo subDirectory in directory.EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            subDirectory.Attributes = FileAttributes.Normal;
        }

        directory.Attributes = FileAttributes.Normal;
    }

    private static string GetActionDisplayName(BslGameActionType actionType)
    {
        return actionType switch
        {
            BslGameActionType.Install => "安装",
            BslGameActionType.Update => "更新",
            BslGameActionType.Predownload => "预下载",
            BslGameActionType.Repair => "修复",
            BslGameActionType.Uninstall => "卸载",
            BslGameActionType.Import => "导入",
            BslGameActionType.Launch => "启动",
            _ => "下载",
        };
    }
}

internal sealed class BslDiskSpacePlan
{
    public string TargetPath { get; init; } = string.Empty;

    public long DownloadBytes { get; init; }

    public long StagingBytes { get; init; }

    public long PatchBytes { get; init; }

    public long FinalWriteBytes { get; init; }

    public long SafetyBytes { get; init; }

    public long PayloadBytes => DownloadBytes + StagingBytes + PatchBytes + FinalWriteBytes;

    public long RequiredFreeBytes => PayloadBytes + SafetyBytes;
}

internal sealed class BslDiskSpaceCheckResult
{
    public bool IsSatisfied { get; init; }

    public string DriveName { get; init; } = string.Empty;

    public string ResolvedPath { get; init; } = string.Empty;

    public long AvailableFreeSpace { get; init; }

    public string? ErrorMessage { get; init; }

    public BslDiskSpacePlan Plan { get; init; } = new();
}
