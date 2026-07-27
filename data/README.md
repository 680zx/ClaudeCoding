# Data folder

This is LEAN's data folder (`Config.Set("data-folder", ...)`). Two different
kinds of thing live here.

## Reference databases — committed

- `market-hours/market-hours-database.json`
- `symbol-properties/symbol-properties-database.csv`

These are LEAN's own database **files**, taken from
`https://raw.githubusercontent.com/QuantConnect/Lean/master/Data/`. LEAN cannot
construct a crypto security without them, and the NuGet packages do not ship
them — they are distributed with the repository's `Data/` folder rather than in
the assemblies.

They are data, not engine code, so vendoring them does not weaken the
"LEAN from NuGet, never cloned" constraint: the engine itself is still resolved
entirely through NuGet.

They also matter for correctness rather than just startup. The BTCUSDT row gives
Binance's real filters:

```
binance,BTCUSDT,crypto,BTCUSDT,USDT,1,0.01,0.00001,BTCUSDT,5
                                     ^tick ^lot            ^min notional (USDT)
```

`ExchangeFilterGate` enforces exactly those numbers, in backtest as well as live,
so a backtest cannot fill an order Binance would have rejected.

## Market data — not committed

`crypto/binance/hour/*.zip` is regenerated rather than stored in git:

```bash
dotnet run --project tools/Parallels.DataTool -- \
    --symbol BTCUSDT --interval 1h --start 2023-01 --end 2024-12 --data-folder ./data
```

The tool downloads Binance's own published monthly kline archives from
`https://data.binance.vision` and writes them through LEAN's `LeanData` helpers,
so the on-disk layout and CSV format come from LEAN itself rather than from a
hand-rolled convention. Getting either subtly wrong produces a run that reads
zero bars and reports a flat equity curve — a silent failure, which is worse than
a loud one.

`PROVENANCE.txt` is written alongside the data and is copied into every backtest
result, so a report can always be traced to the exact archives that produced it.
