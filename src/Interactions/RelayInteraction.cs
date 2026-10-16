// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 miscasquel

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Canvas.Interaction;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Blendify
{
    /// <summary>
    /// Blender-style Shift+RMB reroute insertion: sweep the right mouse button across a
    /// wire and a Relay is inserted where the stroke crosses it, then follows the drag
    /// along that wire. A plain Shift+RMB click on a wire inserts a relay right there.
    /// </summary>
    internal sealed class RelayInteraction : GH_AbstractInteraction
    {
        private const float ClickTolerance = 12f;   // canvas units, same as GhOps.WireHitRadius
        private const float StrokeTolerance = 4f;
        private const float ClickTravelLimit = 8f;

        private readonly GH_Document _document;
        private readonly List<PointF> _stroke = new List<PointF>();

        private GH_Relay _relay;
        private IGH_Param _relaySource;
        private IGH_Param _relayTarget;

        public RelayInteraction(GH_Canvas canvas, GH_CanvasMouseEvent e)
            : base(canvas, e, false)
        {
            _document = canvas.Document;
            _stroke.Add(e.CanvasLocation);
        }

        public override GH_ObjectResponse RespondToMouseMove(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button != MouseButtons.Right)
                return GH_ObjectResponse.Release;

            Append(e.CanvasLocation);
            Update(e.CanvasLocation);
            sender.Invalidate();
            return GH_ObjectResponse.Handled;
        }

        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (_relay == null)
            {
                Append(e.CanvasLocation);
                Update(e.CanvasLocation);
                sender.Invalidate();
            }
            return GH_ObjectResponse.Release;
        }

        private void Append(PointF point)
        {
            var last = _stroke[_stroke.Count - 1];
            if (Math.Abs(point.X - last.X) + Math.Abs(point.Y - last.Y) > 1f)
                _stroke.Add(point);
        }

        private void Update(PointF cursor)
        {
            if (_relay == null)
            {
                var wire = FindWireUnderStroke(cursor);
                if (wire == null)
                    return;

                // Insert the relay where the stroke meets the wire.
                _relaySource = wire.Value.Source;
                _relayTarget = wire.Value.Target;
                _relay = GhOps.InsertRelay(_document, _relaySource, _relayTarget,
                    GhOps.NearestPointOnWire(_relaySource, _relayTarget, cursor));
                return;
            }

            // Once inserted, keep the relay sliding along the original wire.
            MoveRelay(GhOps.NearestPointOnWire(_relaySource, _relayTarget, cursor));
        }

        private WireInfo? FindWireUnderStroke(PointF cursor)
        {
            // A click (barely any travel) gets the generous wire hit radius; a sweep only
            // claims wires it actually touches.
            float tolerance = Travel() <= ClickTravelLimit ? ClickTolerance : StrokeTolerance;
            var wires = GhOps.FindWiresAlong(_document, _stroke, tolerance);
            if (wires.Count == 0)
                return null;

            WireInfo best = wires[0];
            float bestDistance = float.MaxValue;
            foreach (var wire in wires)
            {
                var point = GhOps.NearestPointOnWire(wire.Source, wire.Target, cursor);
                float distance = GhOps.Distance(point, cursor);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = wire;
                }
            }
            return best;
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

        private void MoveRelay(PointF location)
        {
            _relay.Attributes.Pivot = location;
            _relay.Attributes.ExpireLayout();
        }
    }
}
