using QuantConnect.Interfaces;
using QuantConnect.Notifications;
using QuantConnect.Packets;

namespace Parallels.Worker.Live.Binance;

/// <summary>
/// Hands LEAN the single live session this process was launched for.
///
/// Same reason as the backtest worker's equivalent: LEAN's own job queue lives
/// in the Launcher project and is not published to NuGet, only the interface is,
/// and the spec requires a NuGet-only LEAN dependency. The session already
/// arrived in an environment variable, so there is exactly one job to hand over.
/// </summary>
public sealed class SingleLiveJobQueue(LiveNodePacket packet, string algorithmPath) : IJobQueueHandler
{
    private int _dequeued;

    public void Initialize(IApi api, IMessagingHandler messagingHandler) { }

    public AlgorithmNodePacket NextJob(out string algorithmPathOut)
    {
        if (Interlocked.Exchange(ref _dequeued, 1) == 1)
        {
            throw new InvalidOperationException(
                "NextJob was called twice. This process hosts exactly one live session; a desired-state " +
                "change restarts the container rather than starting a second session here.");
        }

        algorithmPathOut = algorithmPath;
        return packet;
    }

    public void AcknowledgeJob(AlgorithmNodePacket job) { }
}

/// <summary>Swallows LEAN's outbound packet stream; there is no cloud backend to publish to.</summary>
public sealed class NullMessagingHandler : IMessagingHandler
{
    public bool HasSubscribers { get; set; }

    public void Initialize(MessagingHandlerInitializeParameters initializeParameters) { }
    public void SetAuthentication(AlgorithmNodePacket job) { }
    public void Send(Packet packet) { }
    public void SendNotification(Notification notification) { }
    public void Dispose() { }
}
