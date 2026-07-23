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
.\scripts\build-release.ps1 -Version 0.9.6-poc
```

Use the .NET 10 SDK. Release packages target `win-x64`, are self-contained, single-file, and untrimmed.
Both release ZIPs include `SamsungSwitchWatch_User_Manual_KO.pdf`; the editable DOCX stays repository-only.

## Runtime ownership

- Viewer owns device IP/model, DPAPI CurrentUser credentials, monitoring schedules, baselines, gaps and events.
- Agent stores no device inventory, credential, command, result, monitoring state or event history.
- Public Agent runtime is Windows service-only with `--service`; direct no-argument or
  `--background` launch exits.
- Production Agent listens only on HTTPS/18443 and connects only to allowed IPv4 CIDRs on Telnet/23.
- Each request uses a fresh bounded Telnet session and always disconnects. If the device closes the
  connection during command execution, reconnect at most once and execute only unfinished commands;
  never retry authentication/enable failures or command timeouts.
- The manual Viewer UI accepts one normalized `show` command at a time; one Agent API request may carry at most eight validated commands for monitoring.
- Each command may include `show running-config`; reject line breaks, separators and configuration commands.
- Manual command and raw output remain in Viewer memory and are never persisted or exported.

## Safety

- Never commit credentials, tokens, certificates, real IPs, host names, MAC addresses, or company command output.
- The Agent API has no application authentication. Windows Firewall management CIDRs are the access boundary.
- Persistent Agent ECDSA identity is stored only under ProgramData and protected with DPAPI LocalMachine.
- Keep stable sanitized error codes; never log passwords, enable passwords, commands, or raw output.
- Do not claim live validation from mock tests.
- Do not perform live network writes or company-network testing from Codex.

## Design and delivery

- Figma file `Samsung Switch Watch` is the UI source of truth.
- Keep operator screens compact, keyboard-accessible and readable at 1280x720 or higher.
- Do not commit generated `bin`, `obj`, `artifacts`, `release`, database, certificate or secret files.
- `Install-or-Update-Agent.cmd` is the only public Agent installation entrypoint. Legacy
  scheduled-task scripts stay source-only for ownership-aware migration and must not enter public ZIPs.
- Preserve Agent ProgramData identity and CIDR configuration across transactional updates.
- Internal Actions artifacts contain six validation files; GitHub Release custom Assets contain only the versioned Agent and Viewer ZIP files.
- Verify `git ls-files AGENTS.md` before GitHub handoff.
