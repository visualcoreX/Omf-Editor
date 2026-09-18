## Viewport

`Viewport -> Show viewport` opens a 3D preview on the right side of the window.
Point it at a skinned `.ogf` model with `Viewport -> Load model...` and the motion
selected in the list is played back on that model's skeleton.

The preview is built by `converter.dll`, which bakes the model and the selected
motion into a binary glTF file, and rendered by [f3d](https://f3d.app) - a copy
of which ships next to the editor in an `f3d` folder of its own.

`Alpha Blending (slower performance)` on the viewport tool strip picks how translucent
surfaces are drawn. Off - the default - transparency is approximated in a single
pass (`--blending=stochastic`), which comes out slightly grainy and costs almost
nothing. On, overlapping translucent surfaces are blended exactly, by dual depth
peeling (`--blending=ddp`), which walks the geometry once per layer and shows in
the frame rate of a playing motion. The f3d config that ships with the viewer
asks for peeling on everything, and this is what overrides it: an X-Ray model is
opaque but for a scope glass or a hud part, and what alpha it has is usually cut
out rather than blended. The setting is remembered, and switching it brings the
viewer up again - which starts the camera over.

Everything the viewport builds goes into a `viewport_cache` folder beside the
editor: the preview it hands to the viewer, the OMF dumped for it, and a png of
every texture the model asks for. The pngs are the slow part - a folder of dds
takes real time to turn - so the cache is kept between runs, and the editor only
sweeps it on the way in, dropping whatever nothing has asked for in two weeks. A
texture that goes on being used has its date renewed each time it is handed over,
so it stays as long as the model does.

The status line of the viewport ends with `H - viewer keys` on a plate of its
own. Those keys belong to the viewer, not to the editor: click the picture
first, and H lists everything f3d answers to. They are read as the letters that
are typed, so they want a latin keyboard layout - of the editor's own doing only
`Space`, which it forwards to the viewer, works from a russian one.

## Finding a motion

`Ctrl+F`, or `Tools -> Find motion`, puts a search box over the list of motions.
What is typed there picks out the first motion whose name carries it, and the
counter beside the box says which of how many that is.

- `Enter` walks on to the next match, `Shift+Enter` back to the previous one,
  and both wrap around the ends of the list. `F3` and the arrow keys do the same
- `Ctrl+Enter` selects every match at once - which is how a whole family of
  motions is deleted, cloned or saved out in one go
- `Esc` closes the box and gives the keyboard back to the list

A query with `*` or `?` in it is a pattern matched against the whole name, the
way file names are - `*aim*_1` for the first of every aim. Anything else is
matched as a part of the name, case insensitive.

The list itself is left whole rather than filtered down to the matches: a motion
is addressed everywhere else in the editor by its place in it.

## Dropping files in

`.omf`, `.skl`, `.skls` and `.ogf` can be dragged onto the editor window
straight from explorer, several at a time.

- an OMF opens. With a file already open the drop asks first: yes opens the
  dropped file, no adds its motions to the file open, the way the merge button
  does
- a `.skl`/`.skls` goes through the same import as
  `File -> Load/add from skl/skls...` - added to the open file, or made into an
  OMF around the skeleton of the SDK file when nothing is open
- an `.ogf` becomes the model of the viewport, which comes out if it was hidden

A drop of several files takes the model first, so the motions that follow it
are previewed right away.

A viewport with a model in it is the one place that takes nothing: the cursor
says no over the whole panel. The viewer would otherwise take the file itself -
VTK asks for dropped files the old way, with `WS_EX_ACCEPTFILES` - and answer an
OMF with a complaint about an unknown format, so the editor takes that style off
the viewer window as soon as it opens and refuses the area around it as well.

While the viewport stands empty it does take drops: what fills it then is the
`Append OGF` button asking for a model, and a model dropped on it is the answer.
The button is up for exactly as long as there is no model - and as long as there
is no viewer window in the way.

## Building

### What you need

- Visual Studio 2022 with the **Desktop development with C++** workload
  (platform toolset v143) and **.NET desktop development**
- The .NET Framework 4.8 targeting pack

Nothing has to be restored or downloaded: both projects build from what is in
the repository.

### Building it

Open `OMF_Editor.sln`, pick **Release** / **x64** and build. From a command
line:

```
msbuild OMF_Editor.sln /p:Configuration=Release /p:Platform=x64
```

Both projects end up in `bin\x64\Release` - `OMF_Editor.exe` with
`converter.dll` beside it. Debug and 32 bit configurations exist too, but x64
is the one that gets built and released.

### The viewer

f3d is not part of the repository: `SDK` is ignored by git. Without it the
editor still builds and runs, and only the viewport says it cannot find
`f3d.exe`.

Take a **nightly** build rather than a release, from the rolling
[nightly tag](https://github.com/f3d-app/f3d/releases/tag/nightly) of
[f3d-app/f3d](https://github.com/f3d-app/f3d). The asset to grab is the plain
Windows archive, `F3D-<version>-Windows-x86_64.zip` - not the `-raytracing`
one, which is bigger for nothing here, and not the `.exe` installer. The
editor is developed against `3.5.0-240-g5e87d7ad`.

Two things the editor leans on exist only in a nightly: the draggable time bar
(`--animation-progress=advanced`), which is the scrubber of the viewport, and
reacting to the Space key the editor forwards to it. The 3.5.0 release ignores
posted key messages entirely.

The archive holds `bin` and `share`. Put that pair in either place:

- `f3d\bin\f3d.exe` next to the built `OMF_Editor.exe`
- `SDK\f3d\bin\f3d.exe` anywhere above it, which is what a source tree does

### Packaging a release

A release is `OMF_Editor.exe`, `converter.dll` and the whole `f3d` folder next
to them, plus `msvcp140.dll`, `vcruntime140.dll` and `vcruntime140_1.dll`
copied out of `f3d\bin` - with those beside the editor nobody has to install
the Visual C++ runtime.

Of the f3d distribution only `osmesa.dll`, `f3d-console.exe`, `f3d_c_api.dll`
and `F3DShellExtension.dll` can be left out. Everything else has to stay:
`f3d.dll` imports zlib, hdf5, netcdf, usd_ms, blosc, tiff, sqlite3, libcrypto
and tbb, and misses any one of them at startup.
