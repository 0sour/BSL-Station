using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Starward.Core;
using Starward.Core.Gacha.Genshin;
using Starward.Core.Gacha.StarRail;
using Starward.Core.Gacha.ZZZ;
using Starward.Core.GameNotice;
using Starward.Core.GameRecord;
using Starward.Core.HoYoPlay;
using Starward.Core.SelfQuery;
using Starward.Features.Background;
using Starward.Features.BSL.Backend;
using Starward.Features.BSL.Backend.Adapters;
using Starward.Features.Database;
using Starward.Features.Gacha;
using Starward.Features.Gacha.UIGF;
using Starward.Features.GameAccount;
using Starward.Features.GameInstall;
using Starward.Features.GameLauncher;
using Starward.Features.GameRecord;
using Starward.Features.HoYoPlay;
using Starward.Features.PlayTime;
using Starward.Features.RPC;
using Starward.Features.Screenshot;
using Starward.Features.SelfQuery;
using Starward.Features.Update;
using Starward.Setup.Core;
using System;
using System.IO;
using System.Net;
using System.Net.Http;

namespace Starward;

public static partial class AppConfig
{

    private static IServiceProvider _serviceProvider;


    private static void BuildServiceProvider()
    {
        if (_serviceProvider == null)
        {
            var logFolder = Path.Combine(CacheFolder, "log");
            Directory.CreateDirectory(logFolder);
            LogFile = Path.Combine(logFolder, $"Starward_{DateTime.Now:yyMMdd}.log");
            Log.Logger = new LoggerConfiguration().WriteTo.File(path: LogFile, shared: true, outputTemplate: $$"""[{Timestamp:HH:mm:ss.fff}] [{Level:u4}] [{{Path.GetFileName(Environment.ProcessPath)}} ({{Environment.ProcessId}})] {SourceContext}{NewLine}{Message}{NewLine}{Exception}{NewLine}""")
                                                  .Enrich.FromLogContext()
                                                  .CreateLogger();
            Log.Information($"Welcome to Starward v{AppVersion}\r\nSystem: {Environment.OSVersion}\r\nCommand Line: {Environment.CommandLine}");

            var sc = new ServiceCollection();
            sc.AddMemoryCache();
            sc.AddLogging(c => c.AddSerilog(Log.Logger));
            sc.AddHttpClient().ConfigureHttpClientDefaults(ConfigDefaultHttpClient);

            sc.AddSingleton<HoYoPlayClient>();
            sc.AddSingleton<GameNoticeClient>();
            sc.AddSingleton<HoYoPlayService>();
            sc.AddSingleton<BackgroundService>();
            sc.AddSingleton<GameLauncherService>();
            sc.AddSingleton<GamePackageService>();
            sc.AddSingleton<PlayTimeService>();
            sc.AddSingleton<GameNoticeService>();
            sc.AddSingleton<SetupService>();

            sc.AddSingleton<GenshinGachaClient>();
            sc.AddSingleton<StarRailGachaClient>();
            sc.AddSingleton<ZZZGachaClient>();
            sc.AddSingleton<GenshinGachaService>();
            sc.AddSingleton<StarRailGachaService>();
            sc.AddSingleton<ZZZGachaService>();
            sc.AddSingleton<UIGFGachaService>();
            sc.AddSingleton<GenshinBeyondGachaClient>();
            sc.AddSingleton<GenshinBeyondGachaService>();

            sc.AddSingleton<HoyolabClient>();
            sc.AddSingleton<HyperionClient>();
            sc.AddSingleton<GameRecordService>();

            sc.AddSingleton<SelfQueryClient>();
            sc.AddSingleton<SelfQueryService>();

            sc.AddHttpClient<ReleaseClient>().ConfigStarwardHttpClient();
            sc.AddTransient<UpdateService>();

            sc.AddSingleton<RpcService>();
            sc.AddSingleton<GameInstallService>();

            sc.AddSingleton<GameAuthLoginService>();
            sc.AddSingleton<GameAccountService>();

            sc.AddSingleton<ScreenCaptureService>();

            sc.AddHttpClient<LogUploadClient>().ConfigStarwardHttpClient();

            sc.AddSingleton<IBslGameAdapter>(sp => new MiHoYoBslAdapter(
                gameKey: "genshin",
                displayName: "原神",
                gameId: GameId.FromGameBiz(GameBiz.hk4e_cn)!,
                hoYoPlayService: sp.GetRequiredService<HoYoPlayService>(),
                gameLauncherService: sp.GetRequiredService<GameLauncherService>(),
                gameInstallService: sp.GetRequiredService<GameInstallService>(),
                gamePackageService: sp.GetRequiredService<GamePackageService>()));
            sc.AddSingleton<IBslGameAdapter>(sp => new MiHoYoBslAdapter(
                gameKey: "starrail",
                displayName: "崩坏：星穹铁道",
                gameId: GameId.FromGameBiz(GameBiz.hkrpg_cn)!,
                hoYoPlayService: sp.GetRequiredService<HoYoPlayService>(),
                gameLauncherService: sp.GetRequiredService<GameLauncherService>(),
                gameInstallService: sp.GetRequiredService<GameInstallService>(),
                gamePackageService: sp.GetRequiredService<GamePackageService>()));
            sc.AddSingleton<IBslGameAdapter>(sp => new MiHoYoBslAdapter(
                gameKey: "zenless",
                displayName: "绝区零",
                gameId: GameId.FromGameBiz(GameBiz.nap_cn)!,
                hoYoPlayService: sp.GetRequiredService<HoYoPlayService>(),
                gameLauncherService: sp.GetRequiredService<GameLauncherService>(),
                gameInstallService: sp.GetRequiredService<GameInstallService>(),
                gamePackageService: sp.GetRequiredService<GamePackageService>()));
            sc.AddSingleton<IBslGameAdapter>(sp => new HypergryphBslAdapter(
                gameKey: "arknights",
                displayName: "明日方舟",
                region: BslGameServerRegion.China,
                apiUrl: "https://launcher.hypergryph.com/api/proxy/batch_proxy",
                webApiUrl: "https://launcher.hypergryph.com/api/proxy/web/batch_proxy",
                appCode: "GzD1CpaWgmSq1wew",
                launcherAppCode: "abYeZZ16BPluCFyT",
                launcherTa: "arknights",
                channel: "1",
                subChannel: "1",
                seq: "5",
                registryKeyName: "Arknights",
                executableName: "Arknights.exe",
                launcherFolderName: "Arknights Game",
                homePage: "https://ak.hypergryph.com/",
                httpClient: sp.GetRequiredService<HttpClient>()));
            sc.AddSingleton<IBslGameAdapter>(sp => new HypergryphBslAdapter(
                gameKey: "arknights-endfield",
                displayName: "明日方舟：终末地",
                region: BslGameServerRegion.China,
                apiUrl: "https://launcher.hypergryph.com/api/proxy/batch_proxy",
                webApiUrl: "https://launcher.hypergryph.com/api/proxy/web/batch_proxy",
                appCode: "6LL0KJuqHBVz33WK",
                launcherAppCode: "abYeZZ16BPluCFyT",
                launcherTa: "endfield",
                channel: "1",
                subChannel: "1",
                seq: "5",
                registryKeyName: "Endfield",
                executableName: "Endfield.exe",
                launcherFolderName: "Arknights Endfield Game",
                homePage: "https://endfield.hypergryph.com/",
                httpClient: sp.GetRequiredService<HttpClient>()));
            sc.AddSingleton<IBslGameAdapter>(sp => new WutheringWavesBslAdapter(sp.GetRequiredService<HttpClient>()));
            sc.AddSingleton<BslCustomGameService>();
            sc.AddSingleton<BslBackendService>();


            _serviceProvider = sc.BuildServiceProvider();
        }
    }

    public static T GetService<T>()
    {
        BuildServiceProvider();
        return _serviceProvider.GetService<T>()!;
    }

    public static ILogger<T> GetLogger<T>()
    {
        BuildServiceProvider();
        return _serviceProvider.GetService<ILogger<T>>()!;
    }

    public static SqliteConnection CreateDatabaseConnection()
    {
        return DatabaseService.CreateConnection();
    }


    private static void ConfigDefaultHttpClient(this IHttpClientBuilder builder)
    {
        builder.RemoveAllLoggers();
        builder.ConfigureHttpClient(client =>
        {
            client.DefaultRequestHeaders.Clear();
#if DEBUG
            client.DefaultRequestHeaders.Add("User-Agent", $"Starward.Debug/{AppVersion}");
#else
            client.DefaultRequestHeaders.Add("User-Agent", $"Starward/{AppVersion}");
#endif
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        });
        builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            EnableMultipleHttp2Connections = true,
            EnableMultipleHttp3Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        });
    }


    private static void ConfigStarwardHttpClient(this IHttpClientBuilder builder)
    {
        builder.RemoveAllLoggers();
        builder.ConfigureHttpClient(client =>
        {
            client.DefaultRequestHeaders.Clear();
#if DEBUG
            client.DefaultRequestHeaders.Add("User-Agent", $"Starward.Debug/{AppVersion}");
#else
            client.DefaultRequestHeaders.Add("User-Agent", $"Starward/{AppVersion}");
#endif
            client.DefaultRequestHeaders.Add("X-Sw-Device-Id", DeviceId.ToString());
            client.DefaultRequestHeaders.Add("X-Sw-Session-Id", SessionId.ToString());
            client.DefaultRequestHeaders.Add("X-Sw-App-Version", AppVersion);
            client.DefaultRequestHeaders.Add("X-Sw-App-Type", "Desktop");
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        });
        builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            EnableMultipleHttp2Connections = true,
            EnableMultipleHttp3Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        });
    }


}
