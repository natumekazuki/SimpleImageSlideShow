using Microsoft.JSInterop;

namespace SimpleImageSlideShow.Components.Pages
{
    public sealed partial class Tiled
    {
        private const double ManualGrowStep = 1.05;

        [JSInvokable]
        public async Task GrowLatestImageAsync()
        {
            if (Occupied is null || Items.Count == 0) return;

            var target = Items.Last();
            var imageSize = await ImageService.GetImageSizeAsync(target.Path);
            if (imageSize is null) return;

            var (origW, origH) = imageSize.Value;
            if (!TryComputeGrownSize(target, origW, origH, out var nextW, out var nextH))
            {
                return;
            }

            var reqCols = Math.Max(1, (int)Math.Ceiling(nextW / TileW));
            var reqRows = Math.Max(1, (int)Math.Ceiling(nextH / TileH));
            if (reqCols > Cols || reqRows > Rows) return;

            var snapshot = TiledItemSnapshot.Capture(target);
            FillCells(target.Row, target.Col, target.RowSpan, target.ColSpan, false);
            SetOwners(target, false);
            Items.RemoveAt(Items.Count - 1);

            var avoidClock = ShowClock && AvoidClockOverlap;
            if (TryApplyManualGrowAtExistingPosition(target, reqRows, reqCols, nextW, nextH, avoidClock) ||
                TryApplyManualGrowAtFreePosition(target, reqRows, reqCols, nextW, nextH, avoidClock) ||
                TryApplyManualGrowWithDefrag(target, reqRows, reqCols, nextW, nextH, avoidClock) ||
                TryApplyManualGrowWithFifo(target, reqRows, reqCols, nextW, nextH, avoidClock) ||
                TryApplyManualGrowWithFifo(target, reqRows, reqCols, nextW, nextH, avoidClock: false))
            {
                InvalidatePlan();
                StateHasChanged();
                try { await EnsurePlanAsync(); } catch { }
                return;
            }

            RestoreManualGrowTarget(target, snapshot);
            InvalidatePlan();
            StateHasChanged();
        }

        private bool TryComputeGrownSize(TiledItem target, double origW, double origH, out double width, out double height)
        {
            width = target.ImgWidth;
            height = target.ImgHeight;

            var factor = ManualGrowStep;
            factor = Math.Min(factor, origW / Math.Max(1.0, width));
            factor = Math.Min(factor, origH / Math.Max(1.0, height));
            factor = Math.Min(factor, GridW / Math.Max(1.0, width));
            factor = Math.Min(factor, GridH / Math.Max(1.0, height));

            if (!double.IsFinite(factor) || factor <= 1.0001)
            {
                return false;
            }

            width *= factor;
            height *= factor;
            return width > target.ImgWidth + 0.5 || height > target.ImgHeight + 0.5;
        }

        private bool TryApplyManualGrowAtExistingPosition(TiledItem target, int rowSpan, int colSpan, double imgWidth, double imgHeight, bool avoidClock)
        {
            if (!CanPlace(target.Row, target.Col, rowSpan, colSpan, avoidClock)) return false;
            ApplyManualGrowTarget(target, target.Row, target.Col, rowSpan, colSpan, imgWidth, imgHeight);
            return true;
        }

        private bool TryApplyManualGrowAtFreePosition(TiledItem target, int rowSpan, int colSpan, double imgWidth, double imgHeight, bool avoidClock)
        {
            if (!TryPlace(rowSpan, colSpan, out var row, out var col, avoidClock)) return false;
            ApplyManualGrowTarget(target, row, col, rowSpan, colSpan, imgWidth, imgHeight);
            return true;
        }

        private bool TryApplyManualGrowWithDefrag(TiledItem target, int rowSpan, int colSpan, double imgWidth, double imgHeight, bool avoidClock)
        {
            if (Occupied is null) return false;

            var simItems = Items.Select(item => new SimItem(item.Path, item.Row, item.Col, item.RowSpan, item.ColSpan)).ToList();
            if (!TryComputeRandomDefragForPlacementSim(rowSpan, colSpan, Occupied, simItems, out var row, out var col, out var moves, avoidClock))
            {
                return false;
            }

            ApplyManualDefragMoves(moves);
            ApplyManualGrowTarget(target, row, col, rowSpan, colSpan, imgWidth, imgHeight);
            return true;
        }

        private bool TryApplyManualGrowWithFifo(TiledItem target, int rowSpan, int colSpan, double imgWidth, double imgHeight, bool avoidClock)
        {
            if (!TryComputeFifoRemovalForPlacement(rowSpan, colSpan, out var removeCount, out var row, out var col, avoidClock))
            {
                return false;
            }

            RemoveOldestItems(removeCount);
            ApplyManualGrowTarget(target, row, col, rowSpan, colSpan, imgWidth, imgHeight);
            return true;
        }

        private void ApplyManualDefragMoves(IReadOnlyList<PlannedMove> moves)
        {
            var moveItems = moves
                .Select(move => (Move: move, Item: Items.FirstOrDefault(item => string.Equals(item.Path, move.Path, StringComparison.OrdinalIgnoreCase))))
                .Where(entry => entry.Item is not null)
                .ToList();

            foreach (var (_, item) in moveItems)
            {
                FillCells(item!.Row, item.Col, item.RowSpan, item.ColSpan, false);
                SetOwners(item, false);
            }

            foreach (var (move, item) in moveItems)
            {
                item!.Row = move.Row;
                item.Col = move.Col;
                var (left, top, width, height) = ComputeJitteredFrame(move.Row, move.Col, item.RowSpan, item.ColSpan);
                item.Left = left;
                item.Top = top;
                item.Width = width;
                item.Height = height;
                FillCells(item.Row, item.Col, item.RowSpan, item.ColSpan, true);
                SetOwners(item, true);
            }
        }

        private void RemoveOldestItems(int count)
        {
            var toRemove = Math.Min(count, Items.Count);
            for (int i = 0; i < toRemove; i++)
            {
                var item = Items[0];
                FillCells(item.Row, item.Col, item.RowSpan, item.ColSpan, false);
                SetOwners(item, false);
                Items.RemoveAt(0);
                UsedPaths.Remove(item.Path);
            }
        }

        private void ApplyManualGrowTarget(TiledItem target, int row, int col, int rowSpan, int colSpan, double imgWidth, double imgHeight)
        {
            target.Row = row;
            target.Col = col;
            target.RowSpan = rowSpan;
            target.ColSpan = colSpan;
            target.ImgWidth = imgWidth;
            target.ImgHeight = imgHeight;
            target.Scale = GetLongEdgeRatio(imgWidth, imgHeight);

            var (left, top, width, height) = ComputeJitteredFrame(row, col, rowSpan, colSpan);
            target.Left = left;
            target.Top = top;
            target.Width = width;
            target.Height = height;
            target.Removing = false;

            FillCells(target.Row, target.Col, target.RowSpan, target.ColSpan, true);
            SetOwners(target, true);
            Items.Add(target);
            UsedPaths.Add(target.Path);
            _lastTickItem = target;
        }

        private void RestoreManualGrowTarget(TiledItem target, TiledItemSnapshot snapshot)
        {
            snapshot.ApplyTo(target);
            FillCells(target.Row, target.Col, target.RowSpan, target.ColSpan, true);
            SetOwners(target, true);
            Items.Add(target);
            UsedPaths.Add(target.Path);
            _lastTickItem = target;
        }

        private double GetLongEdgeRatio(double width, double height)
        {
            var viewportLong = Math.Max(Math.Max(1.0, ViewportW), Math.Max(1.0, ViewportH));
            return Math.Max(width, height) / viewportLong;
        }

        private readonly record struct TiledItemSnapshot(
            int Row,
            int Col,
            int RowSpan,
            int ColSpan,
            double Left,
            double Top,
            double Width,
            double Height,
            double Scale,
            double ImgWidth,
            double ImgHeight,
            bool Removing)
        {
            public static TiledItemSnapshot Capture(TiledItem item) =>
                new(item.Row, item.Col, item.RowSpan, item.ColSpan, item.Left, item.Top, item.Width, item.Height, item.Scale, item.ImgWidth, item.ImgHeight, item.Removing);

            public void ApplyTo(TiledItem item)
            {
                item.Row = Row;
                item.Col = Col;
                item.RowSpan = RowSpan;
                item.ColSpan = ColSpan;
                item.Left = Left;
                item.Top = Top;
                item.Width = Width;
                item.Height = Height;
                item.Scale = Scale;
                item.ImgWidth = ImgWidth;
                item.ImgHeight = ImgHeight;
                item.Removing = Removing;
            }
        }
    }
}
