// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 miscasquel

using System.Drawing;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Canvas.Interaction;

namespace Blendify
{
    /// <summary>
    /// Blender-style middle-mouse pan. Claimed on MMB down, so Grasshopper's
    /// MMB radial menu is replaced (space bar still opens it).
    /// </summary>
    internal sealed class MmbPanInteraction : GH_AbstractInteraction
    {
        private readonly Point _start;
        private readonly Point _origin;

        public MmbPanInteraction(GH_Canvas canvas, GH_CanvasMouseEvent e)
            : base(canvas, e, false)
        {
            _start = e.ControlLocation;
            _origin = canvas.Viewport.Target;
        }

        public override GH_ObjectResponse RespondToMouseMove(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button != MouseButtons.Middle)
                return GH_ObjectResponse.Release;

            var viewport = sender.Viewport;
            viewport.Tx = _origin.X + (e.ControlX - _start.X);
            viewport.Ty = _origin.Y + (e.ControlY - _start.Y);
            sender.Refresh();
            sender.OnViewportChanged();
            return GH_ObjectResponse.Handled;
        }

        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
            => GH_ObjectResponse.Release;
    }
}
