import { useEffect, useRef, useState } from 'react';
import {
  CandlestickSeries,
  LineSeries,
  createChart,
  createSeriesMarkers,
  type IChartApi,
  type ISeriesApi,
  type Time,
} from 'lightweight-charts';
import type { ChartData, TradeMarker } from '../types';

const INDICATOR_COLOURS: Record<string, string> = {
  emaFast: '#f0b429',
  emaSlow: '#4c9aff',
  atr: '#a78bfa',
};

/**
 * Candles, indicator lines and fill markers for one run.
 *
 * Uses the library's documented React pattern: a ref for the container, chart
 * creation in an effect, and full teardown on cleanup. The chart instance is
 * deliberately kept out of React state — it is an imperative object that must
 * not trigger re-renders.
 */
export function PriceChart({ data }: { data: ChartData }) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<IChartApi | null>(null);
  const [hovered, setHovered] = useState<TradeMarker | null>(null);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const chart = createChart(container, {
      width: container.clientWidth,
      height: 460,
      layout: {
        background: { color: 'transparent' },
        textColor: '#c9d1d9',
        // Required by the library's licence: either this logo or a visible link
        // back to tradingview.com must be present.
        attributionLogo: true,
      },
      grid: {
        vertLines: { color: 'rgba(255,255,255,0.05)' },
        horzLines: { color: 'rgba(255,255,255,0.05)' },
      },
      rightPriceScale: { borderColor: 'rgba(255,255,255,0.15)' },
      timeScale: {
        borderColor: 'rgba(255,255,255,0.15)',
        timeVisible: true,
        // A two-year hourly run is ~17,000 bars. At the default minimum of 0.5px
        // per bar those cannot fit in any real window, so fitContent silently
        // clamps and shows only the most recent months — which looks like a
        // correct chart of the wrong date range.
        minBarSpacing: 0.005,
      },
      crosshair: { mode: 0 },
    });
    chartRef.current = chart;

    const candles = chart.addSeries(CandlestickSeries, {
      upColor: '#26a69a',
      downColor: '#ef5350',
      wickUpColor: '#26a69a',
      wickDownColor: '#ef5350',
      borderVisible: false,
    });
    candles.setData(
      data.candles.map((c) => ({
        time: c.time as Time,
        open: c.open,
        high: c.high,
        low: c.low,
        close: c.close,
      })),
    );

    // ATR is a volatility measure in price units, not a price level, so plotting
    // it on the price scale would squash the candles. It is left out here and
    // still available in the marker tooltip.
    for (const [name, points] of Object.entries(data.indicators)) {
      if (name === 'atr') continue;

      const line: ISeriesApi<'Line'> = chart.addSeries(LineSeries, {
        color: INDICATOR_COLOURS[name] ?? '#8b949e',
        lineWidth: 2,
        priceLineVisible: false,
        lastValueVisible: false,
        title: name,
      });
      line.setData(points.map((p) => ({ time: p.time as Time, value: p.value })));
    }

    if (data.markers.length > 0) {
      // Arrows only, no per-marker text. With hundreds of fills the labels
      // overlap into an unreadable smear; size and price are one hover away in
      // the tooltip, which is where that detail actually belongs.
      createSeriesMarkers(
        candles,
        data.markers.map((m) => ({
          time: m.time as Time,
          position: m.side === 'buy' ? ('belowBar' as const) : ('aboveBar' as const),
          color: m.side === 'buy' ? '#26a69a' : '#ef5350',
          shape: m.side === 'buy' ? ('arrowUp' as const) : ('arrowDown' as const),
        })),
      );
    }

    // Hovering a bar shows the fill recorded at that timestamp, including the
    // indicator snapshot captured when the order actually filled.
    const byTime = new Map<number, TradeMarker>(data.markers.map((m) => [m.time, m]));
    chart.subscribeCrosshairMove((param) => {
      const time = param.time as number | undefined;
      setHovered(time === undefined ? null : (byTime.get(time) ?? null));
    });

    // Fit after the markers plugin has attached. Calling it inline leaves the
    // view on the default trailing window — the plugin adjusts the time scale
    // after this effect body runs, undoing an earlier fit.
    const fitHandle = requestAnimationFrame(() => chart.timeScale().fitContent());

    const resize = () => chart.applyOptions({ width: container.clientWidth });
    window.addEventListener('resize', resize);

    return () => {
      cancelAnimationFrame(fitHandle);
      window.removeEventListener('resize', resize);
      chart.remove();
      chartRef.current = null;
    };
  }, [data]);

  return (
    <div className="chart-wrap">
      <div ref={containerRef} className="chart" />
      {hovered && (
        <div className="marker-tooltip">
          <strong className={hovered.side}>{hovered.side.toUpperCase()}</strong>
          <span>{hovered.quantity} @ {hovered.price.toFixed(2)}</span>
          <span className="muted">{new Date(hovered.time * 1000).toISOString().replace('T', ' ').slice(0, 16)}</span>
          {Object.entries(hovered.indicatorValues).map(([k, v]) => (
            <span key={k} className="muted">{k}: {v.toFixed(2)}</span>
          ))}
        </div>
      )}
      <p className="attribution">
        Charts by{' '}
        <a href="https://www.tradingview.com" target="_blank" rel="noreferrer">TradingView</a>
      </p>
    </div>
  );
}
