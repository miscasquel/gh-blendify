# Blendify

**v0.0.1 — work in progress.** This project is under heavy development: expect rough
edges, breaking changes, and treat everything below as *implemented but pending manual
verification in Rhino*.

Blender-style mouse controls for the Grasshopper canvas (Rhino 8 / 9, Windows).

## What it does

| Gesture | Behaviour |
| --- | --- |
| **Middle mouse drag** | Pan the canvas (replaces GH's MMB radial menu; space bar still opens it). |
| **Shift + right-drag across wires** | On release, insert a single Relay that merges every wire the stroke crosses: all swept sources feed the relay and each swept link is rerouted through it (one undo step). A trail and wire highlights preview the merge while dragging; a Shift+RMB click relays the nearest wire. |
| **Ctrl + right-drag across wires** | Cut every wire the stroke crosses (single undo step). |
| **Drag from a socket, drop on empty canvas** | Open the component search; the created component is auto-connected to that socket when a compatible one exists. |

Everything else stays stock Grasshopper: right-click menus, wheel zoom, left-drag
selection, double-click search, `Ctrl+Z` undo (all Blendify edits are undoable).

## Status

- All gestures listed above are implemented, and the plugin compiles against the Rhino 8
  Grasshopper assemblies.
- Runtime behaviour has **not** been manually verified inside Rhino yet — testing is the
  next milestone before anything here is called stable.
- API surface, gesture set and internals may change without notice. No stability
  guarantees before v0.1.0.

## Philosophy

This is a personal, opinionated project, published openly in case someone finds it
useful. It deliberately replaces a few stock Grasshopper gestures with Blender-style ones
(see Notes), and it only implements what its author actually uses. Feature requests may
be ignored; forks are welcome.

## Install

Build `Blendify.gha` and copy it into Grasshopper's libraries folder:

```
%APPDATA%\Grasshopper\Libraries\Blendify.gha
```

Then restart Rhino (or use *File → Special Folders → Components Folder* to locate the
folder and restart Grasshopper).

## Notes

- `MMB click` no longer opens Grasshopper's radial menu (all MMB input belongs to pan).
- `Ctrl + RMB` no longer zooms; use the mouse wheel (or plain `RMB drag` to pan).
- The link-search popup is Grasshopper's own search dialog, so the hit list is not
  type-filtered; Blendify picks the best compatible socket on the new component and
  wires it automatically (exact type match > assignable > generic).
- A relay merge joins data streams: one relay carries one stream, so the targets of the
  merged wires receive the combined data. Sweeping across wires that chain into each
  other (e.g. `A→B` and `B→C`) therefore creates a loop, which Grasshopper reports as a
  cyclic data flow.

## Build

Requires the .NET SDK (7 or newer).

```
dotnet build -c Release
```

The plugin is produced at `bin/Release/Blendify.gha`. It is compiled against the
Grasshopper 8 API and loads in Rhino 8 and Rhino 9. Rhino assemblies are never copied
next to the plugin.

On non-Windows machines the project can still be compiled for verification because
`EnableWindowsTargeting` is set.

## Layout

```
src/
  BlendifyPriority.cs          assembly bootstrap (GH_AssemblyPriority)
  CanvasHooks.cs               attaches a CanvasState to every GH_Canvas
  CanvasState.cs               gesture routing + search popups
  GhOps.cs                     wire geometry, relay insertion, connect/cut with undo
  Interactions/
    MmbPanInteraction.cs       Blender-style middle-mouse pan
    RelayInteraction.cs        Shift+RMB reroute insertion
    WireCutInteraction.cs      Ctrl+RMB wire cutting
```

## License

GPL-3.0-or-later — see [LICENSE](LICENSE).
