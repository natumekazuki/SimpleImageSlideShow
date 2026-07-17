using SimpleImageSlideShow.Components.Pages;
using Xunit;

namespace SimpleImageSlideShow.Tests;

public sealed class TiledPlanCoordinatorTests
{
    [Fact]
    public async Task Invalidate_MakesCapturedGenerationStale()
    {
        await using var coordinator = new TiledPlanCoordinator();
        var generation = coordinator.CurrentGeneration;

        coordinator.Invalidate();

        Assert.False(coordinator.IsCurrent(generation));
    }

    [Fact]
    public async Task CanAppend_RejectsStaleFullAndDuplicatePlans()
    {
        await using var coordinator = new TiledPlanCoordinator();
        var current = coordinator.CurrentGeneration;

        Assert.True(coordinator.CanAppend(current, currentCount: 4, capacity: 5, pathAlreadyPresent: false));
        Assert.False(coordinator.CanAppend(current - 1, currentCount: 4, capacity: 5, pathAlreadyPresent: false));
        Assert.False(coordinator.CanAppend(current, currentCount: 5, capacity: 5, pathAlreadyPresent: false));
        Assert.False(coordinator.CanAppend(current, currentCount: 4, capacity: 5, pathAlreadyPresent: true));
    }

    [Fact]
    public async Task RunExclusiveAsync_SerializesConcurrentPlanProducers()
    {
        await using var coordinator = new TiledPlanCoordinator();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var maximumRunning = 0;
        var invocation = 0;

        async Task Produce(long _, CancellationToken cancellationToken)
        {
            var currentInvocation = Interlocked.Increment(ref invocation);
            var current = Interlocked.Increment(ref running);
            maximumRunning = Math.Max(maximumRunning, current);
            if (currentInvocation == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
            Interlocked.Decrement(ref running);
        }

        var first = coordinator.RunExclusiveAsync(Produce);
        await firstStarted.Task;
        var second = coordinator.RunExclusiveAsync(Produce);

        Assert.False(second.IsCompleted);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, maximumRunning);
    }

    [Fact]
    public async Task RunExclusiveAsync_CapturesCurrentGenerationAfterWaiting()
    {
        await using var coordinator = new TiledPlanCoordinator();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedGenerations = new List<long>();

        var first = coordinator.RunExclusiveAsync(async (generation, cancellationToken) =>
        {
            observedGenerations.Add(generation);
            firstStarted.SetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
        });
        await firstStarted.Task;

        coordinator.Invalidate();
        var second = coordinator.RunExclusiveAsync((generation, _) =>
        {
            observedGenerations.Add(generation);
            return Task.CompletedTask;
        });

        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal([0L, 1L], observedGenerations);
        Assert.Equal(1, coordinator.CurrentGeneration);
    }

    [Fact]
    public async Task RunExclusiveAsync_DirectConsumerObservesPlanAppendedWhileWaiting()
    {
        await using var coordinator = new TiledPlanCoordinator();
        var producerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProducer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new List<string>();

        var producer = coordinator.RunExclusiveAsync(async (_, cancellationToken) =>
        {
            producerStarted.SetResult();
            await releaseProducer.Task.WaitAsync(cancellationToken);
            queue.Add("a.jpg");
        });
        await producerStarted.Task;

        var selected = coordinator.RunConsumerAsync(
            hasQueuedPlan: () => queue.Count > 0,
            applyQueuedPlan: _ => Task.FromResult(queue[0]),
            applyDirectStep: _ => Task.FromResult("direct"));

        Assert.False(selected.IsCompleted);
        releaseProducer.SetResult();

        Assert.Equal("a.jpg", await selected);
        await producer;
    }

    [Fact]
    public async Task DisposeAsync_CancelsProducerAndWaitsForCompletion()
    {
        var coordinator = new TiledPlanCoordinator();
        var producerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var producer = coordinator.RunExclusiveAsync(async (_, cancellationToken) =>
        {
            producerStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                producerFinished.SetResult();
            }
        });
        await producerStarted.Task;

        await coordinator.DisposeAsync();

        await producerFinished.Task;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => producer);
    }

    [Fact]
    public async Task RunConsumerAsync_PlaybackCancellationPreventsWaitingMutation()
    {
        await using var coordinator = new TiledPlanCoordinator();
        using var playbackCancellation = new CancellationTokenSource();
        var producerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProducer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutated = false;

        var producer = coordinator.RunExclusiveAsync(async (_, cancellationToken) =>
        {
            producerStarted.SetResult();
            await releaseProducer.Task.WaitAsync(cancellationToken);
        });
        await producerStarted.Task;

        var consumer = coordinator.RunConsumerAsync(
            hasQueuedPlan: () => false,
            applyQueuedPlan: _ => Task.FromResult(0),
            applyDirectStep: _ =>
            {
                mutated = true;
                return Task.FromResult(1);
            },
            playbackCancellation.Token);

        playbackCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer);
        Assert.False(mutated);

        releaseProducer.SetResult();
        await producer;
    }

    [Fact]
    public async Task RunConsumerAsync_PlaybackCancellationCancelsActiveOperation()
    {
        await using var coordinator = new TiledPlanCoordinator();
        using var playbackCancellation = new CancellationTokenSource();
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutated = false;

        var consumer = coordinator.RunConsumerAsync(
            hasQueuedPlan: () => false,
            applyQueuedPlan: _ => Task.FromResult(0),
            applyDirectStep: async cancellationToken =>
            {
                operationStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                mutated = true;
                return 1;
            },
            playbackCancellation.Token);

        await operationStarted.Task;
        playbackCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer);
        Assert.False(mutated);
    }
}
