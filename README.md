# NOVA TaskBar

NOVA TaskBar is the Windows companion surface for the NOVA device ecosystem. This repository starts with a native Windows floating panel and versioned contracts for later services and clients.

## Run

Requirements: Windows 10/11 and the .NET 10 SDK with the Windows Desktop workload.

```powershell
dotnet run --project src/Nova.WindowsClient/Nova.WindowsClient.csproj
```

Closing the panel hides it to the system tray. The tray menu restores the panel or exits the process. See [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md) for architecture and milestones.
