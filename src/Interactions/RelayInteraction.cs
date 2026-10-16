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
using Grasshopper.Kernel.Undo;

namespace Blendify
{
    /// <summary>
    /// Blender-style Shift+RMB reroute insertion: drag the right mouse button across wires
    /// and every wire the stroke touches gets a Relay inserted (a trail is drawn while
    /// sweeping). A Shift+RMB click inserts a single relay on the nearest wire.
    /// All insertions of one gesture share a single undo step.
    /// </summary>
    internal sealed class RelayInteraction : GH_AbstractInteraction
    {
        private const float StrokeTolerance = 4f;        // canvas units for sweep hits
        private const float ClickTravelLimit = 8f;       // below this the gesture counts as a click

        private static readonly Pen TrailPen = new Pen(Color.FromArgb(230, 90, 200, 255), 1.6f);

        private readonly GH_Canvas _canvas;
        private readonly GH_Document _document;
        private readonly List<PointF> _stroke = new List<PointF>();
        private readonly HashSet<IGH_Param> _relays = new HashSet<IGH_Param>();
        private readonly GH_UndoRecord _undo = new GH_UndoRecord("Blendify: Insert Relay");

        private bool _committed;

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
            Update(e.CanvasLocation);
            sender.Invalidate();
            return GH_ObjectResponse.Handled;
        }

        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            Append(e.CanvasLocation);
            Update(e.CanvasLocation);
            CommitUndo();
            sender.Invalidate();
            return GH_ObjectResponse.Release;
        }

        public override void Destroy()
        {
            CommitUndo();
            _canvas.CanvasPostPaintObjects -= DrawOverlay;
            base.Destroy();
        }

        private void CommitUndo()
        {
            if (_committed || _undo.ActionCount == 0)
                return;

            _committed = true;
            _document.UndoUtil.RecordEvent(_undo);
        }

        private void Update(PointF cursor)
        {
            bool click = Travel() <= ClickTravelLimit;
            var wires = GhOps.FindWiresAlong(_document, _stroke, click ? GhOps.WireHitRadius : StrokeTolerance);
            if (wires.Count == 0)
                return;

            if (click)
            {
                TryInsert(NearestWire(wires, cursor), cursor);
                return;
            }

            foreach (var wire in wires)
                TryInsert(wire, cursor);
        }

        private void TryInsert(WireInfo wire, PointF cursor)
        {
            // After insertion a relay splits the wire into (source -> relay) and
            // (relay -> target); skipping anything connected to a relay of this gesture
            // prevents chains, while untouched wires keep getting their own relay.
            if (_relays.Contains(wire.Source) || _relays.Contains(wire.Target))
                return;

            var location = GhOps.NearestPointOnWire(wire.Source, wire.Target, cursor);
            var relay = GhOps.InsertRelay(_document, wire.Source, wire.Target, location, _undo);
            _relays.Add(relay);
        }

        private static WireInfo NearestWire(List<WireInfo> wires, PointF cursor)
        {
            var best = wires[0];
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
            if (graphics == null || _stroke.Count < 2)
                return;

            graphics.DrawLines(TrailPen, _stroke.ToArray());
        }
    }
}
