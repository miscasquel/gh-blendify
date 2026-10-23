// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 miscasquel

using System.Drawing;
using System.Windows.Forms;
using Grasshopper.GUI.Canvas;

namespace Blendify
{
    /// <summary>
    /// Safety net for the middle-button intercept: while MMB is physically held down,
    /// Grasshopper is not allowed to open its canvas menu. This covers the case where the
    /// WinForms message filter is never called (e.g. a host with a different message loop).
    /// </summary>
    internal sealed class MmbCanvasValidator : GH_CanvasValidator
    {
        public override bool CanShowCanvasMenu(PointF pt)
        {
            // Mirror Blendify's MMB handling: leave canvases without a document (and
            // canvases with modifiers disabled) entirely to Grasshopper.
            if (Document == null || Canvas == null || !Canvas.ModifiersEnabled)
                return base.CanShowCanvasMenu(pt);

            return (Control.MouseButtons & MouseButtons.Middle) == MouseButtons.None
                && base.CanShowCanvasMenu(pt);
        }
    }
}
