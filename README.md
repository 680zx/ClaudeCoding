# Parallels

A LEAN-based trading system with a backtest pipeline, a live Binance worker, an
ASP.NET Core API and a React frontend. Built to the v2 rebuild specification.

LEAN is a hard dependency, taken from NuGet only and never cloned. Every LEAN
package is pinned to a single version (`2.5.17924`) in `Directory.Packages.props`.

---

## What was built, and what was actually proven

| Phase | State | Proof |
|---|---|---|
| **0 — Skeleton** | Complete | Six projects, dependency direction grep-verified below, LEAN pinned, TFM `net10.0` matched to LEAN's own target |
| **1 — Backtest** | Complete and proven | Real LEAN statistics on real Binance data, chart markers matching LEAN's order log exactly, parameter changes without a rebuild, Save persisting a config, one warm dispatcher handling two jobs in two processes |
| **2 — Live, one alpha** | Built; **not proven against Testnet** | Mainnet gate demonstrated refusing and passing; idempotency, exchange filters and protective orders covered by tests. Binance is geo-blocked from this environment — see below |
| **3 — Live, multi-alpha** | Built; gates proven by test, UI proven in browser | Live tab CRUD driven through the real UI; exposure sharing, fault isolation and rate limiting proven by 34 passing tests |
| **4 — Stretch** | Partially done | Saved backtest config → "Use for a live alpha" is wired. Walk-forward validation is **not** built |

### The honest headline: the first backtest result was wrong

An early run reported **+49% return with a 100% win rate**. That was not edge; it
was three defects stacking up, each found by comparing what the engine actually
did against what the report claimed:

1. **The job packet carried starting capital.** LEAN then seeded the cash book in
   its default currency (USD) *before* `Initialize` could set USDT. The account
   held USD it could not spend on a USDT-quoted pair, so every order was rejected
   for insufficient buying power — while the run still produced 16,766 order
   records and a clean-looking report.

2. **Exits never cancelled their entry insight.** Emitting a `Flat` insight does
   not supersede an active `Up` insight; the entry stayed live for its full
   period, so the portfolio model kept seeing a long view and merely re-weighted
   the position instead of closing it.

3. **The order gate treated a zero quantity as a rejection.** A zero target is a
   *flatten instruction*. Every exit was silently dropped before it became an
   order.

Together those meant **positions never closed**, and a trade that never closes
never books a loss — hence the 100% win rate. With exits actually executing, the
same parameters give **−3.6%** and a **36% win rate**, with most of the gross
edge going to 16,895 USDT of fees across 354 round trips.

This is exactly the case the specification's "don't trust a single in-sample
backtest" warning exists for, and it is worth stating plainly: the pretty number
came from a bug, not a strategy.

### The current result, and why it should still be distrusted

Adding the chop filter (`minCrossSeparationFraction`) cuts round trips and the
fee drag that comes with them:

| `minCrossSeparation` | Orders | Net return | Sharpe | Max DD | Fees (USDT) |
|---|---|---|---|---|---|
| 0.000 | 708 | −3.60% | −0.313 | 16.0% | 16,895 |
| 0.004 | 422 | +15.84% | 0.823 | 7.6% | 11,405 |
| 0.008 | 304 | +16.82% | 0.892 | 7.2% | 8,519 |

**That 0.008 figure is in-sample parameter selection and should not be treated as
an expected return.** It was chosen by looking at the same 2023–2024 window it is
reported on. BTC rose roughly 466% over that period while this strategy returned
16.8%, because it is long-only and capped at 25% of equity — so it is not
"beating the market" in any sense. Walk-forward / out-of-sample validation is the
phase-4 work that would make these numbers meaningful, and it is not built.

---

## Architecture

```
Parallels.Contracts   Ports and DTOs. References nothing — no LEAN, no ASP.NET.
Parallels.Strategies  ParallelsAlgorithm (the single QCAlgorithm subclass) + alpha models.
Parallels.Worker.Backtest      Long-running container; one LEAN process per job inside it.
Parallels.Worker.Live.Binance  Long-running; one broker account, N alphas, Binance only.
Parallels.Api         ASP.NET Core. References Contracts only; talks to workers as processes.
Parallels.Web         React SPA, HTTP only.
```

Dependency direction, verified rather than asserted:

```
$ grep ProjectReference in each csproj
Parallels.Contracts        -> (nothing)
Parallels.Strategies       -> Contracts
Parallels.Worker.Backtest  -> Contracts, Strategies
Parallels.Worker.Live.*    -> Contracts, Strategies
Parallels.Api              -> Contracts

$ grep -r "QuantConnect" src/Parallels.Contracts --include=*.cs | wc -l
0
$ grep -r "using QuantConnect\|using Parallels.Strategies" src/Parallels.Api --include=*.cs | wc -l
0
```

### Why there is exactly one algorithm class

LEAN requires exactly one class derived from the algorithm base type per loaded
assembly, and constructs it by reflection with no constructor injection. So:

- Strategies are `AlphaModel`s registered with `AddAlpha`, never `QCAlgorithm`
  subclasses.
- Configuration reaches the algorithm through `SessionContext`, a process-wide
  static. `SessionContext.Publish` throws on a second call — one process is one
  LEAN engine session, because `Composer`, `Config` and the handler singletons
  are all process-wide.
- Backtest and live load the **same** `ParallelsAlgorithm`, so a strategy cannot
  quietly behave differently between them.

### The order gate

Alphas emit insights and never place orders. Every portfolio target passes
through one chain before an order exists, so risk controls cannot be bypassed by
construction:

```
backtest:  ExchangeFilterGate
live:      IdempotencyGuard -> RiskGateway -> RateLimitGate -> ExchangeFilterGate
```

Exchange-filter rounding runs in backtest **as well as** live, so a backtest
cannot fill orders Binance would have rejected.

---

## Running it

Requires the .NET 10 SDK and Node 22.

```bash
# 1. Fetch real market data (Binance's own public archives) into LEAN's format
dotnet run --project tools/Parallels.DataTool -- \
    --symbol BTCUSDT --interval 1h --start 2023-01 --end 2024-12 --data-folder ./data

# 2. Build
dotnet build Parallels.slnx

# 3. API
PARALLELS_ROOT=$PWD ASPNETCORE_URLS=http://127.0.0.1:5080 \
    dotnet run --project src/Parallels.Api

# 4. Frontend
cd src/Parallels.Web && npm install && npm run dev    # http://localhost:5173

# 5. Tests
dotnet test tests/Parallels.Tests
```

A backtest can also be run directly, without the API:

```bash
dotnet run --project src/Parallels.Worker.Backtest -- \
    --job "$(cat job.json)" --data-folder ./data --results ./results --verbose
```

### Docker

```bash
# Data must exist on the host first — containers mount it, they do not fetch it.
dotnet run --project tools/Parallels.DataTool -- \
    --symbol BTCUSDT --interval 1h --start 2023-01 --end 2024-12 --data-folder ./data
mkdir -p results state

docker compose -f deploy/docker-compose.yml up -d --build
```

UI on `http://localhost:5173`, API on `http://localhost:5080`. Override
`PARALLELS_API_PORT` / `PARALLELS_WEB_PORT` if those collide.

Confirm dispatch is actually going through Docker rather than falling back:

```bash
curl -s http://localhost:5080/api/system | jq .dispatchMechanism
# "docker exec into container 'parallels-worker-backtest'"
```

`worker-backtest` stays up idle and each job is a `docker exec` into it — a
fresh process per job without recreating the container.
`worker-live-binance` is built but never started by compose: the API owns its
lifecycle, launching it when the first alpha is enabled.

**For live trading only**, copy `deploy/.env.example` to `deploy/.env` and set
`PARALLELS_HOST_ROOT` to the absolute path of this checkout. The API launches
the live container with `docker run -v`, and the daemon resolves those paths
against the host rather than against the API container — without it the live
worker would mount the host's `/data`, find no market data, and never trade. The
API refuses to launch it with an explicit message instead of failing that way.
Backtesting needs none of this.

---

## Known limitations, stated plainly

**Docker was not available in the environment this was built and proven in.**
`docker` CLI is present but no daemon is reachable. Both dispatch paths are
implemented behind one abstraction; the **child-process fallback is what actually
ran** in every proof here, and `/api/system` and the UI header both report which
mechanism is live rather than assuming. The warm-container property was still
demonstrated in the form the fallback allows: one long-lived dispatcher (API pid
4859, unchanged across both jobs) ran two sequential jobs in two distinct worker
processes (pids 7695 and 7731) — the child-process analogue of `docker exec`
twice against one container id. The `docker exec` path itself has **not** been
executed.

**Binance is geo-blocked from this environment, so Phase 2 was not proven against
Testnet.** `api.binance.com` and `testnet.binance.vision` both return HTTP 451
here, and no API credentials were available. What that means concretely:

- *Proven:* the mainnet confirmation gate refusing without confirmation (exit
  code 3) and passing with it; the full configuration hand-off from API → launch
  environment variable → worker → parsed alpha set; the worker correctly refusing
  to start without credentials.
- *Not proven:* a real Testnet connection, a resting protective order visible on
  the Testnet order book, a duplicate submission caught against the live venue,
  and reconciliation reading real holdings back. The idempotency guard, exchange
  filters and protective-order logic are covered by tests against a real
  `QCAlgorithm` with LEAN's real Binance symbol properties, but that is not the
  same as a live venue confirming them.

**The shared `results/` volume assumes single-host deployment.** The API and every
worker container must see the same path. That is fine for the near-term
single-host target and is what `docker-compose.yml` sets up. A genuinely
multi-node deployment needs a network-shared volume or object storage instead,
and that is not built here.

**The API container mounts the Docker daemon socket.** That is how it execs into
the warm worker and manages the live container's lifecycle, and it grants control
of the host's Docker. Appropriate for local development; not for a shared or
internet-exposed host.

**Live exposure is attributed by symbol, not per alpha.** A portfolio target
reaches the execution layer *after* the portfolio construction model has merged
every active insight for that symbol, so when two alphas trade the same symbol
their individual contributions are genuinely not recoverable at that point. Each
symbol is attributed to one alpha; per-alpha limits are exact when alphas trade
distinct symbols, which is the configuration this is built for.

**Backtest stops are evaluated at the bar close, not intrabar.** The backtest does
not place resting protective orders, so claiming an intrabar stop filled at
exactly the stop price would assume an order that was never placed. Live *does*
place resting stop and take-profit orders, so live can stop out intrabar where the
backtest cannot. This is a real difference between the two, deliberately left
visible rather than papered over.

**LEAN's launcher job-queue and messaging implementations are not on NuGet.** Only
the interfaces ship in `QuantConnect.Common`, so both workers supply their own
single-job queue and a null messaging handler. This is a consequence of the
NuGet-only constraint and turns out to suit the design — the job already arrived
as a `--job` argument or an environment variable.

**LEAN pulls in transitive packages with known advisories** — `DotNetZip` 1.16.0
and `System.Drawing.Common` 4.7.0 both raise NuGet audit warnings. They come from
LEAN's own dependency graph, not from this project's direct references, and
cannot be resolved without LEAN publishing updated packages.

**Reference data is vendored from the LEAN repository.** `data/market-hours/` and
`data/symbol-properties/` are LEAN's own database *files*, downloaded from its
repo. They are data, not engine code — the NuGet packages do not ship them, and
LEAN cannot construct a crypto security without them. The engine dependency
remains NuGet-only.

**Not built at all:** walk-forward / out-of-sample validation, any second broker,
any second strategy type beyond trend following, and authentication on the API
(it is unauthenticated and assumes a trusted local network).
