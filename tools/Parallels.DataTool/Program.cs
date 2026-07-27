using System.Globalization;
using System.IO.Compression;
using QuantConnect;
using QuantConnect.Data.Market;
using QuantConnect.Util;

namespace Parallels.DataTool;

/// <summary>
/// Converts Binance's public monthly kline archives into LEAN's on-disk data
/// format.
///
/// The output path and the CSV line format both come from LEAN's own
/// <see cref="LeanData"/> helpers rather than from a hand-rolled convention.
/// Getting either subtly wrong produces a backtest that silently reads zero bars
/// and reports a flat equity curve, which is far worse than a hard failure.
///
/// Source: https://data.binance.vision — Binance's own published archives, so
/// the prices a backtest runs on are real exchange prints, not synthesized.
/// </summary>
public static class Program
{
    private const string ArchiveBase = "https://data.binance.vision/data/spot/monthly/klines";

    public static async Task<int> Main(string[] args)
    {
        var symbolTicker = Value(args, "--symbol") ?? "BTCUSDT";
        var interval = Value(args, "--interval") ?? "1h";
        var start = ParseMonth(Value(args, "--start") ?? "2023-01");
        var end = ParseMonth(Value(args, "--end") ?? "2024-12");
        var dataFolder = Path.GetFullPath(Value(args, "--data-folder") ?? "data");

        var resolution = interval switch
        {
            "1h" => Resolution.Hour,
            "1d" => Resolution.Daily,
            "1m" => Resolution.Minute,
            _ => throw new ArgumentException($"Unsupported interval '{interval}'. Use 1m, 1h or 1d.")
        };

        var period = resolution switch
        {
            Resolution.Hour => TimeSpan.FromHours(1),
            Resolution.Daily => TimeSpan.FromDays(1),
            _ => TimeSpan.FromMinutes(1)
        };

        var symbol = Symbol.Create(symbolTicker, SecurityType.Crypto, Market.Binance);

        Console.WriteLine($"[data] {symbolTicker} {interval} {start:yyyy-MM} .. {end:yyyy-MM} -> {dataFolder}");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var bars = new List<TradeBar>();
        var months = 0;

        for (var month = start; month <= end; month = month.AddMonths(1))
        {
            var url = $"{ArchiveBase}/{symbolTicker}/{interval}/{symbolTicker}-{interval}-{month:yyyy-MM}.zip";
            var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[data]   {month:yyyy-MM}: HTTP {(int)response.StatusCode}, skipped");
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = archive.Entries.Single(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));

            using var reader = new StreamReader(entry.Open());
            var monthBars = 0;
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;
                var fields = line.Split(',');

                // Recent archives carry a header row; older ones do not.
                if (!long.TryParse(fields[0], out var openTimeRaw)) continue;

                // Binance switched open_time from milliseconds to microseconds in
                // 2025 archives. Detect by magnitude rather than by date so both
                // vintages convert correctly.
                var openTime = openTimeRaw > 100_000_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(openTimeRaw / 1000).UtcDateTime
                    : DateTimeOffset.FromUnixTimeMilliseconds(openTimeRaw).UtcDateTime;

                bars.Add(new TradeBar(
                    openTime,
                    symbol,
                    decimal.Parse(fields[1], CultureInfo.InvariantCulture),
                    decimal.Parse(fields[2], CultureInfo.InvariantCulture),
                    decimal.Parse(fields[3], CultureInfo.InvariantCulture),
                    decimal.Parse(fields[4], CultureInfo.InvariantCulture),
                    decimal.Parse(fields[5], CultureInfo.InvariantCulture),
                    period));
                monthBars++;
            }

            months++;
            Console.WriteLine($"[data]   {month:yyyy-MM}: {monthBars} bars");
        }

        if (bars.Count == 0)
        {
            Console.Error.WriteLine("[data] no bars downloaded — nothing written.");
            return 1;
        }

        bars.Sort((a, b) => a.Time.CompareTo(b.Time));
        WriteLeanFile(dataFolder, symbol, resolution, bars);

        var provenance =
            $"Binance public monthly klines ({interval}) from {ArchiveBase}, " +
            $"{symbolTicker} {start:yyyy-MM}..{end:yyyy-MM}, {months} monthly archives, " +
            $"{bars.Count} bars, converted {DateTimeOffset.UtcNow:u} using LeanData.GenerateLine.";
        await File.WriteAllTextAsync(Path.Combine(dataFolder, "PROVENANCE.txt"), provenance);

        Console.WriteLine($"[data] wrote {bars.Count} bars ({bars[0].Time:u} .. {bars[^1].Time:u})");
        return 0;
    }

    private static void WriteLeanFile(string dataFolder, Symbol symbol, Resolution resolution, List<TradeBar> bars)
    {
        // Hour and Daily collapse to a single zip covering every date, so the
        // date argument is only meaningful for intraday resolutions.
        var zipPath = LeanData.GenerateZipFilePath(dataFolder, symbol, bars[0].Time, resolution, TickType.Trade);
        var entryName = LeanData.GenerateZipEntryName(symbol, bars[0].Time, resolution, TickType.Trade);

        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        if (File.Exists(zipPath)) File.Delete(zipPath);

        using var file = File.Create(zipPath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());

        foreach (var bar in bars)
            writer.WriteLine(LeanData.GenerateLine(bar, SecurityType.Crypto, resolution));

        Console.WriteLine($"[data] {zipPath} :: {entryName}");
    }

    private static DateTime ParseMonth(string value) =>
        DateTime.ParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture);

    private static string? Value(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
