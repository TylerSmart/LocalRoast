using System.Threading.Channels;
using App.Core.Ingestion;

namespace App.Infrastructure.Ingestion;

public sealed class IngestionQueueSignal : IIngestionQueueSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        }
    );

    public void NotifyWorkAvailable()
    {
        _channel.Writer.TryWrite(true);
    }

    public async ValueTask<bool> WaitForWorkAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            if (await _channel.Reader.WaitToReadAsync(cts.Token))
            {
                while (_channel.Reader.TryRead(out _))
                {
                    // drain pending signals
                }
                return true;
            }

            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // timeout reached
            return false;
        }
    }
}
