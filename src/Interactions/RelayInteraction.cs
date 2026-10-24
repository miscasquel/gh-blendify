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
    /// Blender-style Shift+RMB relay gesture:
    ///
    ///   drag across wires  a trail and wire highlights preview the relays while dragging;
    ///                      on release one Relay is inserted per distinct source output,
    ///                      placed at the midpoint of the stroke/wire intersections for
    ///                      that source (one undo step)
    ///
    /// Relay semantics: each relay carries one source stream and reroutes that source's
    /// swept links through itself. A click with no drag does nothing.
    /// </summary>
    internal sealed class RelayInteraction : GH_AbstractInteraction
    {
        private const float StrokeTolerance = 4f;        // canvas units for sweep hits
        private const float DragThreshold = 8f;          // below this the gesture counts as a click

        private static readonly Pen TrailPen = new Pen(Color.FromArgb(230, 90, 200, 255), 1.6f);
        private static readonly Pen HitPen = new Pen(Color.FromArgb(255, 255, 200, 40), 3.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        private readonly GH_Canvas _canvas;
        private readonly GH_Document _document;
        private readonly List<PointF> _stroke = new List<PointF>();
        private List<WireInfo> _hit = new List<WireInfo>();

        public RelayInteraction(GH_Canvas canvas, GH_CanvasMouseEvent e)
            : base(canvas, e, false)
        {
            _canvas = canvas;
            _document = canvas.Document;
            _stroke.Add(e.CanvasLocation);
            _canvas.CanvasPostPaintObjects += DrawOverlay;
        }

        public override GH_ObjectResponse RespondToMouseMove(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button != MouseButtons.Right)
                return GH_ObjectResponse.Release;

            Append(e.CanvasLocation);
            RefreshHits();
            sender.Invalidate();
            return GH_ObjectResponse.Handled;
        }

        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            Append(e.CanvasLocation);
            RefreshHits();

            if (_hit.Count > 0)
            {
                GhOps.MergeWires(_document, _hit, _stroke, StrokeTolerance);
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

        private void RefreshHits()
        {
            if (Travel() <= DragThreshold)
            {
                _hit.Clear();
                return;
            }

            _hit = GhOps.FindWiresAlong(_document, _stroke, StrokeTolerance);
        }

        private void Append(PointF point)
        {
            var last = _stroke[_stroke.Count - 1];
            if (Math.Abs(point.X - last.X) + Math.Abs(point.Y - last.Y) > 1f)
                _stroke.Add(point);
        }

        private float Travel()
        {
            float travel = 0f;
            for (int i = 1; i < _stroke.Count; i++)
            {
                travel += Math.Abs(_stroke[i].X - _stroke[i - 1].X);
                travel += Math.Abs(_stroke[i].Y - _stroke[i - 1].Y);
            }
            return travel;
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

            if (_stroke.Count > 1 && Travel() > DragThreshold)
                graphics.DrawLines(TrailPen, _stroke.ToArray());
        }
    }
}
