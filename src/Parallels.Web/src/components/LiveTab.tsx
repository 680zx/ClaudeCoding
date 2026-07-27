import { useCallback, useEffect, useState } from 'react';
import { api } from '../api';
import type {
  AlphaConfig,
  LiveSessionState,
  SavedBacktestConfig,
  StrategyDescriptor,
  StrategyParameters,
} from '../types';
import { ParameterForm, defaultsFor } from './ParameterForm';

export function LiveTab({ strategies, symbols }: { strategies: StrategyDescriptor[]; symbols: string[] }) {
  const [alphas, setAlphas] = useState<AlphaConfig[]>([]);
  const [saved, setSaved] = useState<SavedBacktestConfig[]>([]);
  const [status, setStatus] = useState<LiveSessionState | null>(null);
  const [editing, setEditing] = useState<AlphaConfig | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const refresh = useCallback(async () => {
    try {
      const [alphaList, savedList, live] = await Promise.all([
        api.listAlphas(),
        api.listSavedConfigs(),
        api.liveStatus(),
      ]);
      setAlphas(alphaList);
      setSaved(savedList);
      setStatus(live);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => {
    void refresh();
    const timer = setInterval(refresh, 5000);
    return () => clearInterval(timer);
  }, [refresh]);

  const mutate = async (action: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await action();
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const blankAlpha = (): AlphaConfig => {
    const descriptor = strategies[0];
    return {
      id: '',
      name: 'New alpha',
      symbols: [symbols[0] ?? 'BTCUSDT'],
      resolution: 'Hour',
      parameters: descriptor ? defaultsFor(descriptor) : { strategyType: '' },
      enabled: false,
      maxExposureFraction: 0.25,
    };
  };

  return (
    <div className="tab">
      <section className="panel">
        <div className="live-header">
          <h2>Live alphas</h2>
          <div className="actions">
            <button className="primary" onClick={() => setEditing(blankAlpha())} disabled={busy}>
              Add alpha
            </button>
          </div>
        </div>

        {status && (
          <p className="muted">
            Worker{' '}
            <strong className={status.running ? 'ok' : 'off'}>
              {status.running ? 'running' : 'stopped'}
            </strong>{' '}
            · {status.enabledAlphaCount} enabled · {status.environment} · {status.mechanism}
            {status.identity && <> · {status.identity}</>}
            {status.detail && <> · {status.detail}</>}
          </p>
        )}

        <p className="muted small">
          Disabling is a soft action: the alpha stops opening new positions, and any existing
          position and its resting protective orders are left alone on the exchange.
        </p>

        {error && <p className="error">{error}</p>}

        <div className="tiles">
          {alphas.length === 0 && <p className="muted">No alphas configured yet.</p>}

          {alphas.map((alpha) => (
            <article key={alpha.id} className={`tile ${alpha.enabled ? 'on' : 'off'}`}>
              <header>
                <h3>{alpha.name}</h3>
                <span className={`badge ${alpha.enabled ? 'on' : 'off'}`}>
                  {alpha.enabled ? 'enabled' : 'disabled'}
                </span>
              </header>

              <dl>
                <div><dt>Strategy</dt><dd>{alpha.parameters.strategyType}</dd></div>
                <div><dt>Symbols</dt><dd>{alpha.symbols.join(', ')}</dd></div>
                <div><dt>Resolution</dt><dd>{alpha.resolution}</dd></div>
                <div><dt>Max exposure</dt><dd>{(alpha.maxExposureFraction * 100).toFixed(0)}%</dd></div>
              </dl>

              <div className="key-params">
                {Object.entries(alpha.parameters)
                  .filter(([k]) => k !== 'strategyType')
                  .slice(0, 4)
                  .map(([k, v]) => <span key={k} className="chip">{k}: {String(v)}</span>)}
              </div>

              <footer className="actions">
                <button onClick={() => mutate(() => api.setAlphaEnabled(alpha.id, !alpha.enabled))} disabled={busy}>
                  {alpha.enabled ? 'Disable' : 'Enable'}
                </button>
                <button onClick={() => setEditing(alpha)} disabled={busy}>Edit</button>
                <button className="danger" disabled={busy}
                  onClick={() => {
                    if (confirm(`Delete "${alpha.name}"? This removes it from desired state.`)) {
                      void mutate(() => api.deleteAlpha(alpha.id));
                    }
                  }}>
                  Delete
                </button>
              </footer>
            </article>
          ))}
        </div>
      </section>

      {saved.length > 0 && (
        <section className="panel">
          <h2>Saved backtest configurations</h2>
          <p className="muted small">
            Start a live alpha from a parameter set a backtest already justified.
          </p>
          <div className="tiles">
            {saved.map((config) => (
              <article key={config.id} className="tile">
                <header><h3>{config.name}</h3></header>
                <dl>
                  <div><dt>Symbol</dt><dd>{config.symbol}</dd></div>
                  <div><dt>Return</dt><dd>{config.totalReturnPercent?.toFixed(2) ?? '—'}%</dd></div>
                  <div><dt>Sharpe</dt><dd>{config.sharpeRatio?.toFixed(3) ?? '—'}</dd></div>
                  <div><dt>Range</dt><dd>{config.startDate} → {config.endDate}</dd></div>
                </dl>
                {config.notes && <p className="muted small">{config.notes}</p>}
                <footer className="actions">
                  <button
                    disabled={busy}
                    onClick={() =>
                      setEditing({
                        id: '',
                        name: config.name,
                        symbols: [config.symbol],
                        resolution: config.resolution,
                        parameters: config.parameters,
                        enabled: false,
                        maxExposureFraction: 0.25,
                        sourceSavedConfigId: config.id,
                      })
                    }
                  >
                    Use for a live alpha
                  </button>
                </footer>
              </article>
            ))}
          </div>
        </section>
      )}

      {editing && (
        <AlphaEditor
          alpha={editing}
          strategies={strategies}
          symbols={symbols}
          onCancel={() => setEditing(null)}
          onSave={async (next) => {
            await mutate(() => (next.id ? api.updateAlpha(next.id, next) : api.createAlpha(next)));
            setEditing(null);
          }}
        />
      )}
    </div>
  );
}

function AlphaEditor({
  alpha,
  strategies,
  symbols,
  onSave,
  onCancel,
}: {
  alpha: AlphaConfig;
  strategies: StrategyDescriptor[];
  symbols: string[];
  onSave: (alpha: AlphaConfig) => Promise<void>;
  onCancel: () => void;
}) {
  const [draft, setDraft] = useState<AlphaConfig>(alpha);
  const descriptor =
    strategies.find((s) => s.strategyType === draft.parameters.strategyType) ?? strategies[0];

  const setParameters = (parameters: StrategyParameters) => setDraft({ ...draft, parameters });

  return (
    <div className="modal-backdrop" onClick={onCancel}>
      <div className="modal wide" onClick={(e) => e.stopPropagation()}>
        <h3>{draft.id ? 'Edit alpha' : 'Add alpha'}</h3>

        <div className="control-row">
          <label className="field">
            <span className="field-label">Name</span>
            <input type="text" value={draft.name}
              onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
          </label>

          <label className="field">
            <span className="field-label">Symbol</span>
            <select value={draft.symbols[0] ?? ''}
              onChange={(e) => setDraft({ ...draft, symbols: [e.target.value] })}>
              {symbols.map((s) => <option key={s} value={s}>{s}</option>)}
            </select>
          </label>

          <label className="field">
            <span className="field-label">Strategy</span>
            <select
              value={String(draft.parameters.strategyType)}
              onChange={(e) => {
                const next = strategies.find((s) => s.strategyType === e.target.value);
                if (next) setParameters(defaultsFor(next));
              }}
            >
              {strategies.map((s) => (
                <option key={s.strategyType} value={s.strategyType}>{s.displayName}</option>
              ))}
            </select>
          </label>

          <label className="field">
            <span className="field-label">Max exposure</span>
            <input type="number" step={0.01} min={0.01} max={1} value={draft.maxExposureFraction}
              onChange={(e) => setDraft({ ...draft, maxExposureFraction: Number(e.target.value) })} />
          </label>

          <label className="field checkbox">
            <input type="checkbox" checked={draft.enabled}
              onChange={(e) => setDraft({ ...draft, enabled: e.target.checked })} />
            <span>Enabled</span>
          </label>
        </div>

        {descriptor && (
          <ParameterForm descriptor={descriptor} values={draft.parameters} onChange={setParameters} />
        )}

        <p className="muted small">
          Saving restarts the live worker so it picks up the change — a full reload, not a
          hot-swap, because LEAN does not reliably support adding an alpha to a running session.
        </p>

        <div className="actions">
          <button className="primary" onClick={() => void onSave(draft)}>Save</button>
          <button onClick={onCancel}>Cancel</button>
        </div>
      </div>
    </div>
  );
}
