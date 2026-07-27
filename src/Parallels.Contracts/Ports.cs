namespace Parallels.Contracts;

/// <summary>
/// Persistence for dispatched runs and their reports.
///
/// A port, not an implementation: the API owns the concrete store, and the
/// workers never touch it — they write their output to the results path they
/// were given and the API reads it back (spec 3.5).
/// </summary>
public interface IBacktestStore
{
    Task SaveAsync(BacktestResult result, CancellationToken ct = default);
    Task<BacktestResult?> GetAsync(string jobId, CancellationToken ct = default);
    Task<IReadOnlyList<BacktestResult>> ListAsync(CancellationToken ct = default);
    Task<ChartData?> GetChartDataAsync(string jobId, CancellationToken ct = default);
}

/// <summary>Desired-state store for live alphas — the Live tab's tiles.</summary>
public interface IAlphaConfigStore
{
    Task<IReadOnlyList<AlphaConfig>> ListAsync(CancellationToken ct = default);
    Task<AlphaConfig?> GetAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(AlphaConfig config, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}

/// <summary>Store for named parameter sets saved off the Backtest tab.</summary>
public interface ISavedConfigStore
{
    Task<IReadOnlyList<SavedBacktestConfig>> ListAsync(CancellationToken ct = default);
    Task<SavedBacktestConfig?> GetAsync(string id, CancellationToken ct = default);
    Task SaveAsync(SavedBacktestConfig config, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}
