import type {
  AlphaConfig,
  BacktestJob,
  BacktestResult,
  ChartData,
  SavedBacktestConfig,
  StrategyDescriptor,
  SystemInfo,
} from './types';

// Requests go to a relative /api path; Vite proxies it to the API in dev (see
// vite.config.ts) and a reverse proxy handles it in a real deployment. Keeping
// the base relative means no build-time host baked into the bundle.
const BASE = '/api';

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${BASE}${path}`, {
    headers: { 'Content-Type': 'application/json' },
    ...init,
  });

  if (!response.ok) {
    // Surface the API's validation detail rather than a bare status code —
    // "SlowPeriod must be greater than FastPeriod" is actionable, "400" is not.
    let detail = `${response.status} ${response.statusText}`;
    try {
      const body = await response.json();
      const errors = body?.errors?.job ?? body?.errors?.alpha ?? body?.errors?.config;
      if (Array.isArray(errors)) detail = errors.join(' ');
      else if (body?.title) detail = body.title;
    } catch {
      /* response had no JSON body; the status line is all there is */
    }
    throw new Error(detail);
  }

  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

export const api = {
  system: () => request<SystemInfo>('/system'),
  strategies: () => request<StrategyDescriptor[]>('/strategies'),
  symbols: () => request<string[]>('/symbols'),

  submitBacktest: (job: Omit<BacktestJob, 'jobId'> & { jobId?: string }) =>
    request<BacktestResult>('/backtests', { method: 'POST', body: JSON.stringify(job) }),
  getBacktest: (id: string) => request<BacktestResult>(`/backtests/${id}`),
  listBacktests: () => request<BacktestResult[]>('/backtests'),
  getChart: (id: string) => request<ChartData>(`/backtests/${id}/chart`),

  listSavedConfigs: () => request<SavedBacktestConfig[]>('/saved-configs'),
  saveConfig: (config: Omit<SavedBacktestConfig, 'id' | 'savedUtc'> & { id?: string }) =>
    request<SavedBacktestConfig>('/saved-configs', { method: 'POST', body: JSON.stringify(config) }),
  deleteSavedConfig: (id: string) => request<void>(`/saved-configs/${id}`, { method: 'DELETE' }),

  listAlphas: () => request<AlphaConfig[]>('/alphas'),
  createAlpha: (alpha: Omit<AlphaConfig, 'id'> & { id?: string }) =>
    request<AlphaConfig>('/alphas', { method: 'POST', body: JSON.stringify(alpha) }),
  updateAlpha: (id: string, alpha: AlphaConfig) =>
    request<AlphaConfig>(`/alphas/${id}`, { method: 'PUT', body: JSON.stringify(alpha) }),
  deleteAlpha: (id: string) => request<void>(`/alphas/${id}`, { method: 'DELETE' }),
  setAlphaEnabled: (id: string, enabled: boolean) =>
    request<AlphaConfig>(`/alphas/${id}/enabled`, { method: 'POST', body: JSON.stringify({ enabled }) }),

  liveStatus: () => request<SystemInfo['live']>('/live/status'),
};

/**
 * Polls a run until it reaches a terminal state.
 *
 * A backtest is minutes of work, so the API accepts the job and returns
 * immediately (spec 4.2) rather than holding a request open for its duration.
 */
export async function pollUntilComplete(
  jobId: string,
  onUpdate: (result: BacktestResult) => void,
  signal?: AbortSignal,
): Promise<BacktestResult> {
  for (;;) {
    if (signal?.aborted) throw new Error('Cancelled');

    const result = await api.getBacktest(jobId);
    onUpdate(result);

    if (result.status === 'Completed' || result.status === 'Failed') return result;
    await new Promise((resolve) => setTimeout(resolve, 1500));
  }
}
