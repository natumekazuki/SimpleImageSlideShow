# SimpleImageSlideShow Installer & Build Guide

## Prerequisites

- .NET 8.0 SDK with the MAUI workloads (`dotnet workload install maui` if needed)
- Inno Setup 6 (command line tool `ISCC.exe` on the PATH makes scriptable builds easier)

## Building the app

1. Restore workloads and NuGet packages  
   
   ```powershell
   dotnet workload restore
   ```

2. Optional sanity build for Windows  

   ```powershell
   dotnet build SimpleImageSlideShow\SimpleImageSlideShow.csproj `
     -f net8.0-windows10.0.19041.0
   ```

3. Publish a Release build that carries the .NET runtime so it can run on a clean Windows machine  

   ```powershell
   dotnet publish SimpleImageSlideShow\SimpleImageSlideShow.csproj `
     -c Release `
     -f net8.0-windows10.0.19041.0 `
     -r win10-x64 `
     -p:SelfContained=true `
     -p:PublishSingleFile=false `
     -p:WindowsPackageType=None `
     -o installer\publish
   ```

   Publish output: `installer\publish`

## Creating the Inno Setup installer

1. Ensure the publish folder above contains the latest build (rerun `dotnet publish` when necessary).
2. Build `installer\SimpleImageSlideShow.iss` from the Inno Setup IDE or invoke `ISCC.exe installer\SimpleImageSlideShow.iss` (you can run this command from the repository root; the script always uses `installer\publish` as its payload folder).
3. The generated installer is written to `installer\artifacts\SimpleImageSlideShow_<version>.exe`.
   - If `ISCC.exe` is not on your `PATH`, call it via the absolute path, e.g.  

     ```powershell
     & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\SimpleImageSlideShow.iss
     ```

     Adjust the path if you installed Inno Setup elsewhere or add the folder to `PATH` for convenience.

## Version flow

- Define the application version once in `SimpleImageSlideShow\SimpleImageSlideShow.csproj` via the `<Version>`, `<FileVersion>`, and `<AssemblyVersion>` elements.
- Every `dotnet build`/`dotnet publish` run updates `installer\AppVersion.ispp` automatically (see the `UpdateInstallerVersionFile` MSBuild target in the csproj). The Inno Setup script includes this file to get `AppVersion`, so the installer filename and metadata exactly matches the csproj version.
- The installer build stops early if the publish folder or `SimpleImageSlideShow.exe` is missing; run the publish command above first so the required payload exists.
