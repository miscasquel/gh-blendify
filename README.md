# Blendify

**v0.2.0 — work in progress.** This project is under active development: expect rough
edges and breaking changes.

Blender-style mouse controls for the Grasshopper canvas (Rhino 8 / 9, Windows).

## What it does

| Gesture | Behaviour |
| --- | --- |
| **Middle mouse drag** | Pan the canvas (replaces GH's MMB radial menu; space bar still opens it). |
| **Shift + right-drag across wires** | On release, insert one Relay per distinct source output among the wires the stroke crosses. Each relay is placed at the midpoint of the stroke/wire intersections for its source and reroutes that source's swept links through itself (one undo step). A trail and wire highlights preview the relays while dragging; a plain Shift+RMB click does nothing. |
| **Ctrl + right-drag across wires** | Cut every wire the stroke crosses (single undo step). |
| **Drag from a socket, drop on empty canvas** | Open the component search; the created component is auto-connected to that socket when a compatible one exists. |

Everything else stays stock Grasshopper: right-click menus, wheel zoom, left-drag
selection, double-click search, `Ctrl+Z` undo (all Blendify edits are undoable).

## Status

- All gestures listed above are implemented and were exercised by hand in Rhino 8; the
  plugin compiles against the Rhino 8 Grasshopper assemblies.
- API surface, gesture set and internals may change without notice; this is still early
  software.

## Philosophy

This is a personal, opinionated project, published openly in case someone finds it
useful. It deliberately replaces a few stock Grasshopper gestures with Blender-style ones
(see Notes), and it only implements what its author actually uses. Feature requests may
be ignored; forks are welcome.

## Install

Download `Blendify.gha` from the
[latest release](https://github.com/miscasquel/gh-blendify/releases/latest) — no
compiler or .NET SDK needed.

1. Close Rhino.
2. If Windows marked the download as blocked, right-click the file → *Properties* →
   tick *Unblock* → *OK*.
3. Copy `Blendify.gha` into Grasshopper's libraries folder:

   ```
   %APPDATA%\Grasshopper\Libraries\Blendify.gha
   ```

4. Start Rhino; Blendify loads automatically. (In Grasshopper, *File → Special Folders
   → Components Folder* opens the same folder.)

`Blendify.gha` is a single plugin assembly; it uses the Grasshopper assemblies that ship
with Rhino, so there is nothing else to install.

## Notes

- `MMB click` no longer opens Grasshopper's radial menu (all MMB input belongs to pan).
- `Ctrl + RMB` no longer zooms; use the mouse wheel (or plain `RMB drag` to pan).
- The link-search popup is Grasshopper's own search dialog, so the hit list is not
  type-filtered; Blendify picks the best compatible socket on the new component and
  wires it automatically (exact type match > assignable > generic).
- A Shift+RMB relay is created per distinct source output, so streams from different
  outputs are not merged. The relay is placed at the midpoint of that output's
  stroke/wire intersections.
- Shift+RMB only inserts relays while dragging; a click with no movement does nothing.

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
  MmbInputFilter.cs            claims MMB before Grasshopper's canvas handler
  MmbCanvasValidator.cs        suppresses GH's MMB canvas menu as a backstop
  Interactions/
    MmbPanInteraction.cs       Blender-style middle-mouse pan
    RelayInteraction.cs        Shift+RMB reroute insertion
    WireCutInteraction.cs      Ctrl+RMB wire cutting
```

## License

GPL-3.0-or-later — see [LICENSE](LICENSE).
