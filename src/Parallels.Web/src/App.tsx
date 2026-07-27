import { useEffect, useState } from 'react';
import { api } from './api';
import { BacktestTab } from './components/BacktestTab';
import { LiveTab } from './components/LiveTab';
import type { StrategyDescriptor, SystemInfo } from './types';

type Tab = 'live' | 'backtest';

export default function App() {
  const [tab, setTab] = useState<Tab>('backtest');
  const [strategies, setStrategies] = useState<StrategyDescriptor[]>([]);
  const [symbols, setSymbols] = useState<string[]>([]);
  const [system, setSystem] = useState<SystemInfo | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    (async () => {
      try {
        const [s, sym, sys] = await Promise.all([api.strategies(), api.symbols(), api.system()]);
        setStrategies(s);
        setSymbols(sym);
        setSystem(sys);
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e));
      }
    })();
  }, []);

  return (
    <div className="app">
      <header className="app-header">
        <h1>Parallels</h1>
        <nav className="tabs">
          <button className={tab === 'live' ? 'active' : ''} onClick={() => setTab('live')}>Live</button>
          <button className={tab === 'backtest' ? 'active' : ''} onClick={() => setTab('backtest')}>Backtest</button>
        </nav>
        {system && (
          // Stated rather than assumed (spec 7): the UI always shows which
          // dispatch mechanism is actually carrying the work.
          <span className="muted small">dispatch: {system.dispatchMechanism}</span>
        )}
      </header>

      {error && <p className="error banner">{error}</p>}

      {tab === 'backtest'
        ? <BacktestTab strategies={strategies} symbols={symbols} />
        : <LiveTab strategies={strategies} symbols={symbols} />}
    </div>
  );
}
