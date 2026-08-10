## Viewport

`Viewport -> Show viewport` opens a 3D preview on the right side of the window.
Point it at a skinned `.ogf` model with `Viewport -> Load model...` and the motion
selected in the list is played back on that model's skeleton.

The preview is built by `converter.dll`, which bakes the model and the selected
motion into a binary glTF file, and rendered by [f3d](https://f3d.app) - a copy
of `f3d.exe` lives in `SDK\binaries` and is copied next to the editor after every
build.
