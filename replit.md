# V-Notch

macOS-style Dynamic Island notch for Windows 10/11. WPF desktop app, .NET 10 (`net10.0-windows10.0.19041.0`), imported from github.com/rainaku/V-Notch.

## Important constraints
- **Windows-only**: WPF + Win32 interop. It cannot build or run in this Linux workspace — no run workflow is configured on purpose. Verify changes by building/running on a Windows machine (`dotnet build`, tests in `Tests/VNotch.Tests.csproj`).
- Code changes here are delivered by committing and pushing to the GitHub `origin` remote (`main`).

## Structure
- `Windows/` — XAML windows incl. `MainWindow` (the notch) and `SpotlightWindow` (Alt+Space search)
- `Controllers/` — feature controllers (Spotlight hotkey, media, capture, etc.)
- `Services/Spotlight/` — search service, ranking, providers (apps, system tools, Everything IPC, Windows Search index, calculator)
- `Tests/` — xUnit tests (excluded from the main csproj build)

## User preferences
- Respond in Vietnamese when the user writes in Vietnamese.
- After completing work, commit and push to GitHub `main` (pull/merge remote first if it's ahead).
