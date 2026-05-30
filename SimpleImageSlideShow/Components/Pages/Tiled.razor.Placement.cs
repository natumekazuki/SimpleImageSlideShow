
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

        private bool TryComputeFifoRemovalForPlacement(int reqRows, int reqCols, out int removeCount, out int row, out int col, bool avoidClock)
        {
            removeCount = 0; row = col = -1;
            if (Occupied is null) return false;

            // copy occupancy
            var occSim = new bool[Rows, Cols];
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    occSim[r, c] = Occupied[r, c];

            // quick success without removals
            if (TryPlaceSim(reqRows, reqCols, occSim, out row, out col, avoidClock))
            {
                removeCount = 0;
                return true;
            }

            // progressively clear oldest tiles and test
            for (int k = 1; k <= Items.Count; k++)
            {
                var it = Items[k - 1];
                FillCellsSim(it.Row, it.Col, it.RowSpan, it.ColSpan, occSim, false);
                if (TryPlaceSim(reqRows, reqCols, occSim, out row, out col, avoidClock))
                {
                    removeCount = k;
                    return true;
                }
            }

            return false;
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

        private bool TryPlaceAreaBasedNoUpscale(double origW, double origH, string filePath, double lo, double hi, double initialRatio, out TiledItem item, bool avoidClock)
        {
            item = default!;
            for (int attempt = 0; attempt < RandomScaleTries; attempt++)
            {
                var ratio = attempt == 0 ? initialRatio : (lo < hi ? lo + Random.Shared.NextDouble() * (hi - lo) : lo);
                ratio = Math.Clamp(ratio, lo, hi);
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
            for (int attempt = 0; attempt < RandomScaleTries; attempt++)
            {
                var ratio = attempt == 0 ? initialRatio : (lo < hi ? lo + Random.Shared.NextDouble() * (hi - lo) : lo);
                ratio = Math.Clamp(ratio, lo, hi);
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
