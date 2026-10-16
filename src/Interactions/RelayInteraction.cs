// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 miscasquel

using System.Drawing;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Canvas.Interaction;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Blendify
{
    /// <summary>
    /// Blender-style Shift+RMB sweep across a wire: inserts a Relay (reroute) and lets it
    /// slide along the original wire while the drag continues. Insertion happens on the
    /// first real movement, so a plain Shift+RMB click does not modify the document.
    /// </summary>
    internal sealed class RelayInteraction : GH_AbstractInteraction
    {
        private readonly GH_Document _document;
        private readonly IGH_Param _source;
        private readonly IGH_Param _target;
        private GH_Relay _relay;

        public RelayInteraction(GH_Canvas canvas, GH_CanvasMouseEvent e, IGH_Param source, IGH_Param target)
            : base(canvas, e, true)
        {
            _document = canvas.Document;
            _source = source;
            _target = target;
        }

        public override GH_ObjectResponse RespondToMouseMove(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            base.RespondToMouseMove(sender, e);
            if (!IsActive)
                return GH_ObjectResponse.Ignore;

            var location = GhOps.NearestPointOnWire(_source, _target, e.CanvasLocation);
            if (_relay == null)
                _relay = GhOps.InsertRelay(_document, _source, _target, location);
            else
                MoveRelay(location);

            sender.Invalidate();
            return GH_ObjectResponse.Handled;
        }

        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (_relay != null)
            {
                MoveRelay(GhOps.NearestPointOnWire(_source, _target, e.CanvasLocation));
                sender.Invalidate();
            }
            return GH_ObjectResponse.Release;
        }

        private void MoveRelay(PointF location)
        {
            _relay.Attributes.Pivot = location;
            _relay.Attributes.ExpireLayout();
        }
    }
}
