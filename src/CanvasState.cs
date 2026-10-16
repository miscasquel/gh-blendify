// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 miscasquel

using System;
using System.Drawing;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;

namespace Blendify
{
    /// <summary>
    /// Per-canvas gesture router. Implements the Blender-style input scheme:
    ///
    ///   MMB drag                      pan
    ///   Shift + RMB sweep across      insert Relays (reroutes)
    ///   wires
    ///   Ctrl + RMB drag across wires  cut them
    ///   drag from a socket, drop on   open component search and auto-connect
    ///   empty canvas                  the created component
    /// </summary>
    internal sealed class CanvasState
    {
        private const int DragSlopPixels = 4;

        private readonly GH_Canvas _canvas;

        // Left button bookkeeping for the current gesture.
        private Point _downControl;
        private IGH_Param _gripParam;
        private bool _gripFromInput;

        // Link-drag search: the socket a wire was pulled from.
        private PendingConnect _pending;

        public CanvasState(GH_Canvas canvas)
        {
            _canvas = canvas;
        }

        public void OnMouseDown(object sender, MouseEventArgs e)
        {
            var canvas = _canvas;
            var document = canvas.Document;

            _pending = null;
            _gripParam = null;

            if (document == null || !canvas.ModifiersEnabled)
                return;

            var mouseEvent = new GH_CanvasMouseEvent(canvas.Viewport, e);

            switch (e.Button)
            {
                case MouseButtons.Middle:
                    // Blender: MMB drag pans. Replaces Grasshopper's MMB radial menu.
                    canvas.ActiveInteraction = new MmbPanInteraction(canvas, mouseEvent);
                    return;

                case MouseButtons.Right:
                {
                    var modifiers = Control.ModifierKeys;
                    if ((modifiers & Keys.Shift) != Keys.None)
                    {
                        // Blender: Shift+RMB sweep across a wire inserts a Reroute (Relay).
                        canvas.ActiveInteraction = new RelayInteraction(canvas, mouseEvent);
                        return;
                    }
                    if ((modifiers & Keys.Control) != Keys.None)
                    {
                        // Blender: Ctrl+RMB drag cuts wires.
                        canvas.ActiveInteraction = new WireCutInteraction(canvas, mouseEvent);
                        return;
                    }
                    return; // Plain RMB: Grasshopper context menu.
                }

                case MouseButtons.Left:
                {
                    _downControl = e.Location;

                    var attributes = document.FindAttributeByGrip(
                        mouseEvent.CanvasLocation, true, true, true, (int)GhOps.GripHitRadius);
                    if (attributes?.DocObject is IGH_Param grip)
                    {
                        _gripParam = grip;
                        _gripFromInput = IsInputGrip(grip, attributes, mouseEvent.CanvasX);
                    }

                    return;
                }
            }
        }

        public void OnMouseUp(object sender, MouseEventArgs e)
        {
            var canvas = _canvas;
            var document = canvas.Document;
            if (document == null || e.Button != MouseButtons.Left)
                return;

            var grip = _gripParam;
            var gripFromInput = _gripFromInput;
            _gripParam = null;

            // Blender link-drag search: a wire was dragged off a socket and dropped on empty
            // canvas. Grasshopper already released its wire interaction without connecting,
            // so open the search popup and remember the socket for auto-connection.
            if (grip == null)
                return;

            var movedPixels = Math.Abs(e.X - _downControl.X) + Math.Abs(e.Y - _downControl.Y);
            if (movedPixels <= DragSlopPixels)
                return;

            var mouseEvent = new GH_CanvasMouseEvent(canvas.Viewport, e);
            if (canvas.ActiveInteraction != null || !IsEmptyCanvas(document, mouseEvent.CanvasLocation))
                return;

            var dialog = OpenSearch(canvas, canvas.PointToScreen(e.Location));
            if (dialog != null)
                _pending = new PendingConnect(grip, gripFromInput, dialog);
        }

        public void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Escape)
                return;

            _gripParam = null;
            _pending = null;
        }

        public void OnDocumentObjectsAdded(GH_Document sender, GH_DocObjectEventArgs e)
        {
            var pending = _pending;
            if (pending == null || e.ObjectCount != 1)
                return;

            IGH_DocumentObject added = null;
            foreach (var obj in e.Objects)
            {
                added = obj;
                break;
            }
            if (added == null)
                return;

            _pending = null;
            GhOps.ConnectToNewObject(sender, added, pending.Param, pending.FromInput);
        }

        /// <summary>Opens Grasshopper's native component search popup, centred on a screen point.</summary>
        private GH_PopupSearchDialog OpenSearch(GH_Canvas canvas, Point screenPoint)
        {
            if (!canvas.ModifiersEnabled)
                return null;

            var validator = canvas.Validator;
            if (validator != null && !validator.CanShowComponentSearchBox(screenPoint))
                return null;

            var dialog = new GH_PopupSearchDialog
            {
                BasePoint = screenPoint,
                Canvas = canvas,
            };

            dialog.FormClosed += (s, e) =>
            {
                if (_pending != null && ReferenceEquals(_pending.Dialog, dialog))
                    _pending = null;
            };

            var owner = canvas.FindForm();
            if (owner != null)
                dialog.Show(owner);
            else
                dialog.Show();
            return dialog;
        }

        private static bool IsEmptyCanvas(GH_Document document, PointF point)
        {
            if (document.RelevantObjectAtPoint(point) != null)
                return false;

            IGH_Param source = null, target = null;
            return !document.FindWireAt(point, GhOps.WireHitRadius, ref source, ref target);
        }

        private static bool IsInputGrip(IGH_Param param, IGH_Attributes attributes, float canvasX)
        {
            switch (param.Kind)
            {
                case GH_ParamKind.input:
                    return true;
                case GH_ParamKind.output:
                    return false;
            }

            if (!attributes.HasInputGrip)
                return false;
            if (!attributes.HasOutputGrip)
                return true;

            // Floating parameters have both grips; the nearer one is the one being dragged.
            return Math.Abs(attributes.OutputGrip.X - canvasX) >= Math.Abs(attributes.InputGrip.X - canvasX);
        }

        private sealed class PendingConnect
        {
            public PendingConnect(IGH_Param param, bool fromInput, GH_PopupSearchDialog dialog)
            {
                Param = param;
                FromInput = fromInput;
                Dialog = dialog;
            }

            public IGH_Param Param { get; }
            public bool FromInput { get; }
            public GH_PopupSearchDialog Dialog { get; }
        }
    }
}
