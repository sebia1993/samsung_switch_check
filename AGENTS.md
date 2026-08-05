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
.\scripts\build-release.ps1 -Version 0.11.1-poc
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
- Production Agent listens only on HTTPS/18443. It accepts loopback and RFC1918 IPv4 Viewer sources
  and connects only to RFC1918 IPv4 switch targets on Telnet/23.
- Each request uses a fresh bounded Telnet session and always disconnects. If the device closes the
  connection during command execution, reconnect at most once and execute only unfinished commands;
  never retry authentication/enable failures or command timeouts.
- The manual Viewer UI accepts one normalized `show` command at a time; one Agent API request may carry at most eight validated commands for monitoring.
- Each command may include `show running-config`; reject line breaks, separators and configuration commands.
- Manual command and raw output remain in Viewer memory and are never persisted or exported.

## Safety

- Never commit credentials, tokens, certificates, real IPs, host names, MAC addresses, or company command output.
- The Agent API has no application authentication. Keep it on a trusted private company network;
  never expose it to a user VLAN, public Wi-Fi or the Internet.
- The product-owned Windows Firewall rule is best effort: Domain/Private inbound TCP/18443 from the
  three RFC1918 ranges. A firewall/GPO/apply/readback failure is an actionable warning and must not
  roll back an otherwise installed service.
- Agent TLS uses a freshly generated self-signed RSA certificate on each process start. Windows
  Schannel requires a temporary UserKeySet container; do not set PersistKeySet, and dispose the
  certificate so the temporary container is removed on process exit. Viewer accepts this
  certificate automatically; TLS encrypts transport but does not authenticate the Agent endpoint.
- Agent DataDirectory is exactly `%ProgramData%\SamsungSwitchWatch`; reject custom paths. A new
  install may adopt only an empty, non-reparse product root whose owner and ACL pass the existing
  trusted-path checks; reject unknown non-empty roots.
- A legacy `install-receipt.json`, when present, must remain Administrators-owned with
  SYSTEM/Administrators-only ACL. It is not a Viewer or target address authority.
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
- Preserve compatible Agent ProgramData configuration across transactional updates. Legacy trust
  and CIDR fields may be read for schema compatibility but must not control v0.11 runtime access.
- Copy packages into protected staging and rehash them before swapping. If service quiescence,
  rollback dependencies or legacy moves are incomplete, block later file mutation and preserve
  snapshots, archives, backups and journal evidence.
- Internal Actions artifacts contain six validation files; GitHub Release custom Assets contain only the versioned Agent and Viewer ZIP files.
- Keep Setup preflight readiness compatible with the legacy API v4 minimum payload. Commit a valid
  file/service installation before readiness checks. Local TCP/HTTPS/API/version or firewall
  readiness failures must leave the service installed and report an actionable warning such as
  `AGENT_LOCAL_CONNECTION_UNCONFIRMED`; do not enter deployment rollback for readiness alone.
- Generate a new production Agent RSA certificate on each start and dispose its non-persistent
  Schannel UserKeySet container on shutdown. Do not add certificate files, persistent identity,
  trust pins, pairing tokens or user confirmation back into the runtime flow.
- Keep unexpected Setup diagnostics limited to safe stage/category/timing values; never add exception
  text, PID, address or path data.
- Viewer automatic status must distinguish awaiting/deferred collection and current unavailable
  from a confirmed current result. Dispose replaced HTTP clients without racing active requests.
- Run the extracted executable smoke gate for Viewer, Mock Agent and Agent Setup in an elevated
  Windows CI environment. This does not replace field checks for Samsung firmware, EDR, firewall
  routing or the full native installation path.
- Verify `git ls-files AGENTS.md` before GitHub handoff.
