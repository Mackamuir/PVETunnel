namespace PveTunnel.Mux;

/// <summary>Bytes moved in each direction by a completed pump.</summary>
public readonly record struct PumpCounts(long AToB, long BToA, bool TimedOut = false);

/// <summary>
/// Pumps bytes in both directions between two streams (e.g. a TCP socket and a
/// <see cref="MuxStream"/>) until either side reaches end-of-stream, then tears both down.
/// </summary>
public static class PumpHelper
{
    /// <summary>
    /// Runs both directions to completion and reports how much moved each way.
    /// </summary>
    /// <param name="idleTimeout">
    /// If set, the pump is torn down once no byte has moved in either direction for this long.
    /// A controller that connects and then says nothing otherwise holds a mux stream open
    /// indefinitely, and enough of those exhaust the session's stream budget.
    /// </param>
    /// <remarks>Counts are for logging only; payload bytes are never retained or inspected.</remarks>
    public static async Task<PumpCounts> BidirectionalAsync(
        Stream a, Stream b, CancellationToken ct, TimeSpan? idleTimeout = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var moved = new Movement();

        var up = CopyAsync(a, b, moved, linked.Token);
        var down = CopyAsync(b, a, moved, linked.Token);
        var both = Task.WhenAny(up, down);

        var timedOut = false;
        if (idleTimeout is { } idle && idle > TimeSpan.Zero)
            timedOut = await WaitWithIdleWatchdogAsync(both, moved, idle, linked).ConfigureAwait(false);
        else
            await both.ConfigureAwait(false);

        linked.Cancel(); // unblock the slower direction

        try { await Task.WhenAll(up, down).ConfigureAwait(false); }
        catch { /* expected on teardown */ }

        return new PumpCounts(Completed(up), Completed(down), timedOut);
    }

    /// <summary>Returns true if the watchdog fired rather than the pump finishing on its own.</summary>
    private static async Task<bool> WaitWithIdleWatchdogAsync(
        Task finished, Movement moved, TimeSpan idle, CancellationTokenSource linked)
    {
        // Poll rather than reset a timer per byte: a quarter of the window bounds the overshoot to
        // 25% and costs one wakeup per tick regardless of throughput.
        var tick = TimeSpan.FromMilliseconds(Math.Max(50, idle.TotalMilliseconds / 4));
        var lastSeen = moved.Total;

        while (true)
        {
            var completed = await Task.WhenAny(finished, Task.Delay(tick, linked.Token)).ConfigureAwait(false);
            if (completed == finished) return false;
            if (linked.IsCancellationRequested) return false;

            var now = moved.Total;
            if (now != lastSeen)
            {
                lastSeen = now;
                moved.MarkActive();
                continue;
            }

            if (moved.IdleFor >= idle)
            {
                linked.Cancel();
                return true;
            }
        }
    }

    /// <summary>A cancelled or faulted direction still moved whatever it moved before it stopped.</summary>
    private static long Completed(Task<long> t) => t.IsCompletedSuccessfully ? t.Result : 0;

    private sealed class Movement
    {
        private long _total;
        private long _lastActiveTicks = DateTime.UtcNow.Ticks;

        public long Total => Interlocked.Read(ref _total);
        public TimeSpan IdleFor => TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastActiveTicks));

        public void Add(int n) => Interlocked.Add(ref _total, n);
        public void MarkActive() => Interlocked.Exchange(ref _lastActiveTicks, DateTime.UtcNow.Ticks);
    }

    private static async Task<long> CopyAsync(Stream from, Stream to, Movement moved, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var total = 0L;
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                await to.FlushAsync(ct).ConfigureAwait(false);
                total += n;
                moved.Add(n);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        return total;
    }
}
