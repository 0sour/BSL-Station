using Microsoft.Extensions.Logging;
using Starward.Core.HoYoPlay;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.BSL.Services;

internal sealed class BslOfficialBackgroundService
{
    private readonly HttpClient _httpClient = AppConfig.GetService<HttpClient>();
    private readonly ILogger<BslOfficialBackgroundService> _logger = AppConfig.GetLogger<BslOfficialBackgroundService>();


    public async Task<GameBackground?> TryGetBackgroundAsync(string gameId, CancellationToken cancellationToken)
    {
        return gameId switch
        {
            "wuthering-waves" => await GetWutheringWavesBackgroundAsync(cancellationToken),
            "arknights" => await GetArknightsBackgroundAsync(cancellationToken),
            "arknights-endfield" => await GetEndfieldBackgroundAsync(cancellationToken),
            _ => null,
        };
    }


    private async Task<GameBackground?> GetWutheringWavesBackgroundAsync(CancellationToken cancellationToken)
    {
        const string launcherIndexUrl = "https://prod-cn-alicdn-gamestarter.kurogame.com/launcher/launcher/10003_Y8xXrXk65DqFHEDgApn3cpK5lfczpFx5/G152/index.json";

        try
        {
            WuwaLauncherIndexResponse? index = await _httpClient.GetFromJsonAsync<WuwaLauncherIndexResponse>(launcherIndexUrl, cancellationToken);
            string? backgroundCode = index?.FunctionCode?.Background;
            if (string.IsNullOrWhiteSpace(backgroundCode))
            {
                return null;
            }

            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string detailUrl = $"https://prod-cn-alicdn-gamestarter.kurogame.com/launcher/10003_Y8xXrXk65DqFHEDgApn3cpK5lfczpFx5/G152/background/{backgroundCode}/zh-Hans.json?_t={timestamp}";
            WuwaBackgroundResponse? detail = await _httpClient.GetFromJsonAsync<WuwaBackgroundResponse>(detailUrl, cancellationToken);

            if (string.IsNullOrWhiteSpace(detail?.BackgroundFile) || string.IsNullOrWhiteSpace(detail.Slogan))
            {
                return null;
            }

            return new GameBackground
            {
                Id = "bsl_wuwa_official",
                Type = GameBackground.BACKGROUND_TYPE_VIDEO,
                Background = new GameImage { Url = detail.Slogan },
                Theme = new GameImage { Url = detail.Slogan },
                Video = new GameImage { Url = detail.BackgroundFile },
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Load official Wuthering Waves background failed.");
            return null;
        }
    }


    private Task<GameBackground?> GetArknightsBackgroundAsync(CancellationToken cancellationToken)
    {
        return GetHypergryphBackgroundAsync(
            apiUrl: "https://launcher.hypergryph.com/api/proxy/web/batch_proxy",
            appCode: "GzD1CpaWgmSq1wew",
            channel: "1",
            subChannel: "1",
            language: "zh-cn",
            backgroundId: "bsl_arknights_official",
            cancellationToken: cancellationToken);
    }


    private Task<GameBackground?> GetEndfieldBackgroundAsync(CancellationToken cancellationToken)
    {
        return GetHypergryphBackgroundAsync(
            apiUrl: "https://launcher.hypergryph.com/api/proxy/web/batch_proxy",
            appCode: "6LL0KJuqHBVz33WK",
            channel: "1",
            subChannel: "1",
            language: "zh-cn",
            backgroundId: "bsl_endfield_official",
            cancellationToken: cancellationToken);
    }


    private async Task<GameBackground?> GetHypergryphBackgroundAsync(
        string apiUrl,
        string appCode,
        string channel,
        string subChannel,
        string language,
        string backgroundId,
        CancellationToken cancellationToken)
    {
        try
        {
            HypergryphBatchRequest request = new()
            {
                Seq = "5",
                ProxyReqs =
                [
                    new HypergryphProxyRequest
                    {
                        Kind = "get_main_bg_image",
                        GetMainBgImageReq = new HypergryphCommonRequest
                        {
                            AppCode = appCode,
                            Language = language,
                            Channel = channel,
                            SubChannel = subChannel,
                        }
                    }
                ]
            };

            using StringContent content = new(System.Text.Json.JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync(apiUrl, content, cancellationToken);
            response.EnsureSuccessStatusCode();
            HypergryphBatchResponse? body = await response.Content.ReadFromJsonAsync<HypergryphBatchResponse>(cancellationToken);
            HypergryphMainBgImage? bg = body?.ProxyRsps?[0]?.GetMainBgImageRsp?.MainBgImage;
            if (string.IsNullOrWhiteSpace(bg?.Url))
            {
                return null;
            }

            return new GameBackground
            {
                Id = backgroundId,
                Type = string.IsNullOrWhiteSpace(bg.VideoUrl) ? GameBackground.BACKGROUND_TYPE_POSTER : GameBackground.BACKGROUND_TYPE_VIDEO,
                Background = new GameImage { Url = bg.Url },
                Theme = new GameImage { Url = bg.Url },
                Video = new GameImage { Url = bg.VideoUrl ?? string.Empty },
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Load official Hypergryph background failed for {AppCode}.", appCode);
            return null;
        }
    }


    private sealed class WuwaLauncherIndexResponse
    {
        [JsonPropertyName("functionCode")]
        public WuwaFunctionCode? FunctionCode { get; set; }
    }

    private sealed class WuwaFunctionCode
    {
        [JsonPropertyName("background")]
        public string? Background { get; set; }
    }

    private sealed class WuwaBackgroundResponse
    {
        [JsonPropertyName("backgroundFile")]
        public string? BackgroundFile { get; set; }

        [JsonPropertyName("slogan")]
        public string? Slogan { get; set; }
    }

    private sealed class HypergryphBatchRequest
    {
        [JsonPropertyName("seq")]
        public string Seq { get; set; } = "5";

        [JsonPropertyName("proxy_reqs")]
        public HypergryphProxyRequest[] ProxyReqs { get; set; } = [];
    }

    private sealed class HypergryphProxyRequest
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("get_main_bg_image_req")]
        public HypergryphCommonRequest? GetMainBgImageReq { get; set; }
    }

    private sealed class HypergryphCommonRequest
    {
        [JsonPropertyName("appcode")]
        public string AppCode { get; set; } = string.Empty;

        [JsonPropertyName("language")]
        public string Language { get; set; } = "zh-cn";

        [JsonPropertyName("channel")]
        public string Channel { get; set; } = string.Empty;

        [JsonPropertyName("sub_channel")]
        public string SubChannel { get; set; } = string.Empty;

        [JsonPropertyName("platform")]
        public string Platform { get; set; } = "Windows";

        [JsonPropertyName("source")]
        public string Source { get; set; } = "launcher";
    }

    private sealed class HypergryphBatchResponse
    {
        [JsonPropertyName("proxy_rsps")]
        public HypergryphProxyResponse[]? ProxyRsps { get; set; }
    }

    private sealed class HypergryphProxyResponse
    {
        [JsonPropertyName("get_main_bg_image_rsp")]
        public HypergryphMainBgImageResponse? GetMainBgImageRsp { get; set; }
    }

    private sealed class HypergryphMainBgImageResponse
    {
        [JsonPropertyName("main_bg_image")]
        public HypergryphMainBgImage? MainBgImage { get; set; }
    }

    private sealed class HypergryphMainBgImage
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("video_url")]
        public string? VideoUrl { get; set; }
    }
}
