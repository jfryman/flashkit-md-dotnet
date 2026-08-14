# Flatpak launcher icons

Generated from `src/FlashKit.Gui/Assets/flashkit.ico` (the icon the GUI
itself shows in its window/tray): the ico's single 32x32 frame is
converted to RGBA — the ico's AND mask becomes the alpha channel — and
nearest-neighbor upscaled to each hicolor size, keeping the pixel-art
chip glyph crisp.

Two properties matter; keep both if the artwork ever changes:

- **Transparent background.** The pre-1.10.2 icon was an opaque
  white-background conversion, which KDE launchers render as what looks
  like the generic "document" fallback icon.
- **Named exactly after the app ID** in `hicolor/<size>/apps/` — flatpak
  only exports icons whose file name matches the app ID.

To regenerate (e.g. after changing flashkit.ico), run the pure-python
converter preserved in the repo history (commit introducing this file)
or any tool that produces RGBA PNGs with transparency at 32/64/128/256.
