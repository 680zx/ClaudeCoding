// TypeScript mirrors of Parallels.Contracts. The API serializes those types with
// camelCase naming, so these shapes line up field for field — including the
// "strategyType" discriminator that makes a parameter set self-describing.

export type StrategyParameters = {
  strategyType: string;
  [key: string]: string | number | boolean;
};

export type ParameterField = {
  name: string;
  label: string;
  kind: 'integer' | 'decimal' | 'fraction';
  default: number;
  min?: number;
  max?: number;
  step?: number;
  help?: string;
};

export type StrategyDescriptor = {
  strategyType: string;
  displayName: string;
  description: string;
  fields: ParameterField[];
};

export type BacktestStatus = 'Queued' | 'Running' | 'Completed' | 'Failed';

export type BacktestJob = {
  jobId: string;
  symbol: string;
  market?: string;
  resolution: string;
  startDate: string;
  endDate: string;
  startingCash: number;
  accountCurrency?: string;
  parameters: StrategyParameters;
  feeFraction: number;
  slippageFraction: number;
  name?: string;
};

export type RunProvenance = {
  symbol: string;
  market: string;
  resolution: string;
  startDate: string;
  endDate: string;
  strategyType: string;
  parameters: StrategyParameters;
  startingCash: number;
  feeFraction: number;
  slippageFraction: number;
  leanVersion?: string;
  workerVersion?: string;
  runStartedUtc: string;
  runCompletedUtc: string;
  dataSource?: string;
};

export type BacktestResult = {
  jobId: string;
  status: BacktestStatus;
  name?: string;
  error?: string;
  provenance?: RunProvenance;
  statistics: Record<string, string>;
  totalReturnPercent?: number;
  sharpeRatio?: number;
  maxDrawdownPercent?: number;
  winRatePercent?: number;
  totalTrades?: number;
  endingEquity?: number;
  hasChartData: boolean;
  completedUtc?: string;
};

export type Candle = { time: number; open: number; high: number; low: number; close: number };
export type IndicatorPoint = { time: number; value: number };

export type TradeMarker = {
  time: number;
  side: 'buy' | 'sell';
  price: number;
  quantity: number;
  indicatorValues: Record<string, number>;
  tag?: string;
};

export type ChartData = {
  runId: string;
  symbol: string;
  candles: Candle[];
  indicators: Record<string, IndicatorPoint[]>;
  markers: TradeMarker[];
  equity: IndicatorPoint[];
};

export type AlphaConfig = {
  id: string;
  name: string;
  symbols: string[];
  market?: string;
  resolution: string;
  parameters: StrategyParameters;
  enabled: boolean;
  maxExposureFraction: number;
  createdUtc?: string;
  updatedUtc?: string;
  sourceSavedConfigId?: string;
};

export type SavedBacktestConfig = {
  id: string;
  name: string;
  sourceJobId: string;
  symbol: string;
  market?: string;
  resolution: string;
  startDate: string;
  endDate: string;
  parameters: StrategyParameters;
  notes?: string;
  totalReturnPercent?: number;
  sharpeRatio?: number;
  maxDrawdownPercent?: number;
  savedUtc: string;
};

export type LiveSessionState = {
  running: boolean;
  identity?: string;
  mechanism: string;
  enabledAlphaCount: number;
  environment: string;
  lastAppliedUtc?: string;
  detail?: string;
};

export type SystemInfo = {
  dispatchMechanism: string;
  dataFolder: string;
  resultsRoot: string;
  live: LiveSessionState;
};
