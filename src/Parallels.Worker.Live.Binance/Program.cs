using Parallels.Contracts;
using Parallels.Strategies;
using Parallels.Strategies.Execution;
using Parallels.Worker.Live.Binance.Risk;
using QuantConnect;
using QuantConnect.Configuration;
using QuantConnect.Lean.Engine;
using QuantConnect.Lean.Engine.Server;
using QuantConnect.Packets;
using QuantConnect.Util;

namespace Parallels.Worker.Live.Binance;

/// <summary>
/// Hosts one broker account's live session: N alphas in one process via the
/// Algorithm Framework.
///
/// One process per broker account, not per strategy (spec 3.2). Process-per-
/// strategy would duplicate the broker connection and market-data subscriptions
/// and would then need cross-process coordination to reinvent what LEAN's own
/// portfolio already does natively inside a single session.
///
/// This is a separate executable from the backtest worker on purpose: the setup,
/// result, data-feed and transaction handlers are all genuinely different code,
/// not a mode switch (spec 2).
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        LiveWorkerOptions options;
        try
        {
            options = LiveWorkerOptions.Parse(args);
        }
        catch (MainnetGateException ex)
        {
            // Distinct exit code so an operator (or a test) can tell "the gate
            // refused" apart from "the configuration was malformed".
            Console.Error.WriteLine($"[live] MAINNET GATE: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[live] configuration error: {ex.Message}");
            return 2;
        }

        Directory.CreateDirectory(options.OutputDirectory);

        var session = options.Session;
        Console.WriteLine($"[live] pid {Environment.ProcessId} session {session.SessionId}");
        Console.WriteLine($"[live] environment : {(options.IsMainnet ? "MAINNET" : "testnet")}");
        Console.WriteLine($"[live] alphas      : {session.Alphas.Count} enabled");
        foreach (var alpha in session.Alphas)
            Console.WriteLine($"[live]   - {alpha.Id} {alpha.Parameters.StrategyType} " +
                              $"{string.Join(",", alpha.Symbols)} maxExposure={alpha.MaxExposureFraction:P0}");
        Console.WriteLine($"[live] aggregate exposure cap {session.MaxAggregateExposureFraction:P0}, " +
                          $"order budget {session.MaxOrdersPerTenSeconds}/10s");

        var gate = BuildGate(session);

        // Preflight validates everything that does not require the exchange:
        // configuration, the mainnet gate, and the assembled gate chain. It
        // exists so the startup path can be exercised where outbound Binance
        // access or credentials are unavailable, without pretending a connection
        // was made.
        if (args.Contains("--preflight"))
        {
            Console.WriteLine("[live] preflight OK — configuration valid, mainnet gate passed, " +
                              "gate chain assembled. No brokerage connection attempted.");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.ApiSecret))
        {
            Console.Error.WriteLine(
                "[live] BINANCE_API_KEY / BINANCE_API_SECRET are not set. Refusing to start a live " +
                "session without credentials.");
            return 2;
        }

        try
        {
            RunEngine(options, gate);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[live] session failed: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// Assembles the chain every order passes through, in the order it must run.
    ///
    /// Idempotency first (never send a duplicate at all), then risk (how much may
    /// be committed), then the rate budget (may anything be sent right now), then
    /// exchange filters last so the surviving quantity is expressed in a form
    /// Binance will accept.
    /// </summary>
    private static IOrderGate BuildGate(LiveSessionConfig session)
    {
        var symbolToAlpha = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var perAlphaLimits = new Dictionary<string, decimal>();

        foreach (var alpha in session.Alphas)
        {
            perAlphaLimits[alpha.Id] = alpha.MaxExposureFraction;
            foreach (var ticker in alpha.Symbols)
                symbolToAlpha.TryAdd(ticker, alpha.Id);
        }

        void Log(string message) => Console.WriteLine(message);

        return new CompositeOrderGate(
            new IdempotencyGuard(TimeSpan.FromMinutes(2), Log),
            new RiskGateway(symbolToAlpha, perAlphaLimits, session.MaxAggregateExposureFraction, Log),
            new RateLimitGate(session.MaxOrdersPerTenSeconds, TimeSpan.FromSeconds(10), Log),
            new ExchangeFilterGate());
    }

    private static void RunEngine(LiveWorkerOptions options, IOrderGate gate)
    {
        ConfigureLean(options);

        SessionContext.Publish(new ParallelsSession
        {
            Mode = SessionMode.Live,
            Live = options.Session,
            OutputDirectory = options.OutputDirectory,
            OrderGate = gate,
            ProtectiveOrders = new ProtectiveOrderPolicy(Console.WriteLine),
        });

        var packet = BuildPacket(options);
        var strategiesAssemblyPath = typeof(ParallelsAlgorithm).Assembly.Location;

        var systemHandlers = new LeanEngineSystemHandlers(
            new SingleLiveJobQueue(packet, strategiesAssemblyPath),
            new QuantConnect.Api.Api(),
            new NullMessagingHandler(),
            new LocalLeanManager());

        systemHandlers.Initialize();

        var job = systemHandlers.JobQueue.NextJob(out var assemblyPath);
        var algorithmHandlers = LeanEngineAlgorithmHandlers.FromConfiguration(Composer.Instance, researchMode: false);
        var algorithmManager = new AlgorithmManager(liveMode: true, job);

        systemHandlers.LeanManager.Initialize(systemHandlers, algorithmHandlers, job, algorithmManager);

        try
        {
            // Reconciliation on start is LEAN's BrokerageSetupHandler doing its
            // job: it reads real holdings, cash and open orders from Binance and
            // seeds the algorithm's portfolio from them. That is what makes
            // restart-on-desired-state-change safe (spec 4.1) — the new session
            // inherits the account as it actually is rather than assuming flat.
            var engine = new Engine(systemHandlers, algorithmHandlers, liveMode: true);
            engine.Run(job, algorithmManager, assemblyPath, WorkerThread.Instance);
        }
        finally
        {
            systemHandlers.JobQueue.AcknowledgeJob(job);
            algorithmHandlers.Dispose();
            systemHandlers.Dispose();
        }
    }

    private static void ConfigureLean(LiveWorkerOptions options)
    {
        var strategiesAssembly = typeof(ParallelsAlgorithm).Assembly;

        Config.Set("environment", "live-binance");
        Config.Set("live-mode", "true");
        Config.Set("algorithm-type-name", nameof(ParallelsAlgorithm));
        Config.Set("algorithm-language", "CSharp");
        Config.Set("algorithm-location", Path.GetFileName(strategiesAssembly.Location));
        Config.Set("data-folder", options.DataFolder);
        Config.Set("results-destination-folder", options.OutputDirectory);
        Config.Set("algorithm-id", options.Session.SessionId);

        // The live handler set — genuinely different code from backtest, which is
        // exactly why this is a separate executable (spec 2).
        Config.Set("setup-handler", "QuantConnect.Lean.Engine.Setup.BrokerageSetupHandler");
        Config.Set("result-handler", "QuantConnect.Lean.Engine.Results.LiveTradingResultHandler");
        Config.Set("data-feed-handler", "QuantConnect.Lean.Engine.DataFeeds.LiveTradingDataFeed");
        Config.Set("real-time-handler", "QuantConnect.Lean.Engine.RealTime.LiveTradingRealTimeHandler");
        Config.Set("transaction-handler", "QuantConnect.Lean.Engine.TransactionHandlers.BrokerageTransactionHandler");
        Config.Set("data-provider", "QuantConnect.Lean.Engine.DataFeeds.DefaultDataProvider");
        Config.Set("map-file-provider", "QuantConnect.Data.Auxiliary.LocalDiskMapFileProvider");
        Config.Set("factor-file-provider", "QuantConnect.Data.Auxiliary.LocalDiskFactorFileProvider");
        Config.Set("object-store", "QuantConnect.Lean.Engine.Storage.LocalObjectStore");
        Config.Set("data-permission-manager", "QuantConnect.Lean.Engine.DataFeeds.DataPermissionManager");
        Config.Set("data-queue-handler", "BinanceBrokerage");
        Config.Set("live-mode-brokerage", "BinanceBrokerage");

        Config.Set("binance-api-key", options.ApiKey ?? "");
        Config.Set("binance-api-secret", options.ApiSecret ?? "");

        // Testnet and mainnet differ only by endpoint. Naming both explicitly
        // keeps "which venue is this actually pointed at" answerable by reading
        // the config rather than by inferring it from a flag elsewhere.
        if (options.IsMainnet)
        {
            Config.Set("binance-api-url", "https://api.binance.com");
            Config.Set("binance-websocket-url", "wss://stream.binance.com:9443/ws");
        }
        else
        {
            Config.Set("binance-api-url", "https://testnet.binance.vision");
            Config.Set("binance-websocket-url", "wss://testnet.binance.vision/ws");
        }
    }

    private static LiveNodePacket BuildPacket(LiveWorkerOptions options) => new()
    {
        Language = Language.CSharp,
        // AlgorithmId is derived from DeployId on a live packet, so setting the
        // deploy id is what names the session.
        DeployId = options.Session.SessionId,
        Brokerage = "BinanceBrokerage",
        DataQueueHandler = "BinanceBrokerage",
        Controls = new Controls
        {
            MaximumDataPointsPerChartSeries = 1_000_000,
            MaximumChartSeries = 100,
        },
        BrokerageData = new Dictionary<string, string>
        {
            ["binance-api-key"] = options.ApiKey ?? "",
            ["binance-api-secret"] = options.ApiSecret ?? "",
            ["binance-api-url"] = Config.Get("binance-api-url", ""),
            ["binance-websocket-url"] = Config.Get("binance-websocket-url", ""),
        },
    };
}
