using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Configuration;
using QuantConnect.Data.Market;
using QuantConnect.Securities;
using QuantConnect.Securities.Crypto;

namespace Parallels.Tests;

/// <summary>
/// A real <see cref="QCAlgorithm"/> with real Binance crypto securities attached.
///
/// Securities are constructed directly rather than through <c>AddCrypto</c>,
/// which needs LEAN's internal data-manager wiring that only a running engine
/// supplies. What matters for these tests is preserved either way: the exchange
/// hours and symbol properties come from LEAN's own databases, so the lot size
/// (0.00001) and minimum notional (5 USDT) the filters are checked against are
/// Binance's actual values, not numbers invented to make a test pass.
/// </summary>
public sealed class AlgorithmFixture
{
    private readonly MarketHoursDatabase _marketHours;
    private readonly SymbolPropertiesDatabase _symbolProperties;

    public QCAlgorithm Algorithm { get; }
    public Symbol Symbol { get; }

    public AlgorithmFixture(string ticker = "BTCUSDT", decimal cash = 100_000m, decimal price = 50_000m)
    {
        Config.Set("data-folder", LocateDataFolder());
        Globals.Reset();

        _marketHours = MarketHoursDatabase.FromDataFolder();
        _symbolProperties = SymbolPropertiesDatabase.FromDataFolder();

        Algorithm = new QCAlgorithm();
        Algorithm.SetStartDate(2024, 1, 1);
        Algorithm.SetEndDate(2024, 6, 30);
        Algorithm.SetAccountCurrency("USDT");
        Algorithm.SetCash(cash);

        Symbol = Add(ticker, price).Symbol;
    }

    /// <summary>Attaches another symbol, so multi-alpha exposure sharing can be exercised.</summary>
    public Security Add(string ticker, decimal price)
    {
        var symbol = QuantConnect.Symbol.Create(ticker, SecurityType.Crypto, Market.Binance);

        var properties = _symbolProperties.GetSymbolProperties(
            Market.Binance, symbol, SecurityType.Crypto, "USDT");

        var exchangeHours = _marketHours.GetExchangeHours(Market.Binance, symbol, SecurityType.Crypto);

        Crypto.DecomposeCurrencyPair(symbol, properties, out var baseCurrency, out var quoteCurrency);

        // Add each currency only if absent. CashBook.Add overwrites, so adding
        // the quote currency unconditionally would wipe out the account cash set
        // in the constructor — leaving equity at zero, which makes every gate
        // that scales limits by equity silently approve everything.
        var cashBook = Algorithm.Portfolio.CashBook;
        if (!cashBook.ContainsKey(quoteCurrency)) cashBook.Add(quoteCurrency, 0m, 1m);
        if (!cashBook.ContainsKey(baseCurrency)) cashBook.Add(baseCurrency, 0m, price);

        var security = new Crypto(
            symbol,
            exchangeHours,
            cashBook[quoteCurrency],
            cashBook[baseCurrency],
            properties,
            Algorithm.Portfolio.CashBook,
            RegisteredSecurityDataTypesProvider.Null,
            new SecurityCache());

        Algorithm.Securities.Add(security);
        SetPrice(symbol, price);
        return security;
    }

    public void SetPrice(Symbol symbol, decimal price) =>
        Algorithm.Securities[symbol].SetMarketPrice(
            new TradeBar(Algorithm.Time, symbol, price, price, price, price, 1m, TimeSpan.FromHours(1)));

    /// <summary>Gives the algorithm a position without routing an order through the gates.</summary>
    public void SetHoldings(Symbol symbol, decimal quantity, decimal averagePrice) =>
        Algorithm.Portfolio[symbol].SetHoldings(averagePrice, quantity);

    private static string LocateDataFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "data");
            if (Directory.Exists(Path.Combine(candidate, "market-hours"))) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the repository data folder containing market-hours/.");
    }
}
