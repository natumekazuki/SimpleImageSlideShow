
namespace SimpleImageSlideShow.Components.Pages
{
    public sealed partial class Tiled
    {
        private double OccupancyPercent()
        {
            if (Occupied is null) return 0;
            int used = 0;
            foreach (var it in Items) used += it.ColSpan * it.RowSpan;
            var total = Cols * Rows;
            return total == 0 ? 0 : (100.0 * used / total);
        }

        private bool TryComputeFifoRemovalForPlacement(
            int reqRows,
            int reqCols,
            out int removeCount,
            out int row,
            out int col,
            bool avoidClock,
            int minRemoveCount = 0,
            int plannedRemoveCount = 0,
            int plannedRow = -1,
            int plannedCol = -1)
        {
            removeCount = 0; row = col = -1;
            if (Occupied is null) return false;
            var gridItems = Items
                .Select(item => new TiledGridItem(item.Row, item.Col, item.RowSpan, item.ColSpan))
                .ToArray();
            if (!TiledPlacementPlanner.TryRecalculateFifoPlacement(
                    Occupied,
                    gridItems,
                    new TiledPlannedGridPlacement(
                        plannedRemoveCount,
                        reqRows,
                        reqCols,
                        plannedRow,
                        plannedCol),
                    avoidClock ? ClockCells : null,
                    minRemoveCount,
                    count => Random.Shared.Next(count),
                    out var placement))
            {
                return false;
            }

            removeCount = placement.RemoveCount;
            row = placement.Row;
            col = placement.Col;
            return true;
        }

        private bool TryRecalculatePlacementPlan(PlannedStep originalPlan, out PlannedStep recalculatedPlan)
        {
            recalculatedPlan = originalPlan;
            if (Occupied is null || TileW <= 0 || TileH <= 0) return false;

            var variants = BuildCurrentPlacementVariants(originalPlan);
            if (variants.Count == 0) return false;

            var onScreenIndex = Items.FindIndex(item =>
                string.Equals(item.Path, originalPlan.Path, StringComparison.OrdinalIgnoreCase));
            var minimumRemoveCount = onScreenIndex >= 0 ? onScreenIndex + 1 : 0;
            var avoidClock = ShowClock && AvoidClockOverlap;

            if (minimumRemoveCount == 0)
            {
                var preferredVariant = variants[0];
                if (originalPlan.RemoveCount == 0 && originalPlan.Moves.Count > 0 &&
                    TiledPlacementPlanner.IsDefragPlacementValid(
                        Occupied,
                        Items.Select(item => new TiledDefragItem(
                            item.Path,
                            item.Row,
                            item.Col,
                            item.RowSpan,
                            item.ColSpan)).ToArray(),
                        originalPlan.Row,
                        originalPlan.Col,
                        preferredVariant.RowSpan,
                        preferredVariant.ColSpan,
                        originalPlan.Moves.Select(move => new TiledDefragMove(
                            move.Path,
                            move.Row,
                            move.Col)).ToArray(),
                        avoidClock ? ClockCells : null))
                {
                    recalculatedPlan = WithPlacement(
                        originalPlan,
                        preferredVariant,
                        originalPlan.Row,
                        originalPlan.Col,
                        removeCount: 0,
                        moves: originalPlan.Moves);
                    return true;
                }

                if (originalPlan.RemoveCount == 0 && originalPlan.Moves.Count == 0 &&
                    CanPlace(
                        originalPlan.Row,
                        originalPlan.Col,
                        preferredVariant.RowSpan,
                        preferredVariant.ColSpan,
                        avoidClock))
                {
                    recalculatedPlan = WithPlacement(
                        originalPlan,
                        preferredVariant,
                        originalPlan.Row,
                        originalPlan.Col,
                        removeCount: 0,
                        moves: []);
                    return true;
                }

                foreach (var variant in variants)
                {
                    if (TryPlace(variant.RowSpan, variant.ColSpan, out var row, out var col, avoidClock))
                    {
                        recalculatedPlan = WithPlacement(originalPlan, variant, row, col, removeCount: 0, moves: []);
                        return true;
                    }
                }

                foreach (var variant in variants)
                {
                    if (TryComputeRandomDefragForPlacement(
                            variant.RowSpan,
                            variant.ColSpan,
                            out var row,
                            out var col,
                            out var moves,
                            avoidClock))
                    {
                        recalculatedPlan = WithPlacement(originalPlan, variant, row, col, removeCount: 0, moves);
                        return true;
                    }
                }
            }

            if (TrySelectBestFifoPlacement(
                    originalPlan,
                    variants,
                    avoidClock,
                    minimumRemoveCount,
                    out recalculatedPlan))
            {
                return true;
            }

            return avoidClock && TrySelectBestFifoPlacement(
                originalPlan,
                variants,
                avoidClock: false,
                minimumRemoveCount,
                out recalculatedPlan);
        }

        private List<PlacementVariant> BuildCurrentPlacementVariants(PlannedStep plan)
        {
            var variants = new List<PlacementVariant>(2);
            var range = GetCurrentScaleRange(plan.OrigWidth, plan.OrigHeight);
            var scaleCandidates = TiledPlacementPlanner.CreateScaleCandidates(
                range.Min,
                range.Max,
                plan.Scale,
                randomTryCount: 0,
                nextDouble: static () => 0);
            foreach (var scale in scaleCandidates)
            {
                AddVariant(scale);
            }
            return variants;

            void AddVariant(double scale)
            {
                var (width, height) = ComputeViewportLongEdgeTargetNoUpscale(
                    plan.OrigWidth,
                    plan.OrigHeight,
                    scale,
                    clampToGrid: true);
                var colSpan = Math.Max(1, (int)Math.Ceiling(width / TileW));
                var rowSpan = Math.Max(1, (int)Math.Ceiling(height / TileH));
                if (colSpan > Cols || rowSpan > Rows) return;
                if (variants.Any(variant => variant.RowSpan == rowSpan && variant.ColSpan == colSpan)) return;
                variants.Add(new PlacementVariant(scale, width, height, rowSpan, colSpan));
            }
        }

        private TiledScaleRange GetCurrentScaleRange(double origWidth, double origHeight)
            => TiledPlacementPlanner.CalculateScaleRange(
                MinScale,
                MaxScale,
                ViewportW,
                ViewportH,
                origWidth,
                origHeight,
                ShrinkGuardThreshold);

        private bool TrySelectBestFifoPlacement(
            PlannedStep originalPlan,
            IReadOnlyList<PlacementVariant> variants,
            bool avoidClock,
            int minimumRemoveCount,
            out PlannedStep selectedPlan)
        {
            selectedPlan = originalPlan;
            PlacementVariant? selectedVariant = null;
            var selectedRemoveCount = int.MaxValue;
            var selectedRow = -1;
            var selectedCol = -1;

            foreach (var variant in variants)
            {
                if (!TryComputeFifoRemovalForPlacement(
                        variant.RowSpan,
                        variant.ColSpan,
                        out var removeCount,
                        out var row,
                        out var col,
                        avoidClock,
                        minimumRemoveCount,
                        originalPlan.RemoveCount,
                        originalPlan.Row,
                        originalPlan.Col))
                {
                    continue;
                }

                if (removeCount < selectedRemoveCount ||
                    (removeCount == selectedRemoveCount &&
                     (selectedVariant is null || variant.Scale > selectedVariant.Value.Scale)))
                {
                    selectedVariant = variant;
                    selectedRemoveCount = removeCount;
                    selectedRow = row;
                    selectedCol = col;
                }
            }

            if (selectedVariant is null) return false;
            selectedPlan = WithPlacement(
                originalPlan,
                selectedVariant.Value,
                selectedRow,
                selectedCol,
                selectedRemoveCount,
                moves: []);
            return true;
        }

        private static PlannedStep WithPlacement(
            PlannedStep plan,
            PlacementVariant variant,
            int row,
            int col,
            int removeCount,
            IReadOnlyList<PlannedMove> moves)
            => plan with
            {
                Row = row,
                Col = col,
                RowSpan = variant.RowSpan,
                ColSpan = variant.ColSpan,
                Scale = variant.Scale,
                ImgWidth = variant.ImgWidth,
                ImgHeight = variant.ImgHeight,
                RemoveCount = removeCount,
                Moves = moves
            };

        private readonly record struct PlacementVariant(
            double Scale,
            double ImgWidth,
            double ImgHeight,
            int RowSpan,
            int ColSpan);

        private bool TryComputeRandomDefragForPlacement(int reqRows, int reqCols, out int row, out int col, out IReadOnlyList<PlannedMove> moves, bool avoidClock)
        {
            row = col = -1;
            moves = [];
            if (Occupied is null || Items.Count == 0 || DefragTargetCount == 0 || DefragTries == 0) return false;

            var simItems = Items.Select(it => new SimItem(it.Path, it.Row, it.Col, it.RowSpan, it.ColSpan)).ToList();
            return TryComputeRandomDefragForPlacementSim(reqRows, reqCols, Occupied, simItems, out row, out col, out moves, avoidClock);
        }

        private bool TryComputeRandomDefragForPlacementSim(
            int reqRows,
            int reqCols,
            bool[,] occ,
            List<SimItem> simItems,
            out int row,
            out int col,
            out IReadOnlyList<PlannedMove> moves,
            bool avoidClock)
        {
            row = col = -1;
            moves = [];
            if (simItems.Count == 0 || DefragTargetCount == 0 || DefragTries == 0) return false;

            int rows = occ.GetLength(0);
            int cols = occ.GetLength(1);
            int moveCount = Math.Min((int)DefragTargetCount, simItems.Count);

            for (int attempt = 0; attempt < DefragTries; attempt++)
            {
                var selected = PickRandomSimItems(simItems, moveCount);
                if (selected.Count == 0) continue;

                var occSim = new bool[rows, cols];
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                        occSim[r, c] = occ[r, c];

                foreach (var it in selected)
                {
                    FillCellsSim(it.Row, it.Col, it.RowSpan, it.ColSpan, occSim, false);
                }

                if (!TryPlaceSim(reqRows, reqCols, occSim, out var newRow, out var newCol, avoidClock)) continue;
                FillCellsSim(newRow, newCol, reqRows, reqCols, occSim, true);

                var plannedMoves = new List<PlannedMove>(selected.Count);
                var failed = false;
                foreach (var it in selected)
                {
                    if (!TryPlaceSim(it.RowSpan, it.ColSpan, occSim, out var moveRow, out var moveCol, avoidClock))
                    {
                        failed = true;
                        break;
                    }

                    FillCellsSim(moveRow, moveCol, it.RowSpan, it.ColSpan, occSim, true);
                    plannedMoves.Add(new PlannedMove(it.Path, moveRow, moveCol));
                }

                if (failed) continue;

                row = newRow;
                col = newCol;
                moves = plannedMoves;
                return true;
            }

            return false;
        }

        private static List<SimItem> PickRandomSimItems(List<SimItem> simItems, int count)
        {
            var selected = new List<SimItem>(count);
            var used = new HashSet<int>();
            while (selected.Count < count && used.Count < simItems.Count)
            {
                var index = Random.Shared.Next(simItems.Count);
                if (used.Add(index))
                {
                    selected.Add(simItems[index]);
                }
            }

            return selected;
        }

        private bool TryPlaceSim(int rowSpan, int colSpan, bool[,] occ, out int row, out int col, bool avoidClock)
        {
            row = col = -1;
            int rows = occ.GetLength(0);
            int cols = occ.GetLength(1);

            // Build all candidate top-left positions and shuffle for random probing order
            int maxR = Math.Max(0, rows - rowSpan + 1);
            int maxC = Math.Max(0, cols - colSpan + 1);
            if (maxR == 0 || maxC == 0) return false;

            var candidates = new List<(int r, int c)>(maxR * maxC);
            for (int r = 0; r < maxR; r++)
                for (int c = 0; c < maxC; c++)
                    candidates.Add((r, c));

            // Fisher–Yates shuffle
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }

            foreach (var (r, c) in candidates)
            {
                bool ok = true;
                for (int rr = r; rr < r + rowSpan && ok; rr++)
                {
                    for (int cc = c; cc < c + colSpan; cc++)
                    {
                        if (occ[rr, cc] || (avoidClock && IsClockCell(rr, cc))) { ok = false; break; }
                    }
                }
                if (ok) { row = r; col = c; return true; }
            }
            return false;
        }

        private static void FillCellsSim(int row, int col, int rowSpan, int colSpan, bool[,] occ, bool value)
        {
            int rows = occ.GetLength(0);
            int cols = occ.GetLength(1);
            for (int r = row; r < row + rowSpan && r < rows; r++)
                for (int c = col; c < col + colSpan && c < cols; c++)
                    occ[r, c] = value;
        }

        private (double sw, double sh) ComputeViewportAreaTargetNoUpscale(double origW, double origH, double ratio, bool clampToGrid)
        {
            var a = origH > 0 ? (origW / origH) : 1.0;
            var screenArea = Math.Max(1.0, ViewportW) * Math.Max(1.0, ViewportH);
            var targetArea = Math.Max(0.0, ratio) * screenArea;
            var desW = Math.Sqrt(targetArea * a);
            var desH = Math.Sqrt(targetArea / Math.Max(1e-9, a));
            // 先にアップスケール禁止（原寸上限）を適用
            var sw = Math.Min(desW, origW);
            var sh = Math.Min(desH, origH);
            // グリッドに収まるようアスペクト比を保って一様スケール
            if (clampToGrid)
            {
                var s = Math.Min(GridW / Math.Max(1.0, sw), GridH / Math.Max(1.0, sh));
                s = Math.Min(1.0, s);
                sw *= s; sh *= s;
            }
            if (!double.IsFinite(sw) || sw <= 0) sw = Math.Min(origW, GridW);
            if (!double.IsFinite(sh) || sh <= 0) sh = Math.Min(origH, GridH);
            return (sw, sh);
        }

        private (double sw, double sh) ComputeViewportLongEdgeTargetNoUpscale(double origW, double origH, double ratio, bool clampToGrid)
        {
            var a = origH > 0 ? (origW / origH) : 1.0;
            var vLong = Math.Max(Math.Max(1.0, ViewportW), Math.Max(1.0, ViewportH));
            var desW = 0.0; var desH = 0.0;
            if (a >= 1.0)
            {
                desW = Math.Max(0.0, ratio) * vLong;
                desH = desW / Math.Max(1e-9, a);
            }
            else
            {
                desH = Math.Max(0.0, ratio) * vLong;
                desW = desH * a;
            }
            // 先にアップスケール禁止（原寸上限）
            var sw = Math.Min(desW, origW);
            var sh = Math.Min(desH, origH);
            // グリッドに収める（等倍スケール）
            if (clampToGrid)
            {
                var s = Math.Min(GridW / Math.Max(1.0, sw), GridH / Math.Max(1.0, sh));
                s = Math.Min(1.0, s);
                sw *= s; sh *= s;
            }
            if (!double.IsFinite(sw) || sw <= 0) sw = Math.Min(origW, GridW);
            if (!double.IsFinite(sh) || sh <= 0) sh = Math.Min(origH, GridH);
            return (sw, sh);
        }

        private (double left, double top, double width, double height) ComputeJitteredFrame(int row, int col, int rowSpan, int colSpan)
        {
            var areaW = colSpan * TileW;
            var areaH = rowSpan * TileH;
            var slackX = Math.Min(PositionJitterMaxPx, areaW * PositionJitterRatio);
            var slackY = Math.Min(PositionJitterMaxPx, areaH * PositionJitterRatio);
            var width = Math.Max(1.0, areaW - slackX);
            var height = Math.Max(1.0, areaH - slackY);
            var jitterX = slackX > 0 ? Random.Shared.NextDouble() * slackX : 0;
            var jitterY = slackY > 0 ? Random.Shared.NextDouble() * slackY : 0;
            var left = OffsetX + col * TileW + jitterX;
            var top = OffsetY + row * TileH + jitterY;
            return (left, top, width, height);
        }

        private TiledItem CreateTiledItem(string path, int row, int col, int rowSpan, int colSpan, double scale, double imgWidth, double imgHeight, string src)
        {
            var (left, top, width, height) = ComputeJitteredFrame(row, col, rowSpan, colSpan);
            return new TiledItem
            {
                Path = path,
                Row = row,
                Col = col,
                RowSpan = rowSpan,
                ColSpan = colSpan,
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                Scale = scale,
                ImgWidth = imgWidth,
                ImgHeight = imgHeight,
                Src = src
            };
        }

        private void ApplyDefragPlacement(TiledItem newItem, IReadOnlyList<PlannedMove> moves)
        {
            var moveItems = moves
                .Select(move => (Move: move, Item: Items.FirstOrDefault(item => string.Equals(item.Path, move.Path, StringComparison.OrdinalIgnoreCase))))
                .ToList();

            if (moveItems.Any(entry => entry.Item is null) ||
                moveItems.Select(entry => entry.Move.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != moves.Count)
            {
                throw new InvalidOperationException("The defragmentation plan no longer matches the current tiled items.");
            }

            foreach (var (_, item) in moveItems)
            {
                FillCells(item!.Row, item.Col, item.RowSpan, item.ColSpan, false);
                SetOwners(item, false);
            }

            FillCells(newItem.Row, newItem.Col, newItem.RowSpan, newItem.ColSpan, true);
            SetOwners(newItem, true);
            Items.Add(newItem);
            UsedPaths.Add(newItem.Path);
            AddCooldown(newItem.Path);

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

        private bool TryPlaceAreaBasedNoUpscale(double origW, double origH, string filePath, double lo, double hi, double initialRatio, out TiledItem item, bool avoidClock)
        {
            item = default!;
            var scaleCandidates = TiledPlacementPlanner.CreateScaleCandidates(
                lo,
                hi,
                initialRatio,
                RandomScaleTries,
                Random.Shared.NextDouble);
            foreach (var ratio in scaleCandidates)
            {
                var (sw, sh) = ComputeViewportAreaTargetNoUpscale(origW, origH, ratio, clampToGrid: true);

                int reqCols = Math.Max(1, (int)Math.Ceiling(sw / TileW));
                int reqRows = Math.Max(1, (int)Math.Ceiling(sh / TileH));

                int maxCols = Math.Min(Cols, reqCols + 2);
                int maxRows = Math.Min(Rows, reqRows + 2);

                for (int rs = reqRows; rs <= maxRows; rs++)
                {
                    for (int cs = reqCols; cs <= maxCols; cs++)
                    {
                        if (TryPlace(rs, cs, out var r, out var c, avoidClock))
                        {
                            var src = BuildVirtualHostUrl(filePath);
                            item = CreateTiledItem(filePath, r, c, rs, cs, ratio, sw, sh, src);
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private bool TryPlaceLongEdgeBasedNoUpscale(double origW, double origH, string filePath, double lo, double hi, double initialRatio, out TiledItem item, bool avoidClock)
        {
            item = default!;
            var scaleCandidates = TiledPlacementPlanner.CreateScaleCandidates(
                lo,
                hi,
                initialRatio,
                RandomScaleTries,
                Random.Shared.NextDouble);
            foreach (var ratio in scaleCandidates)
            {
                var (sw, sh) = ComputeViewportLongEdgeTargetNoUpscale(origW, origH, ratio, clampToGrid: true);

                int reqCols = Math.Max(1, (int)Math.Ceiling(sw / TileW));
                int reqRows = Math.Max(1, (int)Math.Ceiling(sh / TileH));

                int maxCols = Math.Min(Cols, reqCols + 2);
                int maxRows = Math.Min(Rows, reqRows + 2);

                for (int rs = reqRows; rs <= maxRows; rs++)
                {
                    for (int cs = reqCols; cs <= maxCols; cs++)
                    {
                        if (TryPlace(rs, cs, out var r, out var c, avoidClock))
                        {
                            var src = BuildVirtualHostUrl(filePath);
                            item = CreateTiledItem(filePath, r, c, rs, cs, ratio, sw, sh, src);
                            return true;
                        }
                    }
                }
            }
            return false;
        }
        private void SetOwners(TiledItem item, bool set)
        {
            if (Owners is null) return;
            for (int r = item.Row; r < item.Row + item.RowSpan; r++)
            {
                for (int c = item.Col; c < item.Col + item.ColSpan; c++)
                {
                    Owners[r, c] = set ? item : null;
                }
            }
        }

        private double GetBaseFitScaleFromDims(double width, double height)
        {
            // If original is larger than grid area, scale down to fit within grid, else 1.0
            var sx = GridW / Math.Max(1.0, width);
            var sy = GridH / Math.Max(1.0, height);
            var fit = Math.Min(1.0, Math.Min(sx, sy));
            return double.IsFinite(fit) && fit > 0 ? fit : 1.0;
        }


        private bool TryPlace(int rowSpan, int colSpan, out int row, out int col, bool avoidClock)
        {
            row = col = 0;
            if (Occupied is null) return false;
            var candidates = new List<(int r, int c)>();
            for (int r = 0; r <= Rows - rowSpan; r++)
            {
                for (int c = 0; c <= Cols - colSpan; c++)
                {
                    if (CanPlace(r, c, rowSpan, colSpan, avoidClock)) candidates.Add((r, c));
                }
            }
            if (candidates.Count == 0) return false;
            var pick = candidates[Random.Shared.Next(candidates.Count)];
            row = pick.r; col = pick.c; return true;
        }

        private bool CanPlace(int row, int col, int rowSpan, int colSpan, bool avoidClock)
        {
            if (Occupied is null) return false;
            for (int r = row; r < row + rowSpan; r++)
            {
                for (int c = col; c < col + colSpan; c++)
                {
                    if (r < 0 || r >= Rows || c < 0 || c >= Cols) return false;
                    if (Occupied[r, c]) return false;
                    if (avoidClock && IsClockCell(r, c)) return false;
                }
            }
            return true;
        }

        private void FillCells(int row, int col, int rowSpan, int colSpan, bool value)
        {
            if (Occupied is null) return;
            for (int r = row; r < row + rowSpan; r++)
            {
                for (int c = col; c < col + colSpan; c++)
                {
                    Occupied[r, c] = value;
                }
            }
            UpdateClockOverlap();
        }

        private void RemoveClockOverlaps()
        {
            if (!AvoidClockOverlap || !ShowClock || ClockCells is null || Occupied is null) return;

            var removedAny = false;
            foreach (var item in Items.ToList())
            {
                if (!IsOverlappingClock(item.Row, item.Col, item.RowSpan, item.ColSpan)) continue;

                FillCells(item.Row, item.Col, item.RowSpan, item.ColSpan, false);
                SetOwners(item, false);
                Items.Remove(item);
                UsedPaths.Remove(item.Path);
                removedAny = true;
            }

            if (removedAny)
            {
                UpdateClockOverlap();
                InvalidatePlan();
            }
        }

    }
}
