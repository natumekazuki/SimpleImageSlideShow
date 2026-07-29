using SimpleImageSlideShow.Models;

namespace SimpleImageSlideShow.Components.Pages;

internal readonly record struct TiledGridItem(int Row, int Col, int RowSpan, int ColSpan);

internal readonly record struct TiledDefragItem(string Path, int Row, int Col, int RowSpan, int ColSpan);

internal readonly record struct TiledDefragMove(string Path, int Row, int Col);

internal readonly record struct TiledGridPlacement(int RemoveCount, int Row, int Col);

internal readonly record struct TiledPlannedGridPlacement(
    int RemoveCount,
    int RowSpan,
    int ColSpan,
    int Row = -1,
    int Col = -1);

internal readonly record struct TiledScaleRange(double Min, double Max);

internal static class TiledPlacementPlanner
{
    private const double ScaleTolerance = 1e-9;

    internal static IReadOnlyList<double> CreateScaleCandidates(
        double minScale,
        double maxScale,
        double initialScale,
        uint randomTryCount,
        Func<double> nextDouble)
    {
        ArgumentNullException.ThrowIfNull(nextDouble);

        var lower = minScale;
        var upper = maxScale >= minScale ? maxScale : minScale;
        var boundedTryCount = Math.Min(randomTryCount, AppSettings.RandomScaleTriesLimit);
        var candidates = new List<double>((int)boundedTryCount + 1);

        AddIfDistinct(candidates, Math.Clamp(initialScale, lower, upper));
        for (uint i = 0; i < boundedTryCount; i++)
        {
            var sample = nextDouble();
            if (!double.IsFinite(sample)) sample = 0;
            sample = Math.Clamp(sample, 0, 1);
            var scale = lower < upper ? lower + sample * (upper - lower) : lower;
            AddIfDistinct(candidates, scale);
        }

        return candidates;
    }

    internal static TiledScaleRange CalculateScaleRange(
        double configuredMinScale,
        double configuredMaxScale,
        double viewportWidth,
        double viewportHeight,
        double imageWidth,
        double imageHeight,
        double shrinkGuardThreshold)
    {
        var maximum = Math.Max(configuredMinScale, Math.Min(1.0, configuredMaxScale));
        var minimum = Math.Min(configuredMinScale, maximum);
        var viewportLongEdge = Math.Max(Math.Max(1.0, viewportWidth), Math.Max(1.0, viewportHeight));
        var imageLongEdge = Math.Max(imageWidth, imageHeight);
        var originalScale = viewportLongEdge > 0 ? imageLongEdge / viewportLongEdge : minimum;
        if (originalScale <= shrinkGuardThreshold)
        {
            minimum = Math.Max(minimum, originalScale);
        }

        if (minimum > maximum)
        {
            maximum = minimum;
        }

        return new TiledScaleRange(minimum, maximum);
    }

    internal static bool TryFindFifoPlacement(
        bool[,] occupied,
        IReadOnlyList<TiledGridItem> fifoItems,
        int requiredRows,
        int requiredCols,
        bool[,]? blockedCells,
        int minimumRemoveCount,
        Func<int, int> chooseIndex,
        out TiledGridPlacement placement)
        => TryFindFifoPlacementCore(
            occupied,
            fifoItems,
            requiredRows,
            requiredCols,
            blockedCells,
            minimumRemoveCount,
            preferredRow: null,
            preferredCol: null,
            chooseIndex,
            out placement);

    private static bool TryFindFifoPlacementCore(
        bool[,] occupied,
        IReadOnlyList<TiledGridItem> fifoItems,
        int requiredRows,
        int requiredCols,
        bool[,]? blockedCells,
        int minimumRemoveCount,
        int? preferredRow,
        int? preferredCol,
        Func<int, int> chooseIndex,
        out TiledGridPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(occupied);
        ArgumentNullException.ThrowIfNull(fifoItems);
        ArgumentNullException.ThrowIfNull(chooseIndex);

        placement = default;
        var rows = occupied.GetLength(0);
        var cols = occupied.GetLength(1);
        if (requiredRows <= 0 || requiredCols <= 0 || requiredRows > rows || requiredCols > cols)
        {
            return false;
        }
        if (blockedCells is not null &&
            (blockedCells.GetLength(0) != rows || blockedCells.GetLength(1) != cols))
        {
            throw new ArgumentException("Blocked cells must match the occupancy grid dimensions.", nameof(blockedCells));
        }

        var minimum = Math.Max(0, minimumRemoveCount);
        var simulated = (bool[,])occupied.Clone();
        if (minimum == 0 && TryChooseFreeRegion(
                simulated,
                requiredRows,
                requiredCols,
                blockedCells,
                preferredRow,
                preferredCol,
                chooseIndex,
                out var row,
                out var col))
        {
            placement = new TiledGridPlacement(0, row, col);
            return true;
        }

        for (var index = 0; index < fifoItems.Count; index++)
        {
            ClearItem(simulated, fifoItems[index]);
            var removeCount = index + 1;
            if (removeCount >= minimum &&
                TryChooseFreeRegion(
                    simulated,
                    requiredRows,
                    requiredCols,
                    blockedCells,
                    preferredRow,
                    preferredCol,
                    chooseIndex,
                    out row,
                    out col))
            {
                placement = new TiledGridPlacement(removeCount, row, col);
                return true;
            }
        }

        return false;
    }

    internal static bool TryRecalculateFifoPlacement(
        bool[,] currentOccupied,
        IReadOnlyList<TiledGridItem> currentFifoItems,
        TiledPlannedGridPlacement plannedPlacement,
        bool[,]? blockedCells,
        int minimumRemoveCount,
        Func<int, int> chooseIndex,
        out TiledGridPlacement recalculatedPlacement)
        => TryFindFifoPlacementCore(
            currentOccupied,
            currentFifoItems,
            plannedPlacement.RowSpan,
            plannedPlacement.ColSpan,
            blockedCells,
            minimumRemoveCount,
            plannedPlacement.Row >= 0 ? plannedPlacement.Row : null,
            plannedPlacement.Col >= 0 ? plannedPlacement.Col : null,
            chooseIndex,
            out recalculatedPlacement);

    internal static bool RequiresRemovalPreviewRetry<T>(
        IReadOnlyList<T> previewedPrefix,
        IReadOnlyList<T> currentItems,
        int currentRemoveCount)
    {
        ArgumentNullException.ThrowIfNull(previewedPrefix);
        ArgumentNullException.ThrowIfNull(currentItems);

        if (currentRemoveCount != previewedPrefix.Count || currentItems.Count < currentRemoveCount)
        {
            return true;
        }

        var comparer = EqualityComparer<T>.Default;
        for (var index = 0; index < currentRemoveCount; index++)
        {
            if (!comparer.Equals(previewedPrefix[index], currentItems[index]))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsDefragPlacementValid(
        bool[,] occupied,
        IReadOnlyList<TiledDefragItem> currentItems,
        int newRow,
        int newCol,
        int newRowSpan,
        int newColSpan,
        IReadOnlyList<TiledDefragMove> moves,
        bool[,]? blockedCells)
    {
        ArgumentNullException.ThrowIfNull(occupied);
        ArgumentNullException.ThrowIfNull(currentItems);
        ArgumentNullException.ThrowIfNull(moves);

        var rows = occupied.GetLength(0);
        var cols = occupied.GetLength(1);
        if (blockedCells is not null &&
            (blockedCells.GetLength(0) != rows || blockedCells.GetLength(1) != cols))
        {
            throw new ArgumentException("Blocked cells must match the occupancy grid dimensions.", nameof(blockedCells));
        }
        if (moves.Count == 0 || !IsRectangleInBounds(newRow, newCol, newRowSpan, newColSpan, rows, cols))
        {
            return false;
        }

        var itemsByPath = new Dictionary<string, TiledDefragItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in currentItems)
        {
            if (string.IsNullOrWhiteSpace(item.Path) || !itemsByPath.TryAdd(item.Path, item))
            {
                return false;
            }
        }

        var movedItems = new List<(TiledDefragItem Item, TiledDefragMove Move)>(moves.Count);
        var movedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var move in moves)
        {
            if (!movedPaths.Add(move.Path) ||
                !itemsByPath.TryGetValue(move.Path, out var item) ||
                !IsRectangleInBounds(item.Row, item.Col, item.RowSpan, item.ColSpan, rows, cols) ||
                !IsRectangleInBounds(move.Row, move.Col, item.RowSpan, item.ColSpan, rows, cols))
            {
                return false;
            }

            movedItems.Add((item, move));
        }

        var simulated = (bool[,])occupied.Clone();
        foreach (var (item, _) in movedItems)
        {
            ClearItem(simulated, new TiledGridItem(item.Row, item.Col, item.RowSpan, item.ColSpan));
        }

        if (!TryFillRectangle(simulated, newRow, newCol, newRowSpan, newColSpan, blockedCells))
        {
            return false;
        }

        foreach (var (item, move) in movedItems)
        {
            if (!TryFillRectangle(simulated, move.Row, move.Col, item.RowSpan, item.ColSpan, blockedCells))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddIfDistinct(List<double> candidates, double scale)
    {
        if (candidates.All(candidate => Math.Abs(candidate - scale) > ScaleTolerance))
        {
            candidates.Add(scale);
        }
    }

    private static bool TryChooseFreeRegion(
        bool[,] occupied,
        int requiredRows,
        int requiredCols,
        bool[,]? blockedCells,
        int? preferredRow,
        int? preferredCol,
        Func<int, int> chooseIndex,
        out int row,
        out int col)
    {
        var rows = occupied.GetLength(0);
        var cols = occupied.GetLength(1);
        if (preferredRow is int rowCandidate && preferredCol is int colCandidate &&
            IsRegionFree(
                occupied,
                rowCandidate,
                colCandidate,
                requiredRows,
                requiredCols,
                blockedCells))
        {
            row = rowCandidate;
            col = colCandidate;
            return true;
        }

        var candidates = new List<(int Row, int Col)>();
        for (var candidateRow = 0; candidateRow <= rows - requiredRows; candidateRow++)
        {
            for (var candidateCol = 0; candidateCol <= cols - requiredCols; candidateCol++)
            {
                if (IsRegionFree(
                        occupied,
                        candidateRow,
                        candidateCol,
                        requiredRows,
                        requiredCols,
                        blockedCells))
                {
                    candidates.Add((candidateRow, candidateCol));
                }
            }
        }

        if (candidates.Count > 0)
        {
            var selectedIndex = chooseIndex(candidates.Count);
            if (selectedIndex < 0 || selectedIndex >= candidates.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(chooseIndex), "The selected index must identify an available placement.");
            }

            (row, col) = candidates[selectedIndex];
            return true;
        }

        row = col = -1;
        return false;
    }

    private static bool IsRegionFree(
        bool[,] occupied,
        int row,
        int col,
        int rowSpan,
        int colSpan,
        bool[,]? blockedCells)
    {
        var rows = occupied.GetLength(0);
        var cols = occupied.GetLength(1);
        if (!IsRectangleInBounds(row, col, rowSpan, colSpan, rows, cols))
        {
            return false;
        }

        for (var r = row; r < row + rowSpan; r++)
        {
            for (var c = col; c < col + colSpan; c++)
            {
                if (occupied[r, c] || blockedCells?[r, c] == true)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryFillRectangle(
        bool[,] occupied,
        int row,
        int col,
        int rowSpan,
        int colSpan,
        bool[,]? blockedCells)
    {
        for (var r = row; r < row + rowSpan; r++)
        {
            for (var c = col; c < col + colSpan; c++)
            {
                if (occupied[r, c] || blockedCells?[r, c] == true)
                {
                    return false;
                }
            }
        }

        for (var r = row; r < row + rowSpan; r++)
        {
            for (var c = col; c < col + colSpan; c++)
            {
                occupied[r, c] = true;
            }
        }

        return true;
    }

    private static bool IsRectangleInBounds(
        int row,
        int col,
        int rowSpan,
        int colSpan,
        int rows,
        int cols)
    {
        return row >= 0 && col >= 0 && rowSpan > 0 && colSpan > 0 &&
            row <= rows - rowSpan && col <= cols - colSpan;
    }

    private static void ClearItem(bool[,] occupied, TiledGridItem item)
    {
        var rows = occupied.GetLength(0);
        var cols = occupied.GetLength(1);
        var startRow = Math.Max(0, item.Row);
        var startCol = Math.Max(0, item.Col);
        var endRow = Math.Min(rows, item.Row + item.RowSpan);
        var endCol = Math.Min(cols, item.Col + item.ColSpan);
        for (var row = startRow; row < endRow; row++)
        {
            for (var col = startCol; col < endCol; col++)
            {
                occupied[row, col] = false;
            }
        }
    }
}
