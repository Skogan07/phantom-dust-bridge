# Phantom Dust Bridge

Phantom Dust Bridge is the optional Windows companion for **PD - Arsenal Builder**. It connects the Android app to Phantom Dust over a private local network, reads loaded profiles and arsenals, and performs explicitly confirmed arsenal updates with save verification.

The Bridge is designed for private LAN use only. It is not a cloud service and must not be exposed to the public internet. Pairing credentials, certificates, logs, databases, and diagnostic exports are private data and are intentionally excluded from this repository.

## Requirements

- Windows x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) when building from source
- Phantom Dust for Windows for live game integration

## Build and test

From the repository root:

```powershell
dotnet restore pc-bridge/Bridge.slnx
dotnet build pc-bridge/Bridge.slnx -c Release --no-restore
dotnet test pc-bridge/Bridge.Tests/Bridge.Tests.csproj -c Release --no-build
```

Create a self-contained Windows build:

```powershell
dotnet publish pc-bridge/Bridge.App/Bridge.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/bridge-win-x64
```

Run `Bridge.App.exe` from the publish directory. Windows may warn about unsigned community software; verify that your download came from this repository before running it.

## Safety model

- Network service is limited to authenticated private-network use.
- Pairing uses one-use credentials and certificate pinning.
- Game writes are restricted to the selected arsenal name and skills, followed by the game's normal save path and read-back verification.
- Inventory, case bytes, pairing data, and private local state are not published here.
- `pc-bridge/enable-private-firewall.ps1` is an optional administrator-run helper limited to Private networks and `LocalSubnet`.

See [Bridge usage and data storage](pc-bridge/README.md), [PC Sync behavior](docs/PC_SYNC.md), [protocol details](docs/PC_SYNC_PROTOCOL.md), and the [memory verification ledger](docs/PD_MEMORY_NOTES.md).

## Disclaimer

This is an unofficial fan-made companion. It is not affiliated with or endorsed by Microsoft or Xbox. Back up important game data before using software that interacts with a running game process.
