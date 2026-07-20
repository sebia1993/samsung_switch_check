# Samsung Switch Watch

Windows-only Samsung iES switch monitoring proof of concept.

## Structure

- `src/SamsungSwitchWatch.Core`: Telnet, parsing, normalization, event and diff logic.
- `src/SamsungSwitchWatch.Agent`: Windows service, polling, local storage, HTTPS API and event hub.
- `src/SamsungSwitchWatch.Viewer`: WPF dashboard, mini window, tray icon and alerts.
- `tests`: deterministic unit and integration tests; use fake/synthetic Telnet data only.
- `scripts`: PowerShell 5.1-compatible build, install, uninstall and diagnostics scripts.

## Commands

```powershell
dotnet restore SamsungSwitchWatch.sln
dotnet build SamsungSwitchWatch.sln -c Release --no-restore
dotnet test SamsungSwitchWatch.sln -c Release --no-build
```

Use the .NET 10 SDK. Release packages target `win-x64`, are self-contained, and must keep trimming disabled.

## Safety

- Never commit credentials, API tokens, certificates, real IPs, host names, MAC addresses, or company command output.
- Agent CLI execution is limited to registered read-only command IDs. Do not add free-form configuration commands.
- Raw Telnet output stays on the Agent PC and must not be returned by Viewer APIs.
- Tests and local development use mock Telnet servers and sanitized fixtures.
- Keep stable diagnostic codes and redact sensitive data before exports.
- Do not perform live device changes or live company-network tests from Codex.

## Design and delivery

- Figma file `Samsung Switch Watch` is the UI source of truth.
- Keep operator screens compact, keyboard-accessible, and readable at 1280x720 or higher.
- Do not commit generated `bin`, `obj`, `artifacts`, `release`, database, certificate, or secret files.
- Verify `git ls-files AGENTS.md` before any GitHub handoff.
