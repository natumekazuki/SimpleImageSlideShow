using Microsoft.JSInterop;

namespace SimpleImageSlideShow.Components.Pages
{
    public sealed partial class Tiled
    {
        private void InvalidatePlan()
        {
            _planQueue.Clear();
            _planCoordinator.Invalidate();
        }

        private async Task<TiledItem?> ApplyPlannedOrStepAsync(CancellationToken playbackCancellationToken = default)
        {
            var item = await _planCoordinator.RunConsumerAsync(
                hasQueuedPlan: () => _planQueue.Count > 0,
                applyQueuedPlan: async cancellationToken =>
                {
                    var plan = _planQueue[0];
                    _planQueue.RemoveAt(0);
                    return await ApplyPlacementPlanAsync(plan, cancellationToken);
                },
                applyDirectStep: cancellationToken => AddWithFifoRemovalAsync(cancellationToken),
                playbackCancellationToken);
            try { await EnsurePlanAsync(); } catch { }
            return item;
        }

        private async Task<TiledItem?> ApplyPlacementPlanAsync(
            PlannedStep originalPlan,
            CancellationToken cancellationToken = default)
        {
            if (!TryRecalculatePlacementPlan(originalPlan, out var currentPlan))
            {
                InvalidatePlan();
                return null;
            }

            while (true)
            {
                var layoutVersion = _planCoordinator.CurrentGeneration;
                var previewRemoveCount = Math.Min(currentPlan.RemoveCount, Items.Count);
                if (previewRemoveCount == 0) break;

                var previewedPrefix = Items.Take(previewRemoveCount).Select(item => item.Id).ToArray();
                for (var i = 0; i < previewRemoveCount; i++) Items[i].Removing = true;
                StateHasChanged();
                try
                {
                    await Task.Delay(300, cancellationToken);
                }
                finally
                {
                    foreach (var item in Items) item.Removing = false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (_planCoordinator.IsCurrent(layoutVersion)) break;

                if (!TryRecalculatePlacementPlan(originalPlan, out currentPlan))
                {
                    InvalidatePlan();
                    StateHasChanged();
                    return null;
                }

                var recalculatedRemoveCount = Math.Min(currentPlan.RemoveCount, Items.Count);
                if (!TiledPlacementPlanner.RequiresRemovalPreviewRetry(
                        previewedPrefix,
                        Items.Select(item => item.Id).ToArray(),
                        recalculatedRemoveCount))
                {
                    break;
                }

                StateHasChanged();
            }

            if (!PlacementPlansEquivalent(originalPlan, currentPlan))
            {
                InvalidatePlan();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var toRemove = Math.Min(currentPlan.RemoveCount, Items.Count);
            for (var i = 0; i < toRemove; i++)
            {
                var item = Items[0];
                FillCells(item.Row, item.Col, item.RowSpan, item.ColSpan, false);
                SetOwners(item, false);
                Items.RemoveAt(0);
                UsedPaths.Remove(item.Path);
            }

            var addedItem = CreateTiledItem(
                currentPlan.Path,
                currentPlan.Row,
                currentPlan.Col,
                currentPlan.RowSpan,
                currentPlan.ColSpan,
                currentPlan.Scale,
                currentPlan.ImgWidth,
                currentPlan.ImgHeight,
                currentPlan.Src);
            if (currentPlan.Moves.Count > 0)
            {
                ApplyDefragPlacement(addedItem, currentPlan.Moves);
            }
            else
            {
                FillCells(addedItem.Row, addedItem.Col, addedItem.RowSpan, addedItem.ColSpan, true);
                SetOwners(addedItem, true);
                Items.Add(addedItem);
                UsedPaths.Add(currentPlan.Path);
                AddCooldown(currentPlan.Path);
            }

            return addedItem;
        }

        private static bool PlacementPlansEquivalent(PlannedStep left, PlannedStep right)
        {
            if (!string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase) ||
                left.Row != right.Row || left.Col != right.Col ||
                left.RowSpan != right.RowSpan || left.ColSpan != right.ColSpan ||
                left.RemoveCount != right.RemoveCount ||
                Math.Abs(left.Scale - right.Scale) > 1e-9 ||
                left.Moves.Count != right.Moves.Count)
            {
                return false;
            }

            return left.Moves.Zip(right.Moves).All(pair => pair.First == pair.Second);
        }

        private record SimItem(string Path, int Row, int Col, int RowSpan, int ColSpan);

        private Task EnsurePlanAsync()
            => _planCoordinator.RunExclusiveAsync(EnsurePlanCoreAsync);

        private async Task EnsurePlanCoreAsync(long generation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_planCoordinator.IsCurrent(generation) || Occupied is null) return;
            // Fill up to capacity
            int need = PlanCapacity - _planQueue.Count;
            if (need <= 0) return;

            // Simulation state based on current real state plus existing plan
            var occSim = (bool[,])Occupied.Clone();
            var simItems = new List<SimItem>(Items.Select(it => new SimItem(it.Path, it.Row, it.Col, it.RowSpan, it.ColSpan)));
            var plannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ps in _planQueue)
            {
                // Apply already planned steps to simulation so we stack further plans correctly
                // Remove FIFO items as indicated
                int toRemove = Math.Min(ps.RemoveCount, simItems.Count);
                for (int i = 0; i < toRemove; i++)
                {
                    var it = simItems[0];
                    FillCellsSim(it.Row, it.Col, it.RowSpan, it.ColSpan, occSim, false);
                    simItems.RemoveAt(0);
                }
                // Add planned item
                foreach (var move in ps.Moves)
                {
                    var moveIndex = simItems.FindIndex(item => string.Equals(item.Path, move.Path, StringComparison.OrdinalIgnoreCase));
                    if (moveIndex < 0) continue;

                    var it = simItems[moveIndex];
                    FillCellsSim(it.Row, it.Col, it.RowSpan, it.ColSpan, occSim, false);
                    simItems[moveIndex] = it with { Row = move.Row, Col = move.Col };
                }
                FillCellsSim(ps.Row, ps.Col, ps.RowSpan, ps.ColSpan, occSim, true);
                simItems.Add(new SimItem(ps.Path, ps.Row, ps.Col, ps.RowSpan, ps.ColSpan));
                foreach (var move in ps.Moves)
                {
                    var it = simItems.FirstOrDefault(item => string.Equals(item.Path, move.Path, StringComparison.OrdinalIgnoreCase));
                    if (it is not null)
                    {
                        FillCellsSim(it.Row, it.Col, it.RowSpan, it.ColSpan, occSim, true);
                    }
                }
                plannedPaths.Add(ps.Path);
            }

            // Build a used-set for planning (current + screen + planned)
            var usedForPlan = new HashSet<string>(UsedPaths, StringComparer.OrdinalIgnoreCase);
            foreach (var it in simItems) usedForPlan.Add(it.Path);
            foreach (var p in plannedPaths) usedForPlan.Add(p);

            for (int n = 0; n < need; n++)
            {
                var plan = await ComputeOnePlanAsync(occSim, simItems, usedForPlan, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!_planCoordinator.IsCurrent(generation)) return;
                if (plan is null) break;
                var pathAlreadyPresent = UsedPaths.Contains(plan.Path) ||
                    Items.Any(item => string.Equals(item.Path, plan.Path, StringComparison.OrdinalIgnoreCase)) ||
                    _planQueue.Any(item => string.Equals(item.Path, plan.Path, StringComparison.OrdinalIgnoreCase));
                if (!_planCoordinator.CanAppend(
                        generation,
                        _planQueue.Count,
                        PlanCapacity,
                        pathAlreadyPresent))
                {
                    if (!_planCoordinator.IsCurrent(generation) || _planQueue.Count >= PlanCapacity) return;
                    usedForPlan.Add(plan.Path);
                    continue;
                }
                _planQueue.Add(plan);
                // Apply to sim
                int toRemove = Math.Min(plan.RemoveCount, simItems.Count);
                for (int i = 0; i < toRemove; i++)
                {
                    var it = simItems[0];
                    FillCellsSim(it.Row, it.Col, it.RowSpan, it.ColSpan, occSim, false);
                    simItems.RemoveAt(0);
                }
                foreach (var move in plan.Moves)
                {
                    var moveIndex = simItems.FindIndex(item => string.Equals(item.Path, move.Path, StringComparison.OrdinalIgnoreCase));
                    if (moveIndex < 0) continue;

                    var it = simItems[moveIndex];
                    FillCellsSim(it.Row, it.Col, it.RowSpan, it.ColSpan, occSim, false);
                    simItems[moveIndex] = it with { Row = move.Row, Col = move.Col };
                }
                FillCellsSim(plan.Row, plan.Col, plan.RowSpan, plan.ColSpan, occSim, true);
                simItems.Add(new SimItem(plan.Path, plan.Row, plan.Col, plan.RowSpan, plan.ColSpan));
                foreach (var move in plan.Moves)
                {
                    var it = simItems.FirstOrDefault(item => string.Equals(item.Path, move.Path, StringComparison.OrdinalIgnoreCase));
                    if (it is not null)
                    {
                        FillCellsSim(it.Row, it.Col, it.RowSpan, it.ColSpan, occSim, true);
                    }
                }
                usedForPlan.Add(plan.Path);
                try { await PreloadImageUrlAsync(plan.Src, cancellationToken); } catch when (!cancellationToken.IsCancellationRequested) { }
                cancellationToken.ThrowIfCancellationRequested();
                if (!_planCoordinator.IsCurrent(generation)) return;
            }
        }

        private async Task<PlannedStep?> ComputeOnePlanAsync(
            bool[,] occSim,
            List<SimItem> simItems,
            HashSet<string> usedForPlan,
            CancellationToken cancellationToken)
        {
            const int imageTries = 40;
            for (int t = 0; t < imageTries; t++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var imagePath = await GetRandomUnusedPathForPlanAsync(usedForPlan, cancellationToken);
                if (string.IsNullOrWhiteSpace(imagePath)) return null;
                var size = await ImageService.GetImageSizeAsync(imagePath);
                cancellationToken.ThrowIfCancellationRequested();
                if (size is null) continue;
                var (origW, origH) = size.Value;

                var (lo, hi) = GetCurrentScaleRange(origW, origH);
                var rand = lo < hi ? lo + Random.Shared.NextDouble() * (hi - lo) : lo;
                var (sw, sh) = ComputeViewportLongEdgeTargetNoUpscale(origW, origH, rand, clampToGrid: true);
                int reqCols = Math.Max(1, (int)Math.Ceiling(sw / TileW));
                int reqRows = Math.Max(1, (int)Math.Ceiling(sh / TileH));
                var avoidClock = ShowClock && AvoidClockOverlap;
                if (TryPlaceSim(reqRows, reqCols, occSim, out var r0, out var c0, avoidClock: avoidClock))
                {
                    return new PlannedStep
                    {
                        Path = imagePath,
                        Row = r0,
                        Col = c0,
                        RowSpan = reqRows,
                        ColSpan = reqCols,
                        Scale = rand,
                        OrigWidth = origW,
                        OrigHeight = origH,
                        ImgWidth = sw,
                        ImgHeight = sh,
                        Src = BuildVirtualHostUrl(imagePath),
                        RemoveCount = 0
                    };
                }

                if (TryComputeRandomDefragForPlacementSim(reqRows, reqCols, occSim, simItems, out var defragRow, out var defragCol, out var moves, avoidClock: avoidClock))
                {
                    return new PlannedStep
                    {
                        Path = imagePath,
                        Row = defragRow,
                        Col = defragCol,
                        RowSpan = reqRows,
                        ColSpan = reqCols,
                        Scale = rand,
                        OrigWidth = origW,
                        OrigHeight = origH,
                        ImgWidth = sw,
                        ImgHeight = sh,
                        Src = BuildVirtualHostUrl(imagePath),
                        RemoveCount = 0,
                        Moves = moves
                    };
                }

                // Always include the minimum scale so a valid no-removal placement is not left to chance.
                var scaleCandidates = TiledPlacementPlanner.CreateScaleCandidates(
                    lo,
                    hi,
                    rand,
                    RandomScaleTries,
                    Random.Shared.NextDouble);
                foreach (var rtry in scaleCandidates.Skip(1))
                {
                    var (swD, shD) = ComputeViewportLongEdgeTargetNoUpscale(origW, origH, rtry, clampToGrid: true);
                    int reqColsD = Math.Max(1, (int)Math.Ceiling(swD / TileW));
                    int reqRowsD = Math.Max(1, (int)Math.Ceiling(shD / TileH));
                    if (reqColsD <= Cols && reqRowsD <= Rows && TryPlaceSim(reqRowsD, reqColsD, occSim, out var rD, out var cD, avoidClock: avoidClock))
                    {
                        return new PlannedStep
                        {
                            Path = imagePath,
                            Row = rD,
                            Col = cD,
                            RowSpan = reqRowsD,
                            ColSpan = reqColsD,
                            Scale = rtry,
                            OrigWidth = origW,
                            OrigHeight = origH,
                            ImgWidth = swD,
                            ImgHeight = shD,
                            Src = BuildVirtualHostUrl(imagePath),
                            RemoveCount = 0
                        };
                    }
                }

                if (TryComputeBestFifoPlanSim(
                        imagePath,
                        origW,
                        origH,
                        lo,
                        rand,
                        occSim,
                        simItems,
                        avoidClock,
                        out var fifoPlan))
                {
                    return fifoPlan;
                }
            }
            return null;
        }

        private bool TryComputeBestFifoPlanSim(
            string imagePath,
            double origWidth,
            double origHeight,
            double minScale,
            double initialScale,
            bool[,] occupied,
            List<SimItem> simItems,
            bool avoidClock,
            out PlannedStep plan)
        {
            plan = default!;
            return TryForClockPolicy(avoidClock, out plan) ||
                   (avoidClock && TryForClockPolicy(avoidClock: false, out plan));

            bool TryForClockPolicy(bool avoidClock, out PlannedStep selected)
            {
                selected = default!;
                var selectedRemoveCount = int.MaxValue;
                foreach (var scale in new[] { initialScale, minScale }.Distinct())
                {
                    var (width, height) = ComputeViewportLongEdgeTargetNoUpscale(
                        origWidth,
                        origHeight,
                        scale,
                        clampToGrid: true);
                    var colSpan = Math.Max(1, (int)Math.Ceiling(width / TileW));
                    var rowSpan = Math.Max(1, (int)Math.Ceiling(height / TileH));
                    if (colSpan > Cols || rowSpan > Rows ||
                        !TryComputeFifoRemovalForPlacementSim(
                            rowSpan,
                            colSpan,
                            occupied,
                            simItems,
                            out var removeCount,
                            out var row,
                            out var col,
                            avoidClock))
                    {
                        continue;
                    }

                    if (removeCount > selectedRemoveCount ||
                        (removeCount == selectedRemoveCount && selected is not null && scale <= selected.Scale))
                    {
                        continue;
                    }

                    selectedRemoveCount = removeCount;
                    selected = new PlannedStep
                    {
                        Path = imagePath,
                        Row = row,
                        Col = col,
                        RowSpan = rowSpan,
                        ColSpan = colSpan,
                        Scale = scale,
                        OrigWidth = origWidth,
                        OrigHeight = origHeight,
                        ImgWidth = width,
                        ImgHeight = height,
                        Src = BuildVirtualHostUrl(imagePath),
                        RemoveCount = removeCount
                    };
                }

                return selected is not null;
            }
        }

        private bool TryComputeFifoRemovalForPlacementSim(int reqRows, int reqCols, bool[,] occ, List<SimItem> simItems, out int removeCount, out int row, out int col, bool avoidClock)
        {
            removeCount = 0; row = col = -1;
            var gridItems = simItems
                .Select(item => new TiledGridItem(item.Row, item.Col, item.RowSpan, item.ColSpan))
                .ToArray();
            if (!TiledPlacementPlanner.TryFindFifoPlacement(
                    occ,
                    gridItems,
                    reqRows,
                    reqCols,
                    avoidClock ? ClockCells : null,
                    minimumRemoveCount: 0,
                    chooseIndex: count => Random.Shared.Next(count),
                    out var placement))
            {
                return false;
            }

            removeCount = placement.RemoveCount;
            row = placement.Row;
            col = placement.Col;
            return true;
        }

        private async Task<string> GetRandomUnusedPathForPlanAsync(
            HashSet<string> additionallyUsed,
            CancellationToken cancellationToken)
        {
            int tries = GetImageTryCount();
            CleanupCooldown();
            var now = DateTime.UtcNow;
            for (int i = 0; i < tries; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var p = TakeRandomImageFromStock(additionallyUsed);
                if (string.IsNullOrWhiteSpace(p)) return string.Empty;
                if (UsedPaths.Contains(p) || additionallyUsed.Contains(p)) { await Task.Yield(); continue; }
                if (_cooldown.TryGetValue(p, out var until) && until > now) { await Task.Yield(); continue; }
                return p;
            }
            for (int i = 0; i < tries; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var p = TakeRandomImageFromStock(additionallyUsed);
                if (string.IsNullOrWhiteSpace(p)) return string.Empty;
                if (!UsedPaths.Contains(p) && !additionallyUsed.Contains(p)) return p;
                await Task.Yield();
            }
            return string.Empty;
        }

        private async Task PreloadImageUrlAsync(string url, CancellationToken cancellationToken)
        {
            await JS.InvokeVoidAsync("window.app.preloadImage", cancellationToken, url);
        }

        private async Task WaitForGridReadyAsync(TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while ((Occupied is null || Cols <= 0 || Rows <= 0) && sw.Elapsed < timeout)
            {
                await Task.Delay(20);
            }
        }
    }
}
