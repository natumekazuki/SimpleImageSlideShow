namespace SimpleImageSlideShow.Components.Pages;

internal sealed class TiledPlanCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly object disposalLock = new();
    private Task? disposalTask;
    private long generation;
    private int disposalStarted;

    internal long CurrentGeneration => Interlocked.Read(ref generation);

    internal void Invalidate()
    {
        ThrowIfDisposing();
        Interlocked.Increment(ref generation);
    }

    internal bool IsCurrent(long expectedGeneration)
        => Volatile.Read(ref disposalStarted) == 0 && expectedGeneration == CurrentGeneration;

    internal bool CanAppend(long expectedGeneration, int currentCount, int capacity, bool pathAlreadyPresent)
        => IsCurrent(expectedGeneration) && currentCount < capacity && !pathAlreadyPresent;

    internal Task RunExclusiveAsync(Func<long, CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunExclusiveCoreAsync(operation);
    }

    internal Task<T> RunExclusiveAsync<T>(Func<long, CancellationToken, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunExclusiveCoreAsync(operation);
    }

    internal Task<T> RunConsumerAsync<T>(
        Func<bool> hasQueuedPlan,
        Func<CancellationToken, Task<T>> applyQueuedPlan,
        Func<CancellationToken, Task<T>> applyDirectStep,
        CancellationToken operationCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hasQueuedPlan);
        ArgumentNullException.ThrowIfNull(applyQueuedPlan);
        ArgumentNullException.ThrowIfNull(applyDirectStep);
        return RunExclusiveCoreAsync(
            (_, cancellationToken) =>
                hasQueuedPlan()
                    ? applyQueuedPlan(cancellationToken)
                    : applyDirectStep(cancellationToken),
            operationCancellationToken);
    }

    private async Task RunExclusiveCoreAsync(Func<long, CancellationToken, Task> operation)
    {
        ThrowIfDisposing();
        var cancellationToken = lifetimeCancellation.Token;
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation(CurrentGeneration, cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async Task<T> RunExclusiveCoreAsync<T>(
        Func<long, CancellationToken, Task<T>> operation,
        CancellationToken operationCancellationToken = default)
    {
        ThrowIfDisposing();
        using var linkedCancellation = operationCancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeCancellation.Token,
                operationCancellationToken)
            : null;
        var effectiveCancellationToken = linkedCancellation?.Token ?? lifetimeCancellation.Token;
        await operationLock.WaitAsync(effectiveCancellationToken);
        try
        {
            effectiveCancellationToken.ThrowIfCancellationRequested();
            return await operation(CurrentGeneration, effectiveCancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalLock)
        {
            disposalTask ??= DisposeCoreAsync();
            return new ValueTask(disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref disposalStarted, 1) != 0) return;

        lifetimeCancellation.Cancel();
        await operationLock.WaitAsync();
        operationLock.Release();
        operationLock.Dispose();
        lifetimeCancellation.Dispose();
    }

    private void ThrowIfDisposing()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
}
