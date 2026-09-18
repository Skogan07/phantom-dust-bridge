> Workflow 5 supports bounded production arsenal writes with live progress, frame-readiness retries, latest-wins coordination, and saved-profile verification.

# Phantom Dust Bridge

.NET 10 x64 Windows tray companion. Production mode reads the linked profile, updates only the selected arsenal name and skill bytes, asks Phantom Dust to save normally, and verifies the saved result. The operator-only `--test-profile=<evidence.json>` mode remains available for bounded disposable-profile diagnostics.

Build: `dotnet build Bridge.slnx`.
Test: `dotnet test Bridge.Tests/Bridge.Tests.csproj`.
Publish: `dotnet publish Bridge.App/Bridge.App.csproj -c Release -r win-x64 --self-contained true -o publish`.

Run `Bridge.App.exe` for private-LAN read-only mode, or `Bridge.App.exe --demo` for isolated simulated inventory. The demo server binds loopback only; use ADB reverse for emulator integration tests. Normal and controlled-test modes listen only through the authenticated private-network service. The Bridge advertises its current endpoint through mDNS and refreshes that advertisement after a private-address change so an already paired phone can securely rediscover it. Set a trusted network to Private in Windows; do not expose the bridge publicly.

The UI generates a five-minute, one-use four-digit code plus the existing QR/text credential. A matching saved phone identity can use either path to rotate a lost token without losing links. The tray also controls local pause/revoke, exports read-only diagnostics and offers optional sign-in startup. Closing the window leaves the tray running; use Exit to stop it. `enable-private-firewall.ps1` is an optional administrator-run helper restricted to Private networks and LocalSubnet; it is not run by the build.

Data: `%USERPROFILE%\.phantom-dust-bridge\live` or `demo`. This canonical per-user directory is shared by normal desktop and Codex-launched processes and is outside LocalAppData virtualization; it contains the durable SQLite queue, logs, and shared DPAPI certificate at `%USERPROFILE%\.phantom-dust-bridge\certificate.dpapi`. On first startup, the Bridge stages and atomically installs one complete legacy bundle from `%LOCALAPPDATA%\PhantomDustBridge` (including live/demo state); an existing canonical root always wins as a whole and legacy data is never deleted or mixed into it. For a one-time repair from another context, pass `Bridge.App.exe --legacy-root=C:\absolute\path\to\PhantomDustBridge`; this is ignored once canonical storage exists. Never share a pairing payload or private data directory. A single-instance lock is acquired before migration, state, or certificate access, and startup fails if port 17431 is occupied; the runtime never kills another process. Each Bridge database records that certificate's SHA-256 fingerprint before listening; a paired Bridge refuses to start if the file is missing or its fingerprint changes, so identity loss requires an explicit recovery instead of silently breaking every phone pin. Newer state schemas fail closed rather than being rewritten by an older Bridge.

For diagnostics without starting the API: `Bridge.App.exe --diagnostic`. This writes `memory-diagnostic.json` in the live data directory. Candidate bytes remain unverified. No game writes occur.

For automated tests only: `--demo --headless --pair-file=C:\private\pair.json` explicitly exports a one-use credential. Protect and delete this file afterward; it is not a log.

See [PC Sync](../docs/PC_SYNC.md), [protocol](../docs/PC_SYNC_PROTOCOL.md), and [memory verification ledger](../docs/PD_MEMORY_NOTES.md).
