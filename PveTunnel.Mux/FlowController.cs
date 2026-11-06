namespace PveTunnel.Mux;

/// <summary>
/// Credit-based send-side flow control for a single stream. There is exactly one writer per stream
/// (the directional copy pump), so a single-waiter design is sufficient. The sender may only emit
/// DATA up to the available window; the peer replenishes credit with WINDOW frames as it consumes.
/// This bounds in-flight bytes so the small virtio-serial buffer cannot deadlock the shared transport.
/// </summary>
internal sealed class FlowController
{
    private readonly object _gate = new();
    private long _window;
    private TaskCompletionSource<bool>? _waiter;
    private bool _faulted;

    public FlowController(long initialWindow) => _window = initialWindow;

    /// <summary>
    /// Wait until at least one credit byte is available, then reserve up to <paramref name="desired"/>
    /// bytes and return the amount reserved.
    /// </summary>
    public async ValueTask<int> AcquireAsync(int desired, CancellationToken ct)
    {
        while (true)
        {
            TaskCompletionSource<bool> tcs;
            lock (_gate)
            {
                if (_faulted) throw new IOException("stream reset while writing");
                if (_window > 0)
                {
                    var take = (int)Math.Min(desired, _window);
                    _window -= take;
                    return take;
                }
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiter = tcs;
            }

            // Capture the TCS in a local so a concurrent Release()/Fault() nulling the field
            // cannot turn the cancellation-callback state into null.
            using (ct.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetCanceled(), tcs))
            {
                await tcs.Task.ConfigureAwait(false);
            }
        }
    }

    /// <summary>Add credit (received a WINDOW frame) and wake the writer.</summary>
    public void Release(long bytes)
    {
        lock (_gate)
        {
            _window += bytes;
            var w = _waiter;
            _waiter = null;
            w?.TrySetResult(true);
        }
    }

    /// <summary>Permanently fault the controller (stream reset); unblocks any waiter with an error.</summary>
    public void Fault()
    {
        lock (_gate)
        {
            _faulted = true;
            var w = _waiter;
            _waiter = null;
            w?.TrySetException(new IOException("stream reset"));
        }
    }
}
