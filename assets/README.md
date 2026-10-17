# Vemo identity

The approved icon joins a continuous white audio waveform to the letter V on a lavender rounded-square tile, with a diagonal gradient from `#A797ED` to `#8270D8` and a transparent exterior. Android and macOS share the exact design in `vemo-android-icon.svg` and `vemo-icon.svg`.

`scripts/build-icon.swift` deterministically renders the shared geometry and colors with CoreGraphics at all standard macOS icon sizes. Run `swift scripts/build-icon.swift assets` to generate `vemo-icon.png` (1024 × 1024), `Vemo.icns`, and the intermediate `Vemo.iconset`.

An initial concept was made with the built-in image-generation tool. Its exterior contained a baked transparency grid; a follow-up image edit failed because the upstream image provider was unavailable. The shipped assets are vector drawings rendered from code, rather than edits of that raster.

The final vector assets and their rendering source are licensed with Vemo under MIT.
