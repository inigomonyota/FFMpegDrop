# FFmpeg Drop

**A fast, responsive, and honest batch processor for FFmpeg.**

FFmpeg Drop was built born out of frustration. Most "Video Converters" are just wrappers around FFmpeg that try to hide what they are doing, often restricting your control, freezing up when you load too many files, or making it impossible to debug why a file failed.

This tool is different. It doesn't hide FFmpeg; it embraces it. It puts the power of the command line in a modern, drag-and-drop interface that actually respects your system resources.

![Screenshot Placeholder](screenshot.png) *(You should add a screenshot here!)*

## Why is this different?

*   **Unapologetically FFmpeg:** We don't hide the magic. You type the actual arguments you want passed to the encoder. If you know how to use the CLI, you know how to use this.
*   **Zero-Lag UI:** Built with advanced asynchronous patterns (Semaphores, Channels, and UI virtualization). You can drop 5,000 files into the queue, and the UI will remain buttery smooth.
*   **Dynamic Concurrency:** Realized you're using too much CPU mid-batch? You can change the number of simultaneous jobs *while the batch is running*. The app will intelligently throttle or resume jobs to match your setting immediately.
*   **Per-Job Logging:** No more scrolling through a massive, jumbled text file to find one error. Click any file in the queue to see the specific FFmpeg log history for **that file only**.

## Key Features

*   **Smart Drag & Drop:** Filter and queue valid media files recursively from folders.
*   **Live Token System:** Use simple placeholders like `{in}`, `{out}`, `{name}`, and `{dir}` to build your command templates.
*   **Safety First:**
    *   **Atomic Operations:** Output is written to a temp file and only moved/renamed upon successful completion. No corrupted half-written files if you crash.
    *   **Overwrite Protection:** Option to auto-rename output files if the destination exists.
*   **Pause & Resume:**
    *   **Global Pause:** Suspend all active FFmpeg processes instantly (releasing CPU) and resume them later.
    *   **Auto-Throttling:** If you lower the job limit, the app automatically pauses the files with the least progress to free up slots.
*   **Modern UI:** Features a clean, dark-mode compatible interface (auto-detects Windows theme) with Windows 11 rounded corners and acrylic touches.

## How to Use

1.  **Get FFmpeg:**
    FFmpeg Drop does not bundle the encoder (to keep the download small and let you choose your version).
    *   Download `ffmpeg.exe` and `ffprobe.exe`.
    *   Place them in the same folder as `FfmpegDrop.exe`.
2.  **Queue Files:** Drag and drop files or folders onto the main list.
3.  **Set Arguments:**
    Enter your FFmpeg parameters. For example:
    
    ```-c:v libx264 -crf 23 -c:a copy "{out}"```
    
    *Make sure to include `{out}` for the destination!*
4.  **Run:** Click **Run**.
5.  **Adjust:** Use the "Jobs" dropdown to control how many files convert at once (1-4).

### Available Tokens
*   `{in}` : The full path to the source file.
*   `{out}` : The auto-generated temporary output path.
*   `{dir}` : The directory of the source file.
*   `{name}` : The filename (without extension).
*   `{ext}` : The file extension.

## For Developers (How it works)

This is a WPF (.NET 8) application designed to solve the common threading pitfalls of UI-based batch processors.

*   **Log Batching:** FFmpeg output is buffered off-thread and flushed to the UI in batches, preventing the "TextBox freeze" common in other apps.
*   **Virtualization:** The file list uses UI virtualization, allowing for massive queues with minimal RAM usage.
*   **Task Management:** Uses `System.Threading.Channels` for a strict FIFO queue and `SemaphoreSlim` for concurrency control, allowing the "Active Jobs" limit to be hot-swapped at runtime.

## License

[MIT License](LICENSE) - Do whatever you want with it. Just stop using video converters that hide the logs!

    
## Support the Project

This tool is free and open source. If you find it useful and want to support its development, you can do so here:

[Donate via BuyMeACoffee]

http://www.buymeacoffee.com/inigomonyota