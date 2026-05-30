using Microsoft.AspNetCore.Components;
using System.Globalization;

namespace SimpleImageSlideShow.Components.Pages
{
    public sealed partial class Tiled
    {
        private void OnClockToggleChanged(ChangeEventArgs e)
        {
            var show = ShowClock;
            if (e.Value is bool b)
            {
                show = b;
            }
            else if (e.Value is string s && bool.TryParse(s, out var parsed))
            {
                show = parsed;
            }

            if (ShowClock == show) return;
            ShowClock = show;
            RefreshClockLayout(immediate: true);
        }

        private void OnClockAvoidOverlapChanged(ChangeEventArgs e)
        {
            var avoid = AvoidClockOverlap;
            if (e.Value is bool b)
            {
                avoid = b;
            }
            else if (e.Value is string s && bool.TryParse(s, out var parsed))
            {
                avoid = parsed;
            }

            if (AvoidClockOverlap == avoid) return;
            AvoidClockOverlap = avoid;
            RefreshClockLayout(immediate: true);
        }

        private void OnClockCornerChanged(ChangeEventArgs e)
        {
            var next = NormalizeClockCorner(e.Value?.ToString());
            if (string.Equals(next, ClockCorner, StringComparison.Ordinal)) return;
            ClockCorner = next;
            RefreshClockLayout(immediate: true);
        }

        private void OnClockScaleInput(ChangeEventArgs e)
        {
            if (e.Value is string s && double.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
            {
                var nextScale = Math.Clamp(v / 100.0, 0.5, 5.0);
                if (Math.Abs(nextScale - ClockScale) < 0.0001) return;
                ClockScale = nextScale;
                RefreshClockLayout(immediate: false);
            }
        }

        private void RefreshClockLayout(bool immediate)
        {
            if (immediate)
            {
                CancelClockLayoutUpdate();
                ComputeClockReservedCells();
                UpdateClockOverlap();
                RemoveClockOverlaps();
                InvalidatePlan();
                StateHasChanged();
            }
            else
            {
                StateHasChanged();
                ScheduleClockLayoutUpdate();
            }
        }

        private void ScheduleClockLayoutUpdate()
        {
            var previous = _clockLayoutUpdateCts;
            var nextCts = new CancellationTokenSource();
            _clockLayoutUpdateCts = nextCts;

            if (previous is not null)
            {
                try { previous.Cancel(); } catch { }
                previous.Dispose();
            }

            _ = DebouncedClockLayoutUpdateAsync(nextCts);
        }

        private async Task DebouncedClockLayoutUpdateAsync(CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(ClockLayoutDebounceDelay, cts.Token);
                if (cts.IsCancellationRequested) return;
                await InvokeAsync(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    ComputeClockReservedCells();
                    UpdateClockOverlap();
                    RemoveClockOverlaps();
                    InvalidatePlan();
                    StateHasChanged();
                });
            }
            catch (TaskCanceledException) { }
            catch { }
            finally
            {
                if (_clockLayoutUpdateCts == cts)
                {
                    _clockLayoutUpdateCts = null;
                }
                cts.Dispose();
            }
        }

        private void CancelClockLayoutUpdate()
        {
            try
            {
                _clockLayoutUpdateCts?.Cancel();
                _clockLayoutUpdateCts?.Dispose();
            }
            catch { }
            finally
            {
                _clockLayoutUpdateCts = null;
            }
        }

        private void UpdateClockText()
        {
            var now = DateTime.Now;
            ClockTime = now.ToString("HH:mm");
            var ja = CultureInfo.GetCultureInfo("ja-JP");
            var dow = now.ToString("ddd", ja);
            ClockDate = $"{now:MM/dd}({dow})";
        }

        private double ClockWidth => ClockBaseWidth * ClockScale;

        private double ClockHeight => ClockBaseHeight * ClockScale;

        private void ComputeClockReservedCells()
        {
            if (Rows <= 0 || Cols <= 0 || !ShowClock)
            {
                ClockCells = null;
                ClockOverlapped = false;
                return;
            }
            ClockCells = new bool[Rows, Cols];

            var normalizedCorner = NormalizeClockCorner(ClockCorner);

            // Clock rectangle in viewport px
            double width = Math.Min(ClockWidth, Math.Max(0, ViewportW));
            double height = Math.Min(ClockHeight, Math.Max(0, ViewportH));
            double cx1 = normalizedCorner switch
            {
                ClockCornerTopLeft or ClockCornerBottomLeft => ClockMarginHorizontal,
                ClockCornerTopRight or ClockCornerBottomRight => Math.Max(ClockMarginHorizontal, ViewportW - ClockMarginHorizontal - width),
                ClockCornerTopCenter or ClockCornerBottomCenter or ClockCornerCenter => Math.Max(ClockMarginHorizontal, (ViewportW - width) / 2.0),
                _ => ClockMarginHorizontal
            };
            double cy1 = normalizedCorner switch
            {
                ClockCornerTopLeft or ClockCornerTopRight or ClockCornerTopCenter => ClockMarginVertical,
                ClockCornerBottomLeft or ClockCornerBottomRight or ClockCornerBottomCenter => Math.Max(ClockMarginVertical, ViewportH - ClockMarginVertical - height),
                ClockCornerCenter => Math.Max(ClockMarginVertical, (ViewportH - height) / 2.0),
                _ => ClockMarginVertical
            };
            cx1 = Math.Clamp(cx1, 0, Math.Max(0, ViewportW - width));
            cy1 = Math.Clamp(cy1, 0, Math.Max(0, ViewportH - height));
            double cx2 = cx1 + width;
            double cy2 = cy1 + height;

            // Grid rectangle
            double gx1 = OffsetX;
            double gy1 = OffsetY;
            double gx2 = OffsetX + GridW;
            double gy2 = OffsetY + GridH;

            // Intersection
            double ix1 = Math.Max(cx1, gx1);
            double iy1 = Math.Max(cy1, gy1);
            double ix2 = Math.Min(cx2, gx2);
            double iy2 = Math.Min(cy2, gy2);
            if (ix2 <= ix1 || iy2 <= iy1) return; // no overlap

            int cStart = Math.Clamp((int)Math.Floor((ix1 - gx1) / Math.Max(1.0, TileW)), 0, Cols);
            int cEndEx = Math.Clamp((int)Math.Ceiling((ix2 - gx1) / Math.Max(1.0, TileW)), 0, Cols);
            int rStart = Math.Clamp((int)Math.Floor((iy1 - gy1) / Math.Max(1.0, TileH)), 0, Rows);
            int rEndEx = Math.Clamp((int)Math.Ceiling((iy2 - gy1) / Math.Max(1.0, TileH)), 0, Rows);

            for (int r = rStart; r < rEndEx; r++)
                for (int c = cStart; c < cEndEx; c++)
                    ClockCells[r, c] = true;
        }

        private bool IsClockCell(int r, int c)
            => ShowClock && ClockCells is not null && r >= 0 && r < Rows && c >= 0 && c < Cols && ClockCells[r, c];

        private bool IsOverlappingClock(int row, int col, int rowSpan, int colSpan)
        {
            if (!ShowClock || ClockCells is null) return false;
            for (int r = row; r < row + rowSpan; r++)
                for (int c = col; c < col + colSpan; c++)
                    if (IsClockCell(r, c)) return true;
            return false;
        }

        private void UpdateClockOverlap()
        {
            if (!ShowClock || Occupied is null || ClockCells is null) { ClockOverlapped = false; return; }
            bool any = false;
            for (int r = 0; r < Rows && !any; r++)
                for (int c = 0; c < Cols && !any; c++)
                    if (ClockCells[r, c] && Occupied[r, c]) any = true;
            ClockOverlapped = any;
        }

        private static string NormalizeClockCorner(string? corner)
        {
            if (string.IsNullOrWhiteSpace(corner)) return ClockCornerBottomLeft;
            if (corner.Equals(ClockCornerTopLeft, StringComparison.OrdinalIgnoreCase)) return ClockCornerTopLeft;
            if (corner.Equals(ClockCornerTopRight, StringComparison.OrdinalIgnoreCase)) return ClockCornerTopRight;
            if (corner.Equals(ClockCornerTopCenter, StringComparison.OrdinalIgnoreCase)) return ClockCornerTopCenter;
            if (corner.Equals(ClockCornerBottomRight, StringComparison.OrdinalIgnoreCase)) return ClockCornerBottomRight;
            if (corner.Equals(ClockCornerBottomCenter, StringComparison.OrdinalIgnoreCase)) return ClockCornerBottomCenter;
            if (corner.Equals(ClockCornerCenter, StringComparison.OrdinalIgnoreCase)) return ClockCornerCenter;
            return ClockCornerBottomLeft;
        }

        private string ClockCornerCssClass => NormalizeClockCorner(ClockCorner) switch
        {
            ClockCornerTopLeft => "top-left",
            ClockCornerTopRight => "top-right",
            ClockCornerTopCenter => "top-center",
            ClockCornerBottomRight => "bottom-right",
            ClockCornerBottomCenter => "bottom-center",
            ClockCornerCenter => "center",
            _ => "bottom-left"
        };
    }
}
