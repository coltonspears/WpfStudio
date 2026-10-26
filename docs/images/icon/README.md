# Application icon

`WpfStudio.svg` is the source of `src/WpfStudio.App/Resources/WpfStudio.ico` at 40–256 px. `WpfStudio-small.svg`
is the same mark cropped tighter, without the shadow and bevels, for the 16–32 px frames so it stays legible.
Render each SVG to PNG at every icon size (16, 20, 24, 32, 40, 48, 64, 128, 256) and pack the PNGs into one `.ico`.
