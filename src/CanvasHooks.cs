// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 miscasquel

using System.Runtime.CompilerServices;
using Grasshopper.GUI.Canvas;

namespace Blendify
{
    internal static class CanvasHooks
    {
        private static readonly ConditionalWeakTable<GH_Canvas, CanvasState> States =
            new ConditionalWeakTable<GH_Canvas, CanvasState>();

        public static void OnCanvasCreated(GH_Canvas canvas) => Attach(canvas);

        public static void Attach(GH_Canvas canvas)
        {
            if (canvas == null)
                return;

            // Run on the canvas' UI thread so the middle-button filter catches MMB before
            // Grasshopper's own canvas handler can open the radial menu.
            MmbInputFilter.Install();

            if (States.TryGetValue(canvas, out _))
                return;

            var state = new CanvasState(canvas);
            States.Add(canvas, state);

            // Backstop in case the message filter is bypassed by the host's message loop.
            canvas.AddValidator(new MmbCanvasValidator());

            // Grasshopper subscribes its own handlers in the GH_Canvas constructor, so ours
            // run afterwards and see whatever the default behaviour already prepared
            // (e.g. a pending pan/zoom interaction or the radial menu).
            canvas.MouseDown += state.OnMouseDown;
            canvas.MouseUp += state.OnMouseUp;
            canvas.KeyDown += state.OnKeyDown;
            canvas.Document_ObjectsAdded += state.OnDocumentObjectsAdded;
        }
    }
}
