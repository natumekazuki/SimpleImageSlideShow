
using Microsoft.JSInterop;
using SimpleImageSlideShow.Models;

namespace SimpleImageSlideShow.Components.Pages
{
    public sealed partial class Tiled
    {
        private async Task StartAsync()
        {
            await StopAsync();
            _cts = new CancellationTokenSource();
            _lastTickItem = Items.LastOrDefault();
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            var shouldWait = _lastTickItem is not null;
            while (!token.IsCancellationRequested)
            {
                var waitTarget = _lastTickItem;
                if (waitTarget is not null || shouldWait)
                {
                    try
                    {
                        await WaitForNextTickAsync(waitTarget, token);
                    }
                    catch (OperationCanceledException)
                    {
                        if (token.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }
                shouldWait = true;

                TiledItem? newItem = null;
                try
                {
                    await InvokeAsync(async () =>
                    {
                        token.ThrowIfCancellationRequested();
                        var item = await ApplyPlannedOrStepAsync(token);
                        token.ThrowIfCancellationRequested();
                        StateHasChanged();
                        newItem = item;
                    });
                }
                catch (OperationCanceledException) { break; }
                catch { }

                if (token.IsCancellationRequested) break;
                _lastTickItem = newItem ?? Items.LastOrDefault();
            }
        }

        private async Task StopAsync()
        {
            try
            {
                _cts?.Cancel();
                CancelCurrentDelay();
                if (_loopTask is not null)
                    await Task.WhenAny(_loopTask, Task.Delay(500));
            }
            catch { }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _loopTask = null;
            }
        }

        private async Task<TiledItem?> StepAsync()
            => Occupied is null ? null : await ApplyPlannedOrStepAsync();

        private async Task<TiledItem?> AddOneAsync()
        {
            var imagePath = await GetRandomUnusedPathAsync();
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            var size = await ImageService.GetImageSizeAsync(imagePath);
            if (size is null) return null;
            var (origW, origH) = size.Value;

            // 画面長辺比ベースでサイズを決定（アップスケール禁止）。
            var (lo, hi) = GetCurrentScaleRange(origW, origH);
            var rand = lo < hi ? lo + Random.Shared.NextDouble() * (hi - lo) : lo;
            if (!TryPlaceLongEdgeBasedNoUpscale(origW, origH, imagePath, lo, hi, rand, out var item, avoidClock: ShowClock && AvoidClockOverlap))
            {
                return null;
            }

            FillCells(item.Row, item.Col, item.RowSpan, item.ColSpan, true);
            SetOwners(item, true);
            Items.Add(item);
            UsedPaths.Add(imagePath);
            AddCooldown(imagePath);
            return item;
        }

        // Insert at initially chosen scale, removing oldest tiles (FIFO) until placement is possible.
        private async Task<TiledItem?> AddWithFifoRemovalAsync(CancellationToken cancellationToken)
        {
            // pick a candidate image
            const int imageTries = 40;
            for (int t = 0; t < imageTries; t++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var imagePath = await GetRandomUnusedPathAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(imagePath))
                {
                    imagePath = TakeRandomImageFromStock(allowUsedPaths: true);
                }
                if (string.IsNullOrWhiteSpace(imagePath)) return null;
                var size = await ImageService.GetImageSizeAsync(imagePath);
                cancellationToken.ThrowIfCancellationRequested();
                if (size is null) continue;
                var (origW, origH) = size.Value;
                var onScreenIndex = Items.FindIndex(item => string.Equals(item.Path, imagePath, StringComparison.OrdinalIgnoreCase));
                var isAlreadyOnScreen = onScreenIndex >= 0;

                // 長辺比ベースの初期候補（アップスケール禁止）
                var (lo, hi) = GetCurrentScaleRange(origW, origH);
                var rand = lo < hi ? lo + Random.Shared.NextDouble() * (hi - lo) : lo;
                var (sw, sh) = ComputeViewportLongEdgeTargetNoUpscale(origW, origH, rand, clampToGrid: true);
                int reqCols = Math.Max(1, (int)Math.Ceiling(sw / TileW));
                int reqRows = Math.Max(1, (int)Math.Ceiling(sh / TileH));

                // try without removal first (avoid clock area) with multiple random scales
                // attempt initial chosen scale first
                var avoidClock = ShowClock && AvoidClockOverlap;
                if (!isAlreadyOnScreen && TryPlace(reqRows, reqCols, out var r0, out var c0, avoidClock: avoidClock))
                {
                    var src0 = BuildVirtualHostUrl(imagePath);
                    var item0 = CreateTiledItem(imagePath, r0, c0, reqRows, reqCols, rand, sw, sh, src0);
                    FillCells(item0.Row, item0.Col, item0.RowSpan, item0.ColSpan, true);
                    SetOwners(item0, true);
                    Items.Add(item0);
                    UsedPaths.Add(imagePath);
                    AddCooldown(imagePath);
                    return item0;
                }

                if (!isAlreadyOnScreen && TryComputeRandomDefragForPlacement(reqRows, reqCols, out var defragRow, out var defragCol, out var moves, avoidClock: avoidClock))
                {
                    var srcDefrag = BuildVirtualHostUrl(imagePath);
                    var itemDefrag = CreateTiledItem(imagePath, defragRow, defragCol, reqRows, reqCols, rand, sw, sh, srcDefrag);
                    ApplyDefragPlacement(itemDefrag, moves);
                    return itemDefrag;
                }

                // then try a few random scales
                if (!isAlreadyOnScreen)
                {
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
                        if (reqColsD <= Cols && reqRowsD <= Rows && TryPlace(reqRowsD, reqColsD, out var rD, out var cD, avoidClock: avoidClock))
                        {
                            var srcD = BuildVirtualHostUrl(imagePath);
                            var itemD = CreateTiledItem(imagePath, rD, cD, reqRowsD, reqColsD, rtry, swD, shD, srcD);
                            FillCells(itemD.Row, itemD.Col, itemD.RowSpan, itemD.ColSpan, true);
                            SetOwners(itemD, true);
                            Items.Add(itemD);
                            UsedPaths.Add(imagePath);
                            AddCooldown(imagePath);
                            return itemD;
                        }
                    }
                }

                var placementPlan = new PlannedStep
                {
                    Path = imagePath,
                    Row = -1,
                    Col = -1,
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
                var item = await ApplyPlacementPlanAsync(placementPlan, cancellationToken);
                if (item is not null) return item;
            }
            return null;
        }

        private async Task<string> GetRandomUnusedPathAsync(CancellationToken cancellationToken = default)
        {
            int tries = GetImageTryCount();
            CleanupCooldown();
            var now = DateTime.UtcNow;
            for (int i = 0; i < tries; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var p = TakeRandomImageFromStock();
                if (string.IsNullOrWhiteSpace(p)) return string.Empty;
                if (UsedPaths.Contains(p)) { await Task.Yield(); continue; }
                if (_cooldown.TryGetValue(p, out var until) && until > now) { await Task.Yield(); continue; }
                return p;
            }
            // Fallback: ignore TTL but keep the cycle and on-screen duplicate constraints.
            for (int i = 0; i < tries; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var p = TakeRandomImageFromStock();
                if (string.IsNullOrWhiteSpace(p)) return string.Empty;
                if (!UsedPaths.Contains(p)) return p;
                await Task.Yield();
            }
            return string.Empty;
        }

        private int GetImageTryCount()
        {
            var occ = OccupancyPercent();
            // fewer tries when occupancy is high (we'll clear anyway), more when space is plenty
            if (occ < 40) return 20;
            if (occ < 70) return 16;
            if (occ < 90) return 12;
            return 8;
        }

        private void AddCooldown(string path)
        {
            try
            {
                var until = DateTime.UtcNow.AddSeconds(Math.Max(1, ReuseTtlSeconds));
                _cooldown[path] = until;
                _cooldownQueue.Enqueue(path, until.Ticks);
            }
            catch { }
        }

        private void CleanupCooldown()
        {
            try
            {
                var nowTicks = DateTime.UtcNow.Ticks;
                while (_cooldownQueue.TryPeek(out var path, out var ticks) && ticks <= nowTicks)
                {
                    _cooldownQueue.Dequeue();
                    if (_cooldown.TryGetValue(path, out var dt) && dt.Ticks <= nowTicks)
                    {
                        _cooldown.Remove(path);
                    }
                }
            }
            catch { }
        }

        private void ResetImageState()
        {
            Items.Clear();
            UsedPaths.Clear();
            InvalidatePlan();
            _lastTickItem = null;
            ResetUnusedImageStock();

            _cooldown.Clear();
            while (_cooldownQueue.TryDequeue(out _, out _)) { }

            Occupied = null;
            Owners = null;
        }

        private void SetImageStock(IEnumerable<string> imagePaths)
        {
            ImageStock = imagePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            ResetUnusedImageStock();
        }

        private void ResetUnusedImageStock()
        {
            UnusedImageStock.Clear();
            UnusedImageStock.AddRange(ImageStock);
        }

        private string TakeRandomImageFromStock(ISet<string>? additionallyUsed = null, bool allowUsedPaths = false)
        {
            if (ImageStock.Count == 0)
            {
                return string.Empty;
            }

            if (UnusedImageStock.Count == 0)
            {
                ResetUnusedImageStock();
            }

            while (UnusedImageStock.Count > 0)
            {
                var index = Random.Shared.Next(UnusedImageStock.Count);
                var path = UnusedImageStock[index];
                UnusedImageStock.RemoveAt(index);

                if ((!allowUsedPaths && UsedPaths.Contains(path)) || additionallyUsed?.Contains(path) == true)
                {
                    continue;
                }

                return path;
            }

            return string.Empty;
        }

        private async Task WaitForNextTickAsync(TiledItem? lastItem, CancellationToken token)
        {
            var delaySeconds = DelayRange.Normalize(MinDelaySeconds, MaxDelaySeconds).NextDelaySeconds();
            using var delaySkipCts = new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, delaySkipCts.Token);

            lock (_delaySkipLock)
            {
                _delaySkipCts = delaySkipCts;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), linkedCts.Token);
            }
            finally
            {
                lock (_delaySkipLock)
                {
                    if (ReferenceEquals(_delaySkipCts, delaySkipCts))
                    {
                        _delaySkipCts = null;
                    }
                }
            }
        }

        [JSInvokable]
        public Task SkipCurrentDelayAsync()
        {
            CancelCurrentDelay();
            return Task.CompletedTask;
        }

        private void CancelCurrentDelay()
        {
            CancellationTokenSource? cts;
            lock (_delaySkipLock)
            {
                cts = _delaySkipCts;
            }

            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

    }
}
