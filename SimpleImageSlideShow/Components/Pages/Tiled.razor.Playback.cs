
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
                        var item = await ApplyPlannedOrStepAsync();
                        StateHasChanged();
                        newItem = item;
                    });
                }
                catch (OperationCanceledException) { break; }
                catch { }

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
        {
            if (Occupied is null) return null;
            var added = await AddOneAsync();
            if (added is null)
            {
                added = await AddWithFifoRemovalAsync();
            }
            try { await EnsurePlanAsync(); } catch { }
            return added;
        }

        private async Task<TiledItem?> AddOneAsync()
        {
            var imagePath = await GetRandomUnusedPathAsync();
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            var size = await ImageService.GetImageSizeAsync(imagePath);
            if (size is null) return null;
            var (origW, origH) = size.Value;

            // 画面長辺比ベースでサイズを決定（アップスケール禁止）。
            var hi = Math.Max(MinScale, Math.Min(1.0, MaxScale));
            var lo = Math.Min(MinScale, hi);
            var vLong = Math.Max(Math.Max(1.0, ViewportW), Math.Max(1.0, ViewportH));
            var iLong = Math.Max(origW, origH);
            var rImg = vLong > 0 ? (iLong / vLong) : MinScale; // 原寸の長辺が画面長辺に占める比率
            // B: 小さい画像は原寸未満にしない → rImg が範囲内なら下限を rImg まで引き上げ
            if (rImg <= ShrinkGuardThreshold) lo = Math.Max(lo, rImg);
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
        private async Task<TiledItem?> AddWithFifoRemovalAsync()
        {
            // pick a candidate image
            const int imageTries = 40;
            for (int t = 0; t < imageTries; t++)
            {
                var imagePath = await GetRandomUnusedPathAsync();
                if (string.IsNullOrWhiteSpace(imagePath)) return null;
                var size = await ImageService.GetImageSizeAsync(imagePath);
                if (size is null) continue;
                var (origW, origH) = size.Value;

                // 長辺比ベースの初期候補（アップスケール禁止）
                var hi = Math.Max(MinScale, Math.Min(1.0, MaxScale));
                var lo = Math.Min(MinScale, hi);
                var vLong = Math.Max(Math.Max(1.0, ViewportW), Math.Max(1.0, ViewportH));
                var iLong = Math.Max(origW, origH);
                var rImg = vLong > 0 ? (iLong / vLong) : MinScale;
                if (rImg <= ShrinkGuardThreshold) lo = Math.Max(lo, rImg);
                var rand = lo < hi ? lo + Random.Shared.NextDouble() * (hi - lo) : lo;
                var (sw, sh) = ComputeViewportLongEdgeTargetNoUpscale(origW, origH, rand, clampToGrid: true);
                int reqCols = Math.Max(1, (int)Math.Ceiling(sw / TileW));
                int reqRows = Math.Max(1, (int)Math.Ceiling(sh / TileH));

                // if cannot fit even on an empty grid, skip this image
                if (reqCols > Cols || reqRows > Rows) continue;

                // try without removal first (avoid clock area) with multiple random scales
                // attempt initial chosen scale first
                var avoidClock = ShowClock && AvoidClockOverlap;
                if (TryPlace(reqRows, reqCols, out var r0, out var c0, avoidClock: avoidClock))
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

                // then try a few random scales
                for (int tries = 0; tries < RandomScaleTries; tries++)
                {
                    var rtry = lo < hi ? lo + Random.Shared.NextDouble() * (hi - lo) : lo;
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

                // simulate FIFO removals on a copy of the occupancy grid to find minimal removals (avoid clock area)
                if (!TryComputeFifoRemovalForPlacement(reqRows, reqCols, out int removeCount, out int rr, out int cc, avoidClock: avoidClock))
                {
                    // try again allowing clock area as last resort
                    if (!TryComputeFifoRemovalForPlacement(reqRows, reqCols, out removeCount, out rr, out cc, avoidClock: false))
                    {
                        // couldn't compute (should be rare), try another image
                        continue;
                    }
                }

                // perform a single batch removal animation for the first removeCount items
                if (removeCount > 0)
                {
                    int toRemove = Math.Min(removeCount, Items.Count);
                    for (int i = 0; i < toRemove; i++)
                    {
                        var it = Items[i];
                        it.Removing = true;
                    }
                    StateHasChanged();
                    await Task.Delay(300);

                    for (int i = 0; i < toRemove; i++)
                    {
                        var it = Items[0]; // always oldest
                        FillCells(it.Row, it.Col, it.RowSpan, it.ColSpan, false);
                        SetOwners(it, false);
                        Items.RemoveAt(0);
                        UsedPaths.Remove(it.Path);
                    }
                }

                // place new item at the precomputed location
                var src = BuildVirtualHostUrl(imagePath);
                var item = CreateTiledItem(imagePath, rr, cc, reqRows, reqCols, rand, sw, sh, src);
                FillCells(item.Row, item.Col, item.RowSpan, item.ColSpan, true);
                SetOwners(item, true);
                Items.Add(item);
                UsedPaths.Add(imagePath);
                AddCooldown(imagePath);
                return item;
            }
            return null;
        }

        private async Task<string> GetRandomUnusedPathAsync()
        {
            int tries = GetImageTryCount();
            CleanupCooldown();
            var now = DateTime.UtcNow;
            for (int i = 0; i < tries; i++)
            {
                var p = ImageService.GetRandomImagePath();
                if (string.IsNullOrWhiteSpace(p)) return string.Empty;
                if (UsedPaths.Contains(p)) { await Task.Yield(); continue; }
                if (_cooldown.TryGetValue(p, out var until) && until > now) { await Task.Yield(); continue; }
                return p;
            }
            // Fallback: ignore TTL but avoid duplicates on screen
            for (int i = 0; i < tries; i++)
            {
                var p = ImageService.GetRandomImagePath();
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
            _planQueue.Clear();
            _lastTickItem = null;

            _cooldown.Clear();
            while (_cooldownQueue.TryDequeue(out _, out _)) { }

            Occupied = null;
            Owners = null;
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
