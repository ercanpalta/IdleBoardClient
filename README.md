# IdleBoard Client

A Windows digital signage presentation client built with .NET and WPF that receives real-time image broadcasts from an IdleBoard server via Server-Sent Events (SSE) and displays them in borderless fullscreen mode.

## 🚀 Key Features

- **Borderless Fullscreen Display:** Distraction-free, borderless display panel optimized for digital monitors, screens, and public boards.
- **Real-Time SSE Streaming:** Listens to server broadcast events over Server-Sent Events and reflects image updates immediately.
- **Offline Caching & Fallback Slideshow:** Asynchronously caches broadcast images to disk. If network or server connectivity drops, the client automatically continues cycling through cached images offline.
- **System Tray Integration:** Minimizes cleanly to the Windows system tray and runs unobtrusively in the background.
- **Administration Settings:** Quick access to server connection configuration and display settings via `Ctrl + Shift + S`.
- **System Startup Automation:** Built-in auto-start capability with Windows boot.
- **Dynamic Display Adaptability:** Supports configurable image fit modes (uniform, uniform-to-fill, stretch) and smooth slide transition effects.

## 📦 Requirements & Building

- **Operating System:** Windows 10 / 11
- **Runtime / SDK:** .NET 8.0 SDK or higher
- **IDE:** Visual Studio 2022+ or Visual Studio Code with C# Dev Kit

```bash
# Restore and build the solution
dotnet build

# Launch the client application
dotnet run --project IdleBoardClient
```

## ⌨️ Shortcuts & Controls

- **`Ctrl + Shift + S`**: Open connection and configuration settings window.
- **Keyboard Interaction**: Automatic minimization to system tray for seamless unattended operation.

## 🛡️ License

MIT License