// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 miscasquel

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Canvas.Interaction;
using Grasshopper.Kernel;

namespace Blendify
{
    /// <summary>
    /// Blender-style Ctrl+RMB wire cutting: every wire the stroke crosses is removed in a
    /// single undo step. A plain click also cuts the wire under the cursor.
    /// </summary>
    internal sealed class WireCutInteraction : GH_AbstractInteraction
    {
        private static readonly Pen StrokePen = new Pen(Color.FromArgb(235, 255, 70, 70), 1.6f);
        private static readonly Pen HitPen = new Pen(Color.FromArgb(255, 255, 200, 40), 3.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        private readonly GH_Canvas _canvas;
        private readonly GH_Document _document;
        private readonly List<PointF> _stroke = new List<PointF>();
        private List<WireInfo> _hit = new List<WireInfo>();

        public WireCutInteraction(GH_Canvas canvas, GH_CanvasMouseEvent e)
            : base(canvas, e, false)
        {
            _canvas = canvas;
            _document = canvas.Document;
            _stroke.Add(e.CanvasLocation);
            _hit = GhOps.FindWiresAlong(_document, _stroke);
            _canvas.CanvasPostPaintObjects += DrawOverlay;
        }

        public override GH_ObjectResponse RespondToMouseMove(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button != MouseButtons.Right)
                return GH_ObjectResponse.Release;

            var last = _stroke[_stroke.Count - 1];
            if (Math.Abs(e.CanvasX - last.X) + Math.Abs(e.CanvasY - last.Y) > 1.5f)
                _stroke.Add(e.CanvasLocation);

            _hit = GhOps.FindWiresAlong(_document, _stroke);
            sender.Invalidate();
            return GH_ObjectResponse.Handled;
        }

        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            _hit = GhOps.FindWiresAlong(_document, _stroke);
            if (_hit.Count > 0)
            {
                GhOps.CutWires(_document, _hit);
                _hit.Clear();
            }
            sender.Invalidate();
            return GH_ObjectResponse.Release;
        }

        public override void Destroy()
        {
            _canvas.CanvasPostPaintObjects -= DrawOverlay;
            base.Destroy();
        }

        private void DrawOverlay(GH_Canvas sender)
        {
            var graphics = sender.Graphics;
            if (graphics == null)
                return;

            foreach (var wire in _hit)
            {
                var sourceAttributes = wire.Source.Attributes;
                var targetAttributes = wire.Target.Attributes;
                if (sourceAttributes == null || targetAttributes == null)
                    continue;
                graphics.DrawLines(HitPen, GhOps.WirePolyline(sourceAttributes.OutputGrip, targetAttributes.InputGrip));
            }

            if (_stroke.Count > 1)
                graphics.DrawLines(StrokePen, _stroke.ToArray());
        }
    }
}
