# FFmpeg (optional, for bundling into MSIX)

This app can use FFmpeg in two ways:

1. **Bundled**: `ffmpeg.exe` is included with the app (preferred for MSIX installs)
2. **System**: `ffmpeg` is available on `PATH`

## How to bundle

Place the following files in this folder:

- `ffmpeg.exe` (required)
- `ffprobe.exe` (optional, improves trimming/duration detection)

The project file conditionally includes these binaries (if present) as Content so they are:

- copied to `bin\...` for local runs
- included in the MSIX package output

At runtime the app will resolve FFmpeg from either:

- `AppContext.BaseDirectory\ffmpeg.exe` (next to the executable), or
- `AppContext.BaseDirectory\External\ffmpeg\ffmpeg.exe` (default MSIX-bundled layout)

## Notes

- These binaries are intentionally **not** committed (see repo `.gitignore`).
- Ensure the FFmpeg build you use is appropriate for your distribution/licensing requirements.
