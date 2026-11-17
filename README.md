# NOVA TaskBar

NOVA TaskBar is the Windows companion surface for the NOVA device ecosystem. This repository starts with a native Windows floating panel and versioned contracts for later services and clients.

## Download and run on Windows

For a ready-to-run build, open [GitHub Releases](https://github.com/popolo123215-source/NOVA-TASKBAR/releases/latest) and download `NOVA-Taskbar-v6-win-x64.zip`. Extract the whole archive, then run `Nova.TaskBar.exe` from the extracted folder. Keep the adjacent files together; the EXE is not a standalone single-file build.

The release is self-contained and includes the .NET runtime. It requires Windows 10 version 2004 (build 19041) or later, x64. The app starts in the compact NOVA capsule and adds an icon to the system tray; use the tray icon to restore the panel or exit.

GitHub's **Code → Download ZIP** downloads source code only, not the ready-to-run application.

## Run from source

Requirements: Windows 10/11 x64 and the .NET 10 SDK with the Windows Desktop workload.

```powershell
dotnet run --project src/Nova.WindowsClient/Nova.WindowsClient.csproj
```

Closing the panel hides it to the system tray. The tray menu restores the panel or exits the process. See [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md) for architecture and milestones.
