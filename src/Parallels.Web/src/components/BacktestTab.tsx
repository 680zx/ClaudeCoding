import { useEffect, useMemo, useState } from 'react';
import { api, pollUntilComplete } from '../api';
import type { BacktestResult, ChartData, StrategyDescriptor, StrategyParameters } from '../types';
import { ParameterForm, defaultsFor } from './ParameterForm';
import { PriceChart } from './PriceChart';
import { SaveModal } from './SaveModal';

export function BacktestTab({ strategies, symbols }: { strategies: StrategyDescriptor[]; symbols: string[] }) {
  const [symbol, setSymbol] = useState(symbols[0] ?? '');
  const [strategyType, setStrategyType] = useState(strategies[0]?.strategyType ?? '');
  const [startDate, setStartDate] = useState('2023-01-15');
  const [endDate, setEndDate] = useState('2024-12-30');
  const [startingCash, setStartingCash] = useState(100000);
  const [feeFraction, setFeeFraction] = useState(0.001);
  const [slippageFraction, setSlippageFraction] = useState(0.0005);

  const descriptor = useMemo(
    () => strategies.find((s) => s.strategyType === strategyType),
    [strategies, strategyType],
  );

  const [parameters, setParameters] = useState<StrategyParameters>(
    descriptor ? defaultsFor(descriptor) : { strategyType: '' },
  );

  const [result, setResult] = useState<BacktestResult | null>(null);
  const [chart, setChart] = useState<ChartData | null>(null);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saveOpen, setSaveOpen] = useState(false);

  useEffect(() => {
    if (descriptor) setParameters(defaultsFor(descriptor));
  }, [descriptor]);

  // Both lists arrive from the API after the first render, so the initial state
  // is necessarily empty. Without these, the selects would show a strategy while
  // the state behind them stayed blank — and the parameter form, which keys off
  // that state, would never appear.
  useEffect(() => {
    if (!symbol && symbols.length > 0) setSymbol(symbols[0]);
  }, [symbols, symbol]);

  useEffect(() => {
    if (strategies.length === 0) return;
    if (!strategies.some((s) => s.strategyType === strategyType)) {
      setStrategyType(strategies[0].strategyType);
    }
  }, [strategies, strategyType]);

  const run = async () => {
    setRunning(true);
    setError(null);
    setChart(null);
    setResult(null);

    try {
      const submitted = await api.submitBacktest({
        symbol,
        resolution: 'Hour',
        startDate,
        endDate,
        startingCash,
        feeFraction,
        slippageFraction,
        parameters,
        name: `${strategyType} ${symbol}`,
      });

      const final = await pollUntilComplete(submitted.jobId, setResult);

      if (final.status === 'Completed' && final.hasChartData) {
        setChart(await api.getChart(final.jobId));
      } else if (final.status === 'Failed') {
        setError(final.error ?? 'The run failed without reporting a reason.');
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setRunning(false);
    }
  };

  return (
    <div className="tab">
      <section className="panel">
        <h2>Run a backtest</h2>

        <div className="control-row">
          <label className="field">
            <span className="field-label">Symbol</span>
            <select value={symbol} onChange={(e) => setSymbol(e.target.value)}>
              {symbols.map((s) => <option key={s} value={s}>{s}</option>)}
            </select>
          </label>

          <label className="field">
            <span className="field-label">Strategy</span>
            <select value={strategyType} onChange={(e) => setStrategyType(e.target.value)}>
              {strategies.map((s) => (
                <option key={s.strategyType} value={s.strategyType}>{s.displayName}</option>
              ))}
            </select>
          </label>

          <label className="field">
            <span className="field-label">Start</span>
            <input type="date" value={startDate} onChange={(e) => setStartDate(e.target.value)} />
          </label>

          <label className="field">
            <span className="field-label">End</span>
            <input type="date" value={endDate} onChange={(e) => setEndDate(e.target.value)} />
          </label>

          <label className="field">
            <span className="field-label">Starting cash</span>
            <input type="number" value={startingCash} step={1000}
              onChange={(e) => setStartingCash(Number(e.target.value))} />
          </label>

          <label className="field">
            <span className="field-label">Fee</span>
            <input type="number" value={feeFraction} step={0.0001}
              onChange={(e) => setFeeFraction(Number(e.target.value))} />
          </label>

          <label className="field">
            <span className="field-label">Slippage</span>
            <input type="number" value={slippageFraction} step={0.0001}
              onChange={(e) => setSlippageFraction(Number(e.target.value))} />
          </label>
        </div>

        {descriptor && (
          <>
            <p className="muted">{descriptor.description}</p>
            <ParameterForm descriptor={descriptor} values={parameters} onChange={setParameters} />
          </>
        )}

        <div className="actions">
          <button className="primary" onClick={run} disabled={running || !symbol}>
            {running ? 'Running…' : 'Run backtest'}
          </button>
          {result && result.status === 'Completed' && (
            <button onClick={() => setSaveOpen(true)}>Save…</button>
          )}
          {result && <span className="status-pill">{result.status}</span>}
        </div>

        {error && <p className="error">{error}</p>}
      </section>

      {result?.status === 'Completed' && (
        <section className="panel">
          <h2>Results</h2>
          <div className="stat-row">
            <Stat label="Net return" value={fmtPercent(result.totalReturnPercent)} />
            <Stat label="Sharpe" value={fmtNumber(result.sharpeRatio)} />
            <Stat label="Max drawdown" value={fmtPercent(result.maxDrawdownPercent)} />
            <Stat label="Orders" value={result.totalTrades?.toString() ?? '—'} />
            <Stat label="End equity" value={fmtMoney(result.endingEquity)} />
          </div>

          {result.provenance && (
            <p className="muted provenance">
              LEAN {result.provenance.leanVersion} · {result.provenance.symbol} {result.provenance.resolution} ·{' '}
              {result.provenance.startDate} → {result.provenance.endDate} · fee {result.provenance.feeFraction} ·
              slippage {result.provenance.slippageFraction}
              {result.provenance.dataSource && <><br />{result.provenance.dataSource}</>}
            </p>
          )}

          <details>
            <summary>All LEAN statistics ({Object.keys(result.statistics).length})</summary>
            <div className="stats-grid">
              {Object.entries(result.statistics).map(([k, v]) => (
                <div key={k}><span className="muted">{k}</span><span>{v}</span></div>
              ))}
            </div>
          </details>
        </section>
      )}

      {chart && (
        <section className="panel">
          <h2>{chart.symbol} — {chart.markers.length} fills</h2>
          <PriceChart data={chart} />
        </section>
      )}

      {saveOpen && result && (
        <SaveModal
          result={result}
          symbol={symbol}
          startDate={startDate}
          endDate={endDate}
          parameters={parameters}
          onClose={() => setSaveOpen(false)}
        />
      )}
    </div>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="stat">
      <span className="stat-label">{label}</span>
      <span className="stat-value">{value}</span>
    </div>
  );
}

const fmtPercent = (v?: number) => (v === undefined || v === null ? '—' : `${v.toFixed(2)}%`);
const fmtNumber = (v?: number) => (v === undefined || v === null ? '—' : v.toFixed(3));
const fmtMoney = (v?: number) =>
  v === undefined || v === null ? '—' : v.toLocaleString(undefined, { maximumFractionDigits: 0 });
