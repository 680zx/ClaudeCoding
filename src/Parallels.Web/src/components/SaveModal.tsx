import { useState } from 'react';
import { api } from '../api';
import type { BacktestResult, StrategyParameters } from '../types';

/**
 * Persists the exact parameters a run used, plus a link to that run's report
 * (spec 4.2).
 *
 * The parameters saved are the ones the worker reported back in the run's
 * provenance, not the ones currently sitting in the form. Those can differ if
 * the form was edited after the run finished, and saving the edited values would
 * attach a report to parameters that never produced it.
 */
export function SaveModal({
  result,
  symbol,
  startDate,
  endDate,
  parameters,
  onClose,
}: {
  result: BacktestResult;
  symbol: string;
  startDate: string;
  endDate: string;
  parameters: StrategyParameters;
  onClose: () => void;
}) {
  const [name, setName] = useState(`${symbol} ${result.provenance?.strategyType ?? ''}`.trim());
  const [notes, setNotes] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      await api.saveConfig({
        name,
        sourceJobId: result.jobId,
        symbol: result.provenance?.symbol ?? symbol,
        resolution: result.provenance?.resolution ?? 'Hour',
        startDate: result.provenance?.startDate ?? startDate,
        endDate: result.provenance?.endDate ?? endDate,
        parameters: result.provenance?.parameters ?? parameters,
        notes: notes || undefined,
        totalReturnPercent: result.totalReturnPercent,
        sharpeRatio: result.sharpeRatio,
        maxDrawdownPercent: result.maxDrawdownPercent,
      });
      setSaved(true);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <h3>Save this configuration</h3>

        {saved ? (
          <>
            <p>Saved. It is available on the Live tab as a starting point for a new alpha.</p>
            <div className="actions"><button className="primary" onClick={onClose}>Close</button></div>
          </>
        ) : (
          <>
            <label className="field">
              <span className="field-label">Name</span>
              <input value={name} onChange={(e) => setName(e.target.value)} autoFocus />
            </label>

            <label className="field">
              <span className="field-label">Notes</span>
              <textarea value={notes} rows={3} onChange={(e) => setNotes(e.target.value)}
                placeholder="Why is this worth keeping?" />
            </label>

            <p className="muted">
              Saves the parameters this run actually used, linked to report <code>{result.jobId}</code>.
            </p>

            {error && <p className="error">{error}</p>}

            <div className="actions">
              <button className="primary" onClick={save} disabled={saving || !name.trim()}>
                {saving ? 'Saving…' : 'Save'}
              </button>
              <button onClick={onClose}>Cancel</button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
