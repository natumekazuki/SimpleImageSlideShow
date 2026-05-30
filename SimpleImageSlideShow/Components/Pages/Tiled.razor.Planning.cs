using Microsoft.JSInterop;

namespace SimpleImageSlideShow.Components.Pages
{
    public sealed partial class Tiled
    {
        private void InvalidatePlan() => _planQueue.Clear();

        private async Task<TiledItem?> ApplyPlannedOrStepAsync()
        {
            if (_planQueue.Count == 0)
            {
                return await StepAsync();
            }

            var plan = _planQueue[0];
            _planQueue.RemoveAt(0);

            // Apply removals with fade-out animation
            int toRemove = Math.Min(plan.RemoveCount, Items.Count);
            if (toRemove > 0)
            {
                for (int i = 0; i < toRemove; i++) Items[i].Removing = true;
                StateHasChanged();
                await Task.Delay(300);
                for (int i = 0; i < toRemove; i++)
                {
                    var it = Items[0];
                    FillCells(it.Row, it.Col, it.RowSpan, it.ColSpan, false);
                    SetOwners(it, false);
                    Items.RemoveAt(0);
                    UsedPaths.Remove(it.Path);
                }
            }

            var item = CreateTiledItem(plan.Path, plan.Row, plan.Col, plan.RowSpan, plan.ColSpan, plan.Scale, plan.ImgWidth, plan.ImgHeight, plan.Src, plan.AudioSrc);
            FillCells(item.Row, item.Col, item.RowSpan, item.ColSpan, true);
            SetOwners(item, true);
            Items.Add(item);
            UsedPaths.Add(plan.Path);
            AddCooldown(plan.Path);

            try { await EnsurePlanAsync(); } catch { }
            return item;
        }

        private record SimItem(string Path, int Row, int Col, int RowSpan, int ColSpan);

        private async Task EnsurePlanAsync()
        {
            if (Occupied is null) { _planQueue.Clear(); return; }
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
                FillCellsSim(ps.Row, ps.Col, ps.RowSpan, ps.ColSpan, occSim, true);
                simItems.Add(new SimItem(ps.Path, ps.Row, ps.Col, ps.RowSpan, ps.ColSpan));
                plannedPaths.Add(ps.Path);
            }

            // Build a used-set for planning (current + screen + planned)
            var usedForPlan = new HashSet<string>(UsedPaths, StringComparer.OrdinalIgnoreCase);
            foreach (var it in simItems) usedForPlan.Add(it.Path);
            foreach (var p in plannedPaths) usedForPlan.Add(p);

            for (int n = 0; n < need; n++)
            {
                var plan = await ComputeOnePlanAsync(occSim, simItems, usedForPlan);
                if (plan is null) break;
                _planQueue.Add(plan);
                // Apply to sim
                int toRemove = Math.Min(plan.RemoveCount, simItems.Count);
                for (int i = 0; i < toRemove; i++)
                {
                    var it = simItems[0];
                    FillCellsSim(it.Row, it.Col, it.RowSpan, it.ColSpan, occSim, false);
                    simItems.RemoveAt(0);
                }
                FillCellsSim(plan.Row, plan.Col, plan.RowSpan, plan.ColSpan, occSim, true);
                simItems.Add(new SimItem(plan.Path, plan.Row, plan.Col, plan.RowSpan, plan.ColSpan));
                usedForPlan.Add(plan.Path);
                try { await PreloadImageUrlAsync(plan.Src); } catch { }
            }
        }

        private async Task<PlannedStep?> ComputeOnePlanAsync(bool[,] occSim, List<SimItem> simItems, HashSet<string> usedForPlan)
        {
            const int imageTries = 40;
            for (int t = 0; t < imageTries; t++)
            {
                var imagePath = await GetRandomUnusedPathForPlanAsync(usedForPlan);
                if (string.IsNullOrWhiteSpace(imagePath)) return null;
                var size = await ImageService.GetImageSizeAsync(imagePath);
                if (size is null) continue;
                var (origW, origH) = size.Value;

                var hi = Math.Max(MinScale, Math.Min(1.0, MaxScale));
                var lo = Math.Min(MinScale, hi);
                var vLong2 = Math.Max(Math.Max(1.0, ViewportW), Math.Max(1.0, ViewportH));
                var iLong2 = Math.Max(origW, origH);
                var rImg2 = vLong2 > 0 ? (iLong2 / vLong2) : MinScale;
                if (rImg2 <= ShrinkGuardThreshold) lo = Math.Max(lo, rImg2);
                var rand = lo < hi ? lo + Random.Shared.NextDouble() * (hi - lo) : lo;
                var (sw, sh) = ComputeViewportLongEdgeTargetNoUpscale(origW, origH, rand, clampToGrid: true);
                int reqCols = Math.Max(1, (int)Math.Ceiling(sw / TileW));
                int reqRows = Math.Max(1, (int)Math.Ceiling(sh / TileH));
                if (reqCols > Cols || reqRows > Rows) continue;

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
                        ImgWidth = sw,
                        ImgHeight = sh,
                        Src = BuildVirtualHostUrl(imagePath),
                        AudioSrc = GetAudioUrlForImage(imagePath),
                        RemoveCount = 0
                    };
                }

                // Try additional random scales for no-removal placement
                for (int tries = 0; tries < RandomScaleTries; tries++)
                {
                    var rtry = lo < hi ? lo + Random.Shared.NextDouble() * (hi - lo) : lo;
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
                            ImgWidth = swD,
                            ImgHeight = shD,
                            Src = BuildVirtualHostUrl(imagePath),
                            AudioSrc = GetAudioUrlForImage(imagePath),
                            RemoveCount = 0
                        };
                    }
                }

                if (!TryComputeFifoRemovalForPlacementSim(reqRows, reqCols, occSim, simItems, out int removeCount, out int rr, out int cc, avoidClock: avoidClock))
                {
                    if (!TryComputeFifoRemovalForPlacementSim(reqRows, reqCols, occSim, simItems, out removeCount, out rr, out cc, avoidClock: false))
                    {
                        continue;
                    }
                }

                return new PlannedStep
                {
                    Path = imagePath,
                    Row = rr,
                    Col = cc,
                    RowSpan = reqRows,
                    ColSpan = reqCols,
                    Scale = rand,
                    ImgWidth = sw,
                    ImgHeight = sh,
                    Src = BuildVirtualHostUrl(imagePath),
                    AudioSrc = GetAudioUrlForImage(imagePath),
                    RemoveCount = Math.Max(0, removeCount)
                };
            }
            return null;
        }

        private bool TryComputeFifoRemovalForPlacementSim(int reqRows, int reqCols, bool[,] occ, List<SimItem> simItems, out int removeCount, out int row, out int col, bool avoidClock)
        {
            removeCount = 0; row = col = -1;
            int rows = occ.GetLength(0), cols = occ.GetLength(1);
            // Quick success without removals
            if (TryPlaceSim(reqRows, reqCols, occ, out row, out col, avoidClock))
            {
                removeCount = 0;
                return true;
            }
            // Work on a copy
            var occSim = new bool[rows, cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    occSim[r, c] = occ[r, c];

            for (int k = 1; k <= simItems.Count; k++)
            {
                var it = simItems[k - 1];
                FillCellsSim(it.Row, it.Col, it.RowSpan, it.ColSpan, occSim, false);
                if (TryPlaceSim(reqRows, reqCols, occSim, out row, out col, avoidClock))
                {
                    removeCount = k;
                    return true;
                }
            }
            return false;
        }

        private async Task<string> GetRandomUnusedPathForPlanAsync(HashSet<string> additionallyUsed)
        {
            int tries = GetImageTryCount();
            CleanupCooldown();
            var now = DateTime.UtcNow;
            for (int i = 0; i < tries; i++)
            {
                var p = ImageService.GetRandomImagePath();
                if (string.IsNullOrWhiteSpace(p)) return string.Empty;
                if (UsedPaths.Contains(p) || additionallyUsed.Contains(p)) { await Task.Yield(); continue; }
                if (_cooldown.TryGetValue(p, out var until) && until > now) { await Task.Yield(); continue; }
                return p;
            }
            for (int i = 0; i < tries; i++)
            {
                var p = ImageService.GetRandomImagePath();
                if (string.IsNullOrWhiteSpace(p)) return string.Empty;
                if (!UsedPaths.Contains(p) && !additionallyUsed.Contains(p)) return p;
                await Task.Yield();
            }
            return string.Empty;
        }

        private async Task PreloadImageUrlAsync(string url)
        {
            try { await JS.InvokeVoidAsync("window.app.preloadImage", url); } catch { }
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
