# Repository Guidelines

This repository contains a .NET MAUI (Blazor Hybrid) application that displays local images as a slideshow (Slide) or tiled grid (Tiled) on Windows.

## Project Structure & Module Organization
- Root: `SimpleImageSlideShow.sln`
- App: `SimpleImageSlideShow/`
  - UI (Blazor): `Components/Pages` (`Home.razor*` Slide, `Tiled.razor*` Tiled), `Components/Layout`
  - Static assets: `wwwroot/` (`index.html`, CSS)
  - Domain: `Models/` (settings, image entities, helpers)
  - Services (interfaces): `Services/` (settings, image, window, webview host)
  - Windows impl: `Platforms/Windows/` (service implementations, WinUI entry)
  - App shell: `App.xaml*`, `MainPage.xaml*`, `MauiProgram.cs`

## Build, Test, and Development Commands
- Restore workloads: `dotnet workload restore`
- Build (Windows): `dotnet build SimpleImageSlideShow/SimpleImageSlideShow.csproj -f net8.0-windows10.0.19041.0`
- Run (Windows CLI): `dotnet build -t:Run -f net8.0-windows10.0.19041.0`
- Run (Visual Studio): open the solution and start the “Windows Machine” profile.
Notes: The app is Windows-focused (WebView2, FolderPicker, System.Drawing). Non‑Windows TFM isn’t wired.

## Coding Style & Naming Conventions
- C# 10+/NET 8, 4‑space indent, file‑scoped namespaces preferred.
- Naming: PascalCase for types/members; camelCase for locals/parameters; prefix private fields only when it improves clarity.
- Keep platform‑specific code under `Platforms/Windows/` and reference via interfaces in `Services/`.
- Blazor pages follow `Foo.razor`, `Foo.razor.cs`, `Foo.razor.css`.
- Dispose watchers/timers; avoid leaking `FileSystemWatcher` or `Timer`.

## Testing Guidelines
- No formal test project yet. If adding logic that’s testable, create `SimpleImageSlideShow.Tests` (xUnit), mirror namespaces, and name tests `TypeNameTests.cs`.
- Run tests with `dotnet test`.

## Commit & Pull Request Guidelines
- Commits: keep focused; use imperative present tense (e.g., "Add tiled placement FIFO"). Optionally follow Conventional Commits (`feat:`, `fix:`, `refactor:`).
- PRs: include a clear description, linked issue (if any), before/after notes or screenshots (UI), and reproduction/validation steps.
- Scope: avoid unrelated refactors. Prefer small, reviewable changes.

## Security & Configuration Tips
- Settings persist at `FileSystem.AppDataDirectory/SimpleImageSlideShow/settings.json`; do not store secrets.
- Web content accesses local images via WebView2 virtual host `https://appimages.local/`. Keep mapping logic in `IWebViewHostService` implementations.
- Do not reference Windows‑only types from shared code; guard with interfaces and `#if WINDOWS` where needed.

