// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 miscasquel

using System;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Widgets;
using Grasshopper.Kernel;

namespace Blendify
{
    /// <summary>
    /// Replaces Grasshopper's profiler speech balloons with a compact solve-time label
    /// above every component.
    ///
    /// The Profiler widget remains the on/off switch. While it is visible, its native
    /// balloon tags are suppressed for the duration of each widget paint and restored
    /// immediately afterwards, so no profiler setting is left modified. Timings come
    /// from <see cref="IGH_ActiveObject.ProcessorTime"/>, the same value the native
    /// profiler displays.
    /// </summary>
    internal sealed class ProfilerOverlay
    {
        // GH clamps the profiler threshold to this value. Anything slower than ten
        // seconds is rare enough to let a native tag through.
        private const int HiddenThreshold = 10000;

        // Colour-code boundaries, in milliseconds.
        private const double FastMilliseconds = 5.0;
        private const double SlowMilliseconds = 50.0;

        // Skip labels on components that would be too narrow to read at this zoom.
        private const float MinimumComponentWidth = 24f;

        private const float LabelGap = 2f;

        // Extra horizontal room so a label wider than its component can still be
        // centred without being clipped.
        private const float LabelOverhang = 24f;

        private const int LabelAlpha = 235;

        private static readonly SolidBrush LightZeroBrush = Brush(107, 114, 128);
        private static readonly SolidBrush LightFastBrush = Brush(21, 128, 61);
        private static readonly SolidBrush LightMediumBrush = Brush(180, 83, 9);
        private static readonly SolidBrush LightSlowBrush = Brush(185, 28, 28);

        private static readonly SolidBrush DarkZeroBrush = Brush(156, 163, 175);
        private static readonly SolidBrush DarkFastBrush = Brush(74, 222, 128);
        private static readonly SolidBrush DarkMediumBrush = Brush(251, 191, 36);
        private static readonly SolidBrush DarkSlowBrush = Brush(248, 113, 113);

        private static readonly StringFormat LabelFormat = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };

        private static bool _installed;

        // Threshold suppression is global because GH exposes the profiler threshold
        // as a static setting. The depth counter keeps nested paints balanced.
        private static int _suppressionDepth;
        private static int _savedThreshold;
        private static bool _hasSavedThreshold;

        public ProfilerOverlay(GH_Canvas canvas)
        {
            canvas.CanvasPostPaintOverlay += DrawLabels;
            canvas.CanvasPaintBegin += ResetSuppression;
            canvas.CanvasPrePaintWidgets += SuppressNativeTags;
            canvas.CanvasPostPaintWidgets += RestoreNativeTags;
            canvas.CanvasPaintEnd += RestoreAfterInterruption;
        }

        /// <summary>
        /// Subscribes once to the profiler widget's visibility changes so toggling the
        /// profiler immediately repaints the canvas and shows or hides our labels.
        /// </summary>
        public static void Install()
        {
            if (_installed)
                return;

            _installed = true;
            GH_ProfilerWidget.WidgetVisibleChanged += OnProfilerVisibilityChanged;
        }

        private static void OnProfilerVisibilityChanged()
        {
            CancelSuppression();
            Instances.ActiveCanvas?.Invalidate();
        }

        private void DrawLabels(GH_Canvas canvas)
        {
            if (!GH_ProfilerWidget.SharedVisible)
                return;

            // Labels are a canvas-only decoration; keep thumbnails and exported images clean.
            if (canvas.DrawingMode != GH_CanvasMode.Control)
                return;

            var document = canvas.Document;
            var graphics = canvas.Graphics;
            if (document == null || graphics == null)
                return;

            var state = graphics.Save();
            try
            {
                // Draw in control space so the label keeps a constant on-screen size
                // regardless of canvas zoom.
                graphics.ResetTransform();
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                var darkCanvas = IsDarkCanvas();
                var font = GH_FontServer.Small;
                var fontHeight = font.Height;

                foreach (var obj in document.Objects)
                {
                    if (!(obj is IGH_Component component))
                        continue;

                    var attributes = component.Attributes;
                    if (attributes == null)
                        continue;

                    var bounds = attributes.Bounds;
                    if (bounds.Width <= 0f || bounds.Height <= 0f)
                        continue;

                    var projected = canvas.Viewport.ProjectRectangle(bounds);
                    if (projected.Width < MinimumComponentWidth)
                        continue;
                    if (!projected.IntersectsWith(canvas.ClientRectangle))
                        continue;

                    double milliseconds = component.ProcessorTime.TotalMilliseconds;
                    if (double.IsNaN(milliseconds) || milliseconds < 0.0)
                        milliseconds = 0.0;

                    var label = FormatMilliseconds(milliseconds);
                    var brush = SelectBrush(milliseconds, darkCanvas);
                    var labelBounds = new RectangleF(
                        projected.X - LabelOverhang,
                        projected.Top - fontHeight - LabelGap,
                        projected.Width + 2f * LabelOverhang,
                        fontHeight + LabelGap);

                    graphics.DrawString(label, font, brush, labelBounds, LabelFormat);
                }
            }
            finally
            {
                graphics.Restore(state);
            }
        }

        private static string FormatMilliseconds(double milliseconds)
        {
            if (milliseconds < 0.05)
                return "0 ms";
            if (milliseconds < 10.0)
                return milliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms";
            return Math.Round(milliseconds, MidpointRounding.AwayFromZero)
                .ToString("0", CultureInfo.InvariantCulture) + " ms";
        }

        private static SolidBrush SelectBrush(double milliseconds, bool darkCanvas)
        {
            if (milliseconds < 0.05)
                return darkCanvas ? DarkZeroBrush : LightZeroBrush;
            if (milliseconds < FastMilliseconds)
                return darkCanvas ? DarkFastBrush : LightFastBrush;
            if (milliseconds < SlowMilliseconds)
                return darkCanvas ? DarkMediumBrush : LightMediumBrush;
            return darkCanvas ? DarkSlowBrush : LightSlowBrush;
        }

        private static bool IsDarkCanvas()
        {
            var background = GH_Skin.canvas_mono ? GH_Skin.canvas_mono_color : GH_Skin.canvas_back;
            int luminance = (background.R * 299 + background.G * 587 + background.B * 114) / 1000;
            return luminance < 128;
        }

        private static SolidBrush Brush(int red, int green, int blue)
            => new SolidBrush(Color.FromArgb(LabelAlpha, red, green, blue));

        private static void ResetSuppression(GH_Canvas canvas)
        {
            // Recover from an interrupted previous paint before starting a new one.
            CancelSuppression();
        }

        private static void SuppressNativeTags(GH_Canvas canvas)
        {
            if (!GH_ProfilerWidget.SharedVisible)
            {
                CancelSuppression();
                return;
            }

            if (_suppressionDepth == 0)
            {
                _savedThreshold = GH_ProfilerWidget.Threshold;
                _hasSavedThreshold = _savedThreshold != HiddenThreshold;
                if (_hasSavedThreshold)
                    GH_ProfilerWidget.Threshold = HiddenThreshold;
            }

            _suppressionDepth++;
        }

        private static void RestoreNativeTags(GH_Canvas canvas)
        {
            EndSuppression();
        }

        private static void RestoreAfterInterruption(GH_Canvas canvas)
        {
            if (_suppressionDepth > 0)
                CancelSuppression();
        }

        private static void EndSuppression()
        {
            if (_suppressionDepth <= 0)
            {
                _suppressionDepth = 0;
                return;
            }

            _suppressionDepth--;
            if (_suppressionDepth != 0)
                return;

            if (_hasSavedThreshold)
                GH_ProfilerWidget.Threshold = _savedThreshold;
            _hasSavedThreshold = false;
        }

        private static void CancelSuppression()
        {
            _suppressionDepth = 0;
            if (_hasSavedThreshold)
                GH_ProfilerWidget.Threshold = _savedThreshold;
            _hasSavedThreshold = false;
        }
    }
}
