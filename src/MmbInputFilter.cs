// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 miscasquel

using System;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;

namespace Blendify
{
    /// <summary>
    /// Claims the middle mouse button before Grasshopper's canvas handler sees it.
    /// GH creates its radial menu on MMB down and immediately repaints it; replacing the
    /// interaction afterwards leaves the menu visible for one frame. Swallowing the
    /// message here lets us start the pan without GH ever constructing the radial menu.
    /// </summary>
    internal sealed class MmbInputFilter : IMessageFilter
    {
        private const int WmMButtonDown = 0x0207;
        private const int WmMButtonDblClk = 0x0209;

        // Message filters are per UI thread, so keep one instance per thread.
        [ThreadStatic]
        private static MmbInputFilter _instance;

        public static void Install()
        {
            if (_instance != null)
                return;

            _instance = new MmbInputFilter();
            Application.AddMessageFilter(_instance);
        }

        bool IMessageFilter.PreFilterMessage(ref Message m)
        {
            if (m.Msg != WmMButtonDown && m.Msg != WmMButtonDblClk)
                return false;

            var canvas = FindCanvas(m.HWnd);
            if (canvas == null || canvas.IsDisposed || canvas.Document == null || !canvas.ModifiersEnabled)
                return false;

            long lParam = m.LParam.ToInt64();
            int x = (short)(lParam & 0xFFFF);
            int y = (short)((lParam >> 16) & 0xFFFF);

            var args = new MouseEventArgs(
                MouseButtons.Middle,
                m.Msg == WmMButtonDblClk ? 2 : 1,
                x,
                y,
                0);
            var mouseEvent = new GH_CanvasMouseEvent(canvas.Viewport, args);

            canvas.Select();
            canvas.Capture = true;
            canvas.ActiveInteraction = new MmbPanInteraction(canvas, mouseEvent);
            return true;
        }

        private static GH_Canvas FindCanvas(IntPtr handle)
        {
            var control = Control.FromHandle(handle) ?? Control.FromChildHandle(handle);
            while (control != null)
            {
                if (control is GH_Canvas canvas)
                    return canvas;
                control = control.Parent;
            }
            return null;
        }
    }
}
