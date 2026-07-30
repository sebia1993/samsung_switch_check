# Samsung Switch Watch

Windows-only Samsung iES switch Telnet execution and monitoring proof of concept.

## Structure

- `src/SamsungSwitchWatch.Core`: Telnet negotiation, prompt handling, command validation and sanitized errors.
- `src/SamsungSwitchWatch.Agent`: stateless HTTPS-to-Telnet execution service.
- `src/SamsungSwitchWatch.Viewer`: WPF device/credential owner, dashboard, monitoring and local history.
- `tests`: deterministic tests using synthetic Telnet servers and sanitized fixtures only.
- `scripts`: PowerShell 5.1-compatible build, install, rollback, uninstall and diagnostics.

## Commands

```powershell
dotnet restore SamsungSwitchWatch.sln --locked-mode
dotnet build SamsungSwitchWatch.sln -c Release --no-restore
dotnet test SamsungSwitchWatch.sln -c Release --no-build
.\scripts\validate.ps1 -Configuration Release
.\scripts\build-release.ps1 -Version 0.10.10-poc
```

Use the .NET 10 SDK. Release packages target `win-x64`, are self-contained, single-file, and untrimmed.
Both release ZIPs include `SamsungSwitchWatch_User_Manual_KO.pdf`; the editable DOCX stays repository-only.
Regenerate the manual from `tools/build-user-manual.py` before a release whenever the operator flow changes.

## Runtime ownership

- Viewer owns device IP/model, DPAPI CurrentUser credentials, monitoring schedules, baselines, gaps and events.
- Agent stores no device inventory, credential, command, result, monitoring state or event history.
- Public Agent runtime is Windows service-only with `--service`; direct no-argument or
  `--background` launch exits.
- The service runs as the passwordless `NT SERVICE\SamsungSwitchWatchAgent` virtual account.
  Accept legacy `LocalService`-owned data descendants only for one stopped-service migration.
- Production Agent listens only on HTTPS/18443 and connects only to allowed IPv4 CIDRs on Telnet/23.
- Each request uses a fresh bounded Telnet session and always disconnects. If the device closes the
  connection during command execution, reconnect at most once and execute only unfinished commands;
  never retry authentication/enable failures or command timeouts.
- The manual Viewer UI accepts one normalized `show` command at a time; one Agent API request may carry at most eight validated commands for monitoring.
- Each command may include `show running-config`; reject line breaks, separators and configuration commands.
- Manual command and raw output remain in Viewer memory and are never persisted or exported.

## Safety

- Never commit credentials, tokens, certificates, real IPs, host names, MAC addresses, or company command output.
- The Agent API has no application authentication. The exact Viewer IPv4 is enforced both by the
  product-owned Windows Firewall rule and by the Agent request middleware.
- Treat Windows Firewall readback forms `IP`, `IP/32`, and
  `IP/255.255.255.255` as equivalent only for the same single Viewer host. Never accept another
  prefix, list, range, `Any`, `LocalSubnet`, or IPv6 as an exact Viewer rule.
- Keep post-apply firewall verification bounded, preserve strict non-address fields, roll back on
  mismatch, and expose only stable mismatch codes rather than raw addresses or rule contents.
- Persistent Agent ECDSA identity is stored only under ProgramData and protected with DPAPI LocalMachine.
- Agent DataDirectory is exactly `%ProgramData%\SamsungSwitchWatch`; reject custom paths and even
  empty pre-existing roots during a new install.
- `install-receipt.json` is Administrators-owned with SYSTEM/Administrators-only ACL. It is not a
  CIDR authority; preserve target CIDRs from validated config and management CIDRs from the exact
  owned firewall rule.
- Keep stable sanitized error codes; never log passwords, enable passwords, commands, or raw output.
- Do not claim live validation from mock tests.
- Do not perform live network writes or company-network testing from Codex.

## Design and delivery

- Figma file `Samsung Switch Watch` is the UI source of truth.
- Keep operator screens compact, keyboard-accessible and readable at 1280x720 or higher.
- Do not commit generated `bin`, `obj`, `artifacts`, `release`, database, certificate or secret files.
- `SamsungSwitchWatch.Agent.Setup.exe` is the only public Agent installation entrypoint. All
  PowerShell/CMD deployment scripts stay source-only for development and legacy recovery.
- The Viewer release is portable: extract the ZIP and run `SamsungSwitchWatch.Viewer.exe`.
  Do not add public install scripts, auto-start registration or an administrator requirement.
- Preserve Agent ProgramData identity and CIDR configuration across transactional updates.
- Copy packages into protected staging and rehash them before swapping. If service quiescence,
  rollback dependencies or legacy moves are incomplete, block later file mutation and preserve
  snapshots, archives, backups and journal evidence.
- Internal Actions artifacts contain six validation files; GitHub Release custom Assets contain only the versioned Agent and Viewer ZIP files.
- Verify `git ls-files AGENTS.md` before GitHub handoff.
