## Viewport

`Viewport -> Show viewport` opens a 3D preview on the right side of the window.
Point it at a skinned `.ogf` model with `Viewport -> Load model...` and the motion
selected in the list is played back on that model's skeleton.

The preview is built by `converter.dll`, which bakes the model and the selected
motion into a binary glTF file, and rendered by [f3d](https://f3d.app) - a copy
of which ships next to the editor in an `f3d` folder of its own.

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
