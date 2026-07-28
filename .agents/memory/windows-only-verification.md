---
name: Windows-only verification
description: How to validate changes in this repo given it cannot build or run on Replit Linux
---

The rule: never claim runtime verification for this project from the Replit workspace; it targets `net10.0-windows10.0.19041.0` with WPF/Win32 interop and only compiles/runs on Windows.

**Why:** the repl is Linux; `dotnet` for this TFM plus WPF is unavailable. Screenshots/workflows are impossible.

**How to apply:** validate with careful code reading, an architect review round, and by writing/extending xUnit tests under `Tests/` (they run on the user's Windows machine or CI). Tell the user explicitly what still needs Windows-side verification, and propose it as a follow-up task when substantial.
