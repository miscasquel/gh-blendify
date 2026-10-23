// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 miscasquel

using Grasshopper;
using Grasshopper.Kernel;

namespace Blendify
{
    /// <summary>
    /// Assembly bootstrap. Grasshopper instantiates this class before anything else from
    /// this assembly, which is the earliest point where we can hook the canvas.
    /// </summary>
    public class BlendifyPriority : GH_AssemblyPriority
    {
        public override GH_LoadingInstruction PriorityLoad()
        {
            MmbInputFilter.Install();
            Instances.CanvasCreated += CanvasHooks.OnCanvasCreated;

            // The editor may already exist when the plugin is (re)loaded.
            CanvasHooks.Attach(Instances.ActiveCanvas);

            return GH_LoadingInstruction.Proceed;
        }
    }
}
