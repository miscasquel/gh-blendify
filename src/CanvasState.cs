// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 miscasquel

using System;
using System.Drawing;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Canvas.Interaction;
using Grasshopper.Kernel;

namespace Blendify
{
    /// <summary>
    /// Per-canvas gesture router. Implements the Blender-style input scheme:
    ///
    ///   MMB drag                      pan
    ///   Shift + RMB sweep across      merge swept wires into one
    ///   wires                         Relay (reroute hub)
    ///   Ctrl + RMB drag across wires  cut them
    ///   drag a wire from a socket     open component search and auto-connect
    ///   and release unconnected       the created component
    /// </summary>
    internal sealed class CanvasState
    {
        // GH_WireInteraction searches this far for the socket a wire would connect to.
        private const int WireTargetSearchRadius = 20;

        private readonly GH_Canvas _canvas;

        // Wire-drag bookkeeping. Grasshopper owns the drag itself; we only remember
        // what its GH_WireInteraction reports while the wire is on the cursor.
        private bool _wireDragArmed;
        private bool _wireDragHasTarget;
        private IGH_Param _wireDragSource;
        private bool _wireDragFromInput;

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
            ResetWireDrag();

            if (document == null || !canvas.ModifiersEnabled)
                return;

            var mouseEvent = new GH_CanvasMouseEvent(canvas.Viewport, e);

            switch (e.Button)
            {
                case MouseButtons.Middle:
                    // Blender: MMB drag pans. Normally MmbInputFilter claims the button
                    // before GH sees it; this is the fallback if the filter is unavailable.
                    if (canvas.ActiveInteraction is MmbPanInteraction)
                        return;
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
            }
        }

        public void OnMouseMove(object sender, MouseEventArgs e)
        {
            var canvas = _canvas;

            if (canvas.ActiveInteraction is GH_WireInteraction wire)
            {
                if (!wire.IsActive)
                    return;

                if (!TryCaptureWireDragSource(wire))
                {
                    ResetWireDrag();
                    return;
                }

                _wireDragArmed = true;

                // GH_WireInteraction updates its target on every mouse move and does not
                // re-test it on mouse-up, so the last move is the state it will act on.
                UpdateWireDragTarget(new GH_CanvasMouseEvent(canvas.Viewport, e));
                return;
            }

            // The wire interaction ended or was replaced before the release we act on.
            ResetWireDrag();
        }

        public void OnMouseUp(object sender, MouseEventArgs e)
        {
            var canvas = _canvas;
            var source = _wireDragSource;
            var fromInput = _wireDragFromInput;
            var openSearch = _wireDragArmed && !_wireDragHasTarget;

            ResetWireDrag();

            if (!openSearch || source == null || e.Button != MouseButtons.Left)
                return;

            // MouseUp is subscribed after Grasshopper's handler, so a normally completed
            // wire drag has already been destroyed here. If it is still active, GH did not
            // finish the drag normally (for example a Rhino 9 search or disabled interaction)
            // and we must not open a second popup.
            if (canvas.ActiveInteraction is GH_WireInteraction)
                return;

            if (!canvas.ClientRectangle.Contains(e.Location))
                return;

            var document = canvas.Document;
            if (document == null)
                return;

            var dialog = OpenSearch(canvas, canvas.PointToScreen(e.Location));
            if (dialog != null && !dialog.IsDisposed)
                _pending = new PendingConnect(source, fromInput, dialog);
        }

        public void OnKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Escape:
                    _pending = null;
                    ResetWireDrag();
                    break;

                // Rhino 9 uses Space to open its own link search while a wire is being
                // dragged, and Alt to turn an empty drop into a Relay. Grasshopper handles
                // those paths; do not also open our search on the subsequent release.
                case Keys.Space:
                case Keys.Menu:
                    ResetWireDrag();
                    break;
            }
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

        /// <summary>
        /// Recovers the socket Grasshopper is dragging from. GH_WireInteraction exposes the
        /// mouse-down point publicly but not its source, so ask the document the same
        /// question Grasshopper asked when it started the drag.
        /// </summary>
        private bool TryCaptureWireDragSource(GH_WireInteraction wire)
        {
            if (_wireDragSource != null)
                return true;

            var document = _canvas.Document;
            if (document == null)
                return false;

            var grip = document.RelevantObjectAtPoint(wire.CanvasPointDown);
            if (grip == null)
                return false;

            switch (grip.ObjectType)
            {
                case GH_ObjectSpecies.grip_in:
                case GH_ObjectSpecies.grip_out:
                case GH_ObjectSpecies.grip:
                    break;
                default:
                    return false;
            }

            var param = grip.Parameter;
            if (param?.Attributes == null)
                return false;

            _wireDragSource = param;
            _wireDragFromInput = IsInputGrip(param, param.Attributes, wire.CanvasPointDown.X);
            return true;
        }

        /// <summary>
        /// Mirrors GH_WireInteraction.RespondToMouseMove: a release connects when the last
        /// move found a grip on a different top-level object.
        /// </summary>
        private void UpdateWireDragTarget(GH_CanvasMouseEvent e)
        {
            var document = _canvas.Document;
            var source = _wireDragSource;
            if (document == null || source?.Attributes == null)
            {
                _wireDragHasTarget = false;
                return;
            }

            var target = document.FindAttributeByGrip(
                e.CanvasLocation,
                false,
                !_wireDragFromInput,
                _wireDragFromInput,
                WireTargetSearchRadius);

            if (target == null || !(target.DocObject is IGH_Param))
            {
                _wireDragHasTarget = false;
                return;
            }

            var sourceTopLevel = source.Attributes.GetTopLevel?.DocObject;
            var targetTopLevel = target.GetTopLevel?.DocObject;
            _wireDragHasTarget = sourceTopLevel != null
                && targetTopLevel != null
                && !ReferenceEquals(targetTopLevel, sourceTopLevel);
        }

        private void ResetWireDrag()
        {
            _wireDragArmed = false;
            _wireDragHasTarget = false;
            _wireDragSource = null;
            _wireDragFromInput = false;
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
