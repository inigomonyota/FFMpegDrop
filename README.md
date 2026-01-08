FFmpeg Drop (minimal batch GUI)

- Drag/drop files or folders into the queue
- Paste FFmpeg arguments into the template box (must include {in} and {out})
- Optional:
  - Overwrite originals (safe temp + replace)
  - Output to a chosen folder
  - Show FFmpeg window (disables live log capture)

Runtime requirement:
- Put ffmpeg.exe next to the built EXE (AppContext.BaseDirectory).
  Example after build:
    bin\Release\net8.0-windows\ffmpeg.exe
    bin\Release\net8.0-windows\FfmpegDrop.exe

Presets:
- Saved to %APPDATA%\FfmpegDrop\presets.json
