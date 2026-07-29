using SimpleImageSlideShow.Components.Pages;
using SimpleImageSlideShow.Models;
using Xunit;

namespace SimpleImageSlideShow.Tests;

public sealed class TiledPlacementPlannerTests
{
    [Fact]
    public void CreateScaleCandidates_UsesConfiguredRetryCountWithoutImplicitMinimum()
    {
        var samples = new Queue<double>([0.75, 0.5, 0.25]);

        var candidates = TiledPlacementPlanner.CreateScaleCandidates(
            minScale: 0.25,
            maxScale: 1.0,
            initialScale: 0.8,
            randomTryCount: 3,
            nextDouble: samples.Dequeue);

        Assert.Equal([0.8, 0.8125, 0.625, 0.4375], candidates);
        Assert.DoesNotContain(0.25, candidates);
        Assert.All(candidates, scale => Assert.InRange(scale, 0.25, 1.0));
    }

    [Fact]
    public void CreateScaleCandidates_LimitsExtremeTryCountBeforeGeneration()
    {
        var calls = 0;

        var candidates = TiledPlacementPlanner.CreateScaleCandidates(
            minScale: 0.1,
            maxScale: 1.0,
            initialScale: 0.9,
            randomTryCount: uint.MaxValue,
            nextDouble: () => ++calls / 501.0);

        Assert.Equal((int)AppSettings.RandomScaleTriesLimit, calls);
        Assert.InRange(candidates.Count, 1, (int)AppSettings.RandomScaleTriesLimit + 1);
    }

    [Fact]
    public void TryFindFifoPlacement_UsesCurrentFreeSpaceWithoutRemoval()
    {
        var occupied = new bool[2, 3];
        occupied[0, 0] = true;
        occupied[1, 0] = true;
        var items = new[]
        {
            new TiledGridItem(0, 0, 1, 1),
            new TiledGridItem(1, 0, 1, 1)
        };

        var found = TiledPlacementPlanner.TryFindFifoPlacement(
            occupied,
            items,
            requiredRows: 2,
            requiredCols: 2,
            blockedCells: null,
            minimumRemoveCount: 0,
            chooseIndex: _ => 0,
            out var placement);

        Assert.True(found);
        Assert.Equal(0, placement.RemoveCount);
        Assert.Equal(0, placement.Row);
        Assert.Equal(1, placement.Col);
    }

    [Fact]
    public void TryRecalculateFifoPlacement_IgnoresStalePlannedRemovalCount()
    {
        var currentOccupied = new bool[2, 2];
        currentOccupied[0, 0] = true;

        var found = TiledPlacementPlanner.TryRecalculateFifoPlacement(
            currentOccupied,
            [new TiledGridItem(0, 0, 1, 1)],
            new TiledPlannedGridPlacement(RemoveCount: 4, RowSpan: 1, ColSpan: 1),
            blockedCells: null,
            minimumRemoveCount: 0,
            chooseIndex: _ => 0,
            out var recalculated);

        Assert.True(found);
        Assert.Equal(0, recalculated.RemoveCount);
        Assert.Equal(0, recalculated.Row);
        Assert.Equal(1, recalculated.Col);
    }

    [Fact]
    public void TryFindFifoPlacement_ReturnsSmallestRequiredPrefixFromCurrentState()
    {
        var occupied = new bool[2, 2]
        {
            { true, true },
            { true, true }
        };
        var items = new[]
        {
            new TiledGridItem(0, 0, 1, 2),
            new TiledGridItem(1, 0, 1, 2)
        };

        var found = TiledPlacementPlanner.TryFindFifoPlacement(
            occupied,
            items,
            requiredRows: 2,
            requiredCols: 2,
            blockedCells: null,
            minimumRemoveCount: 0,
            chooseIndex: _ => 0,
            out var placement);

        Assert.True(found);
        Assert.Equal(2, placement.RemoveCount);
    }

    [Fact]
    public void TryFindFifoPlacement_PreservesExplicitMinimumRemovalPolicy()
    {
        var occupied = new bool[2, 3];
        occupied[0, 0] = true;
        occupied[1, 0] = true;
        var items = new[]
        {
            new TiledGridItem(0, 0, 1, 1),
            new TiledGridItem(1, 0, 1, 1)
        };

        var found = TiledPlacementPlanner.TryFindFifoPlacement(
            occupied,
            items,
            requiredRows: 2,
            requiredCols: 2,
            blockedCells: null,
            minimumRemoveCount: 2,
            chooseIndex: _ => 0,
            out var placement);

        Assert.True(found);
        Assert.Equal(2, placement.RemoveCount);
    }

    [Fact]
    public void TryFindFifoPlacement_DoesNotUseBlockedCells()
    {
        var occupied = new bool[2, 3];
        var blocked = new bool[2, 3];
        blocked[0, 0] = true;
        blocked[1, 0] = true;

        var found = TiledPlacementPlanner.TryFindFifoPlacement(
            occupied,
            [],
            requiredRows: 2,
            requiredCols: 2,
            blockedCells: blocked,
            minimumRemoveCount: 0,
            chooseIndex: _ => 0,
            out var placement);

        Assert.True(found);
        Assert.Equal(0, placement.RemoveCount);
        Assert.Equal(0, placement.Row);
        Assert.Equal(1, placement.Col);
    }

    [Fact]
    public void CreateScaleCandidates_ZeroRetriesReturnsOnlyClampedInitialScale()
    {
        var candidates = TiledPlacementPlanner.CreateScaleCandidates(
            minScale: 0.2,
            maxScale: 0.4,
            initialScale: 0.8,
            randomTryCount: 0,
            nextDouble: () => 0.5);

        Assert.Single(candidates);
        Assert.Equal(0.4, candidates[0], 10);
    }

    [Fact]
    public void CalculateScaleRange_UsesCurrentSettingsAndViewport()
    {
        var range = TiledPlacementPlanner.CalculateScaleRange(
            configuredMinScale: 0.2,
            configuredMaxScale: 0.4,
            viewportWidth: 1000,
            viewportHeight: 500,
            imageWidth: 300,
            imageHeight: 200,
            shrinkGuardThreshold: 0.25);

        Assert.Equal(0.2, range.Min, 10);
        Assert.Equal(0.4, range.Max, 10);
    }

    [Fact]
    public void CalculateScaleRange_CollapsesToOriginalScaleWhenShrinkGuardExceedsConfiguredMaximum()
    {
        var range = TiledPlacementPlanner.CalculateScaleRange(
            configuredMinScale: 0.1,
            configuredMaxScale: 0.2,
            viewportWidth: 1000,
            viewportHeight: 500,
            imageWidth: 220,
            imageHeight: 100,
            shrinkGuardThreshold: 0.25);

        var candidates = TiledPlacementPlanner.CreateScaleCandidates(
            range.Min,
            range.Max,
            initialScale: 0.2,
            randomTryCount: 3,
            nextDouble: () => 0);

        Assert.Equal(0.22, range.Min, 10);
        Assert.Equal(0.22, range.Max, 10);
        Assert.Single(candidates);
        Assert.Equal(0.22, candidates[0], 10);
    }

    [Fact]
    public void CreateScaleCandidates_CollapsesInvertedRangeAtMinimum()
    {
        var candidates = TiledPlacementPlanner.CreateScaleCandidates(
            minScale: 0.22,
            maxScale: 0.2,
            initialScale: 0.2,
            randomTryCount: 3,
            nextDouble: () => 0);

        Assert.Single(candidates);
        Assert.Equal(0.22, candidates[0], 10);
    }

    [Fact]
    public void TryRecalculateFifoPlacement_PreservesValidPlannedPositionAtMinimumRemovalCount()
    {
        var occupied = new bool[1, 4]
        {
            { true, true, false, false }
        };

        var found = TiledPlacementPlanner.TryRecalculateFifoPlacement(
            occupied,
            [new TiledGridItem(0, 0, 1, 1)],
            new TiledPlannedGridPlacement(
                RemoveCount: 1,
                RowSpan: 1,
                ColSpan: 1,
                Row: 0,
                Col: 3),
            blockedCells: null,
            minimumRemoveCount: 1,
            chooseIndex: _ => 0,
            out var recalculated);

        Assert.True(found);
        Assert.Equal(1, recalculated.RemoveCount);
        Assert.Equal(0, recalculated.Row);
        Assert.Equal(3, recalculated.Col);
    }

    [Fact]
    public void TryRecalculateFifoPlacement_PrefersMinimumRemovalOverStalePlannedPosition()
    {
        var occupied = new bool[1, 3]
        {
            { true, true, true }
        };

        var found = TiledPlacementPlanner.TryRecalculateFifoPlacement(
            occupied,
            [
                new TiledGridItem(0, 0, 1, 1),
                new TiledGridItem(0, 2, 1, 1)
            ],
            new TiledPlannedGridPlacement(
                RemoveCount: 2,
                RowSpan: 1,
                ColSpan: 1,
                Row: 0,
                Col: 2),
            blockedCells: null,
            minimumRemoveCount: 0,
            chooseIndex: _ => 0,
            out var recalculated);

        Assert.True(found);
        Assert.Equal(1, recalculated.RemoveCount);
        Assert.Equal(0, recalculated.Row);
        Assert.Equal(0, recalculated.Col);
    }

    [Fact]
    public void RequiresRemovalPreviewRetry_DetectsCountAndElementChanges()
    {
        string[] previewed = ["a", "b"];

        Assert.False(TiledPlacementPlanner.RequiresRemovalPreviewRetry(previewed, ["a", "b", "c"], 2));
        Assert.True(TiledPlacementPlanner.RequiresRemovalPreviewRetry(previewed, ["a", "b", "c"], 1));
        Assert.True(TiledPlacementPlanner.RequiresRemovalPreviewRetry(previewed, ["x", "b", "c"], 2));
    }

    [Fact]
    public void TryFindFifoPlacement_UsesProvidedPositionSelector()
    {
        var found = TiledPlacementPlanner.TryFindFifoPlacement(
            new bool[2, 3],
            [],
            requiredRows: 1,
            requiredCols: 1,
            blockedCells: null,
            minimumRemoveCount: 0,
            chooseIndex: count => count - 1,
            out var placement);

        Assert.True(found);
        Assert.Equal(0, placement.RemoveCount);
        Assert.Equal(1, placement.Row);
        Assert.Equal(2, placement.Col);
    }

    [Fact]
    public void IsDefragPlacementValid_AcceptsExistingValidMovesDeterministically()
    {
        var occupied = new bool[2, 2]
        {
            { true, true },
            { false, false }
        };
        var items = new[]
        {
            new TiledDefragItem("a.jpg", 0, 0, 1, 1),
            new TiledDefragItem("b.jpg", 0, 1, 1, 1)
        };
        var moves = new[]
        {
            new TiledDefragMove("a.jpg", 1, 0),
            new TiledDefragMove("b.jpg", 1, 1)
        };

        var valid = TiledPlacementPlanner.IsDefragPlacementValid(
            occupied,
            items,
            newRow: 0,
            newCol: 0,
            newRowSpan: 1,
            newColSpan: 2,
            moves,
            blockedCells: null);

        Assert.True(valid);
        Assert.True(occupied[0, 0]);
        Assert.True(occupied[0, 1]);
        Assert.False(occupied[1, 0]);
        Assert.False(occupied[1, 1]);
    }

    [Fact]
    public void IsDefragPlacementValid_RejectsMissingMovePath()
    {
        var valid = TiledPlacementPlanner.IsDefragPlacementValid(
            new bool[2, 2],
            [new TiledDefragItem("a.jpg", 0, 0, 1, 1)],
            newRow: 0,
            newCol: 0,
            newRowSpan: 1,
            newColSpan: 1,
            [new TiledDefragMove("missing.jpg", 1, 0)],
            blockedCells: null);

        Assert.False(valid);
    }

    [Fact]
    public void IsDefragPlacementValid_RejectsOverlappingMoveDestinations()
    {
        var occupied = new bool[2, 2]
        {
            { true, true },
            { false, false }
        };

        var valid = TiledPlacementPlanner.IsDefragPlacementValid(
            occupied,
            [
                new TiledDefragItem("a.jpg", 0, 0, 1, 1),
                new TiledDefragItem("b.jpg", 0, 1, 1, 1)
            ],
            newRow: 0,
            newCol: 0,
            newRowSpan: 1,
            newColSpan: 2,
            [
                new TiledDefragMove("a.jpg", 1, 0),
                new TiledDefragMove("b.jpg", 1, 0)
            ],
            blockedCells: null);

        Assert.False(valid);
    }

    [Fact]
    public void IsDefragPlacementValid_RejectsClockCollision()
    {
        var occupied = new bool[2, 2];
        occupied[0, 0] = true;
        var blocked = new bool[2, 2];
        blocked[1, 0] = true;

        var valid = TiledPlacementPlanner.IsDefragPlacementValid(
            occupied,
            [new TiledDefragItem("a.jpg", 0, 0, 1, 1)],
            newRow: 0,
            newCol: 0,
            newRowSpan: 1,
            newColSpan: 2,
            [new TiledDefragMove("a.jpg", 1, 0)],
            blocked);

        Assert.False(valid);
    }

    [Fact]
    public void IsDefragPlacementValid_RejectsCurrentNewImageGeometryOutsideGrid()
    {
        var occupied = new bool[2, 2];
        occupied[0, 1] = true;

        var valid = TiledPlacementPlanner.IsDefragPlacementValid(
            occupied,
            [new TiledDefragItem("a.jpg", 0, 1, 1, 1)],
            newRow: 0,
            newCol: 1,
            newRowSpan: 1,
            newColSpan: 2,
            [new TiledDefragMove("a.jpg", 1, 1)],
            blockedCells: null);

        Assert.False(valid);
    }

    [Fact]
    public void IsDefragPlacementValid_RejectsMoveIntoUnmovedItem()
    {
        var occupied = new bool[2, 2]
        {
            { true, true },
            { false, false }
        };

        var valid = TiledPlacementPlanner.IsDefragPlacementValid(
            occupied,
            [
                new TiledDefragItem("moving.jpg", 0, 0, 1, 1),
                new TiledDefragItem("stationary.jpg", 0, 1, 1, 1)
            ],
            newRow: 0,
            newCol: 0,
            newRowSpan: 1,
            newColSpan: 1,
            [new TiledDefragMove("moving.jpg", 0, 1)],
            blockedCells: null);

        Assert.False(valid);
    }
}
