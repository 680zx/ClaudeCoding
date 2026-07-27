using Parallels.Contracts;
using QuantConnect;
using QuantConnect.Interfaces;
using QuantConnect.Notifications;
using QuantConnect.Packets;

namespace Parallels.Worker.Backtest;

/// <summary>
/// Hands LEAN the one job this process was started for.
///
/// LEAN's own <c>QuantConnect.Queues.JobQueue</c> lives in the Launcher project,
/// which is not published to NuGet — only the <see cref="IJobQueueHandler"/>
/// interface ships in QuantConnect.Common. Since the spec requires a NuGet-only
/// LEAN dependency (never cloned), the host has to supply this itself.
///
/// That turns out to be the right shape regardless: the job already arrived as a
/// <c>--job</c> argument from the API, so "the queue" is a queue of exactly one,
/// and a second <c>NextJob</c> call would mean a second engine session in a
/// process that cannot support one.
/// </summary>
public sealed class SingleJobQueue(BacktestNodePacket packet, string algorithmPath) : IJobQueueHandler
{
    private int _dequeued;

    public void Initialize(IApi api, IMessagingHandler messagingHandler) { }

    public AlgorithmNodePacket NextJob(out string algorithmPathOut)
    {
        if (Interlocked.Exchange(ref _dequeued, 1) == 1)
        {
            throw new InvalidOperationException(
                "NextJob was called twice. This process runs exactly one backtest; " +
                "dispatch another job to a new process.");
        }

        algorithmPathOut = algorithmPath;
        return packet;
    }

    public void AcknowledgeJob(AlgorithmNodePacket job) { }
}

/// <summary>
/// Swallows LEAN's outbound packet stream.
///
/// The cloud messaging handler pushes results to QuantConnect's own backend;
/// there is nothing to push to here. Results are read from the result handler
/// and written to the run's output directory instead.
/// </summary>
public sealed class NullMessagingHandler : IMessagingHandler
{
    public bool HasSubscribers { get; set; }

    public void Initialize(MessagingHandlerInitializeParameters initializeParameters) { }
    public void SetAuthentication(AlgorithmNodePacket job) { }
    public void Send(Packet packet) { }
    public void SendNotification(Notification notification) { }
    public void Dispose() { }
}

/// <summary>Builds the LEAN packet describing one backtest.</summary>
public static class PacketFactory
{
    public static BacktestNodePacket ForBacktest(BacktestJob job)
    {
        var start = job.StartDate.ToDateTime(TimeOnly.MinValue);
        var end = job.EndDate.ToDateTime(TimeOnly.MinValue);

        // startingCapital is deliberately null. Passing an amount here makes LEAN
        // seed the cash book in its default account currency (USD) before
        // Initialize runs, which then refuses the algorithm's own
        // SetAccountCurrency("USDT") with "already been set to USD". The result
        // is an account holding USD that cannot buy a USDT-quoted pair: every
        // order is rejected for insufficient buying power and the run reports a
        // flat equity curve while still producing thousands of order records.
        // Letting Initialize set both currency and cash avoids that entirely.
        return new BacktestNodePacket(
            userId: 0,
            projectId: 0,
            sessionId: job.JobId,
            algorithmData: [],
            name: job.Name ?? job.JobId,
            startingCapital: null)
        {
            Language = Language.CSharp,
            BacktestId = job.JobId,
            PeriodStart = start,
            PeriodFinish = end,
            Controls = new Controls
            {
                // Generous enough that a two-year hourly run is never silently
                // truncated; a truncated run would still report statistics, which
                // is the failure mode worth ruling out.
                MaximumDataPointsPerChartSeries = 1_000_000,
                MaximumChartSeries = 100,
            },
        };
    }
}
