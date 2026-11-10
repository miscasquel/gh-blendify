// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 miscasquel

using System;
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
    /// Per-canvas gesture router. Implements the Blender-style input scheme:
    ///
    ///   MMB drag                      pan
    ///   Shift + RMB sweep across      merge swept wires into one
    ///   wires                         Relay (reroute hub)
    ///   Ctrl + RMB drag across wires  cut them
    ///   drag a wire from a socket     open component search and auto-connect
    ///   and release unconnected       the created component
    ///   drop a node with free         insert it into the wire under the cursor
    ///   outputs onto a wire
    /// </summary>
    internal sealed class CanvasState
    {
        // GH_WireInteraction searches this far for the socket a wire would connect to.
        private const int WireTargetSearchRadius = 20;

        // Canvas-unit radius for finding the wire under a dragged object.
        private const float WireDropSearchRadius = 8f;

        // Control-pixel travel before a left press counts as a drag.
        private const int InsertDragThreshold = 4;

        private static readonly Pen InsertPen = new Pen(Color.FromArgb(255, 255, 200, 40), 3.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        private readonly GH_Canvas _canvas;

        // Wire-drag bookkeeping. Grasshopper owns the drag itself; we only remember
        // what its GH_WireInteraction reports while the wire is on the cursor.
        private bool _wireDragArmed;
        private bool _wireDragHasTarget;
        private IGH_Param _wireDragSource;
        private bool _wireDragFromInput;

        // Link-drag search: the socket a wire was pulled from.
        private PendingConnect _pending;

        // Left-drag insertion: the top-level object under the press and the wire the
        // cursor is currently offering to splice it into.
        private bool _leftDown;
        private Point _leftDownControl;
        private IGH_DocumentObject _insertCandidate;
        private PointF _leftDownPivot;
        private bool _leftDownPivotValid;
        private WireInsertion? _insertHover;

        public CanvasState(GH_Canvas canvas)
        {
            _canvas = canvas;
            canvas.CanvasPostPaintObjects += DrawInsertHighlight;
        }

        public void OnMouseDown(object sender, MouseEventArgs e)
        {
            var canvas = _canvas;
            var document = canvas.Document;

            _pending = null;
            ResetWireDrag();
            ResetWireInsert();

            if (document == null || !canvas.ModifiersEnabled)
                return;

            var mouseEvent = new GH_CanvasMouseEvent(canvas.Viewport, e);

            switch (e.Button)
            {
                case MouseButtons.Left:
                    BeginInsertTracking(mouseEvent);
                    return;

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

            // A lost MouseUp (for example after the cursor leaves the control) must not
            // leave stale insertion state behind.
            if (_leftDown && (Control.MouseButtons & MouseButtons.Left) == MouseButtons.None)
                ResetWireInsert();
            else
                UpdateInsertHover(new GH_CanvasMouseEvent(canvas.Viewport, e));
        }

        public void OnMouseUp(object sender, MouseEventArgs e)
        {
            var canvas = _canvas;
            var insertion = e.Button == MouseButtons.Left ? CaptureInsertionOnMouseUp(e) : (WireInsertion?)null;
            var source = _wireDragSource;
            var fromInput = _wireDragFromInput;
            var openSearch = _wireDragArmed && !_wireDragHasTarget;

            ResetWireDrag();

            if (insertion.HasValue)
            {
                var insertDocument = canvas.Document;
                if (insertDocument != null)
                {
                    GhOps.InsertIntoWire(insertDocument, insertion.Value);
                    canvas.Invalidate();
                }
            }

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
                    ResetWireInsert();
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

        private void BeginInsertTracking(GH_CanvasMouseEvent e)
        {
            _leftDown = true;
            _leftDownControl = e.ControlLocation;
            _insertCandidate = FindTopLevelObjectAt(_canvas.Document, e.CanvasLocation);

            var attributes = _insertCandidate?.Attributes;
            _leftDownPivotValid = attributes != null;
            _leftDownPivot = _leftDownPivotValid ? attributes.Pivot : PointF.Empty;
        }

        private WireInsertion? CaptureInsertionOnMouseUp(MouseEventArgs e)
        {
            WireInsertion? result = null;
            if (_leftDown && _insertCandidate != null)
            {
                var mouseEvent = new GH_CanvasMouseEvent(_canvas.Viewport, e);
                if (TryGetInsertionAt(mouseEvent, out var insertion))
                    result = insertion;
            }

            ResetWireInsert();
            return result;
        }

        private void UpdateInsertHover(GH_CanvasMouseEvent e)
        {
            WireInsertion? hover = null;
            if (TryGetInsertionAt(e, out var insertion))
                hover = insertion;

            if (!SameInsertion(hover, _insertHover))
            {
                _insertHover = hover;
                _canvas.Invalidate();
            }
        }

        private bool TryGetInsertionAt(GH_CanvasMouseEvent e, out WireInsertion insertion)
        {
            insertion = default(WireInsertion);
            var canvas = _canvas;
            var document = canvas.Document;
            var candidate = _insertCandidate;

            if (!_leftDown || candidate == null || document == null)
                return false;
            if ((Control.ModifierKeys & Keys.Alt) != Keys.None)
                return false;
            if (!HasDraggedFarEnough(e.ControlLocation))
                return false;
            if (!CandidateHasMoved(candidate))
                return false;
            if (!canvas.ClientRectangle.Contains(e.ControlLocation))
                return false;
            if (document.SelectedCount != 1)
                return false;

            return GhOps.TryFindWireInsertion(document, candidate, e.CanvasLocation, WireDropSearchRadius, out insertion);
        }

        private bool HasDraggedFarEnough(Point control)
        {
            return Math.Abs(control.X - _leftDownControl.X)
                + Math.Abs(control.Y - _leftDownControl.Y) > InsertDragThreshold;
        }

        private bool CandidateHasMoved(IGH_DocumentObject candidate)
        {
            if (!_leftDownPivotValid || candidate?.Attributes == null)
                return false;

            var pivot = candidate.Attributes.Pivot;
            return Math.Abs(pivot.X - _leftDownPivot.X)
                + Math.Abs(pivot.Y - _leftDownPivot.Y) > 0.5f;
        }

        private void ResetWireInsert()
        {
            _leftDown = false;
            _insertCandidate = null;
            _leftDownPivotValid = false;
            if (_insertHover.HasValue)
            {
                _insertHover = null;
                _canvas.Invalidate();
            }
        }

        private static IGH_DocumentObject FindTopLevelObjectAt(GH_Document document, PointF canvasPoint)
        {
            if (document == null)
                return null;

            var data = document.RelevantObjectAtPoint(canvasPoint);
            if (data?.TopLevelObject == null)
                return null;

            switch (data.ObjectType)
            {
                // Components and floating parameters report doc_object when their body
                // (rather than a grip or balloon) is picked.
                case GH_ObjectSpecies.doc_object:
                case GH_ObjectSpecies.component:
                case GH_ObjectSpecies.parameter:
                    break;
                default:
                    return null;
            }

            var obj = data.TopLevelObject;
            return obj is IGH_Component || obj is IGH_Param ? obj : null;
        }

        private static bool SameInsertion(WireInsertion? a, WireInsertion? b)
        {
            if (!a.HasValue || !b.HasValue)
                return a.HasValue == b.HasValue;

            var first = a.Value;
            var second = b.Value;
            return ReferenceEquals(first.WireSource, second.WireSource)
                && ReferenceEquals(first.WireTarget, second.WireTarget)
                && ReferenceEquals(first.Input, second.Input)
                && ReferenceEquals(first.Output, second.Output);
        }

        private void DrawInsertHighlight(GH_Canvas sender)
        {
            var insertion = _insertHover;
            if (!insertion.HasValue)
                return;

            var graphics = sender.Graphics;
            if (graphics == null)
                return;

            var sourceAttributes = insertion.Value.WireSource?.Attributes;
            var targetAttributes = insertion.Value.WireTarget?.Attributes;
            if (sourceAttributes == null || targetAttributes == null)
                return;

            graphics.DrawLines(
                InsertPen,
                GhOps.WirePolyline(sourceAttributes.OutputGrip, targetAttributes.InputGrip));
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
