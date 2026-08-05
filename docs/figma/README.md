# Figma handoff

- File: [Samsung Switch Watch](https://www.figma.com/design/JueYiLj18xFE7enHvGlU2s)
- Target: Windows WPF, 1440x900 dashboard, minimum 1280x720
- Current Viewer dashboard: node `33:205`
- Current device-management dialog: node `37:4`
- Current device-command dashboard: node `37:333`
- Current simplified Agent connection dialog: node `73:72`
- Current no-input Agent Setup screen: node `73:362`
- Previous HTTPS Agent connection and same-PC preflight dialog: node `52:123`
- Previous Agent Setup same-PC address helper screen: node `54:363`
- Current Agent Setup recovery-failed screen: node `58:120`
- Current Agent Setup sanitized field-diagnostic screen: node `60:340`
- Current Viewer connection sanitized field-diagnostic screen: node `62:99`
- Previous HTTPS Agent connection dialog: node `36:2`
- Previous Agent Setup manual management-network screen: node `46:72`
- Agent Setup manual CIDR states: normalized `48:72`, invalid `48:85`, maximum `48:98`
- Agent Setup firewall verification failure state: node `49:72`
- Mini window: 400x260 offline/recovery frame, node `14:127`
- Popup state strip: node `15:129`
- Operational and security state gallery: node `15:148`
- UX audit board: page `24:2`, root frame `24:3`
- Figma font: Noto Sans KR
- Windows implementation font: Segoe UI

Pages:

1. Cover
2. Foundations
3. Components
4. Screens
5. 05 UX Audit

WPF has no supported Figma Code Connect framework label, and this POC file is
not a published library. Handoff therefore uses Figma variables, component
descriptions, node IDs, screenshots, and `get_design_context`; no false Code
Connect mapping is created.

## Current core operator flow

The current design follows the user-approved Agent/Viewer boundary:

1. The Viewer stores the switch name, model, IP address, login ID, login
   password, and optional Enable password for the current Windows user.
2. The Viewer asks the remote Agent to test a fresh Telnet session.
3. The Agent receives one request, connects to TCP 23, logs in, optionally
   enters Enable mode, runs a validated single-line `show` command, returns the
   output, and closes the Telnet session.
4. Manual command output exists only in Viewer memory. It may be copied but is
   not persisted or exported.
5. Monitoring runs only while the Viewer is open. The dashboard reports the
   latest check and any monitoring gap after the Viewer is reopened.

Node `37:4` is the source of truth for device input order and credential
ownership. The simple form keeps advanced security terminology away from the
operator and makes connection testing explicit before monitoring is enabled.

Node `37:333` is the source of truth for the manual command experience. It
accepts one `show` command, shows common commands such as `show port status` and
`show sylog tail num 100`, displays the returned output, and clearly states
that the raw output is not saved.

## Superseded connection and Setup input history (v7-v10)

The following connection, network-input, and firewall-gate frames document
earlier decisions only. They are retained for traceability and must not be used
as current setup or connection instructions. The current implementation source
of truth is the v0.11 section below.

Node `36:2` established the removal of the historical fingerprint,
pairing-token, `SSW1:` pairing, and Bearer-token flows. Node `52:123` preserves
that simplified Agent address and fixed HTTPS port `18443`; access control
belongs to the management network and Windows Firewall rules.

### v10 connection layout

Node `52:123` established the v10 connection layout. The v10 operator had to
explicitly press `Agent와 Viewer가 같은 PC일 때 테스트`; the Viewer never starts this check
automatically. It searches only real private IPv4 addresses on the current PC,
not `localhost` or `127/8`, and separately reports these three scopes:

1. Agent service, TCP/18443, HTTPS, API, and version are reachable.
2. Switch access has not yet been tested and remains under
   구현 화면에서는 `장비 관리 → 로그인 확인`으로 명확히 표시합니다. Figma 노드의 기존
   `접속 시험` 문구는 다음 디자인 동기화 때 같은 의미로 갱신해야 합니다.
3. A same-PC success does not prove the route or firewall from the actual
   remote Viewer PC.

Node `46:72` was the source of truth for the historical Agent Setup
management-network portion. Automatic RFC1918 management-network discovery
remains the default, while an approved routed network that is absent from the
list may be added as `IPv4/prefix`. A host address is normalized to its
canonical network address, public or RFC1918-crossing ranges are rejected, and
automatic plus manual selections are limited to two unique networks.

The companion state frames show the required feedback: `48:72` for successful
normalization, `48:85` for invalid or non-private input, and `48:98` when a
third unique network is attempted. Existing one or two canonical
`AllowedTargetCidrs` are restored into the same list. If that target list cannot
be restored safely, Setup shows `SETUP_EXISTING_NETWORKS_NOT_LOADED`, preloads
no network, and asks the operator to select or add the approved networks again.

Node `54:363` established the initial Viewer-address step while preserving that
management-network behavior. The historical `같은 PC 시험용 주소` action filled a single
detected private IPv4 immediately or lets the operator select among multiple
private IPv4 candidates. If no suitable address exists, Setup shows actionable
feedback instead of inventing a loopback address. Before remote deployment, the
v10 operator had to run Setup again with the actual Viewer PC fixed IPv4 so the Agent
API and product-owned Windows Firewall rule return to the intended exact `/32`
scope.

The v8 screen preserves the v7 firewall behavior: another program's inbound
TCP/18443 Allow rule is shown as a warning and is never changed or removed.
The historical `설치 / 업데이트` flow created the product-owned Viewer `/32`
rule and configures the Agent to allow only the fixed Viewer IPv4 at the
remote-work API boundary. Local `/health/live` and `/health/ready` checks remain
the only loopback exception, and no separate auto-fix button is added.

Node `49:72` is the v9 failure-state source of truth for
`SETUP_FIREWALL_FAILED`. Windows may return the same single Viewer host as a
bare IPv4, `/32`, or `/255.255.255.255`; those three forms are treated as
equivalent while broader, multiple, ranged, or different addresses remain
blocked. Setup retries readback for at most two seconds, reports only a stable
safe mismatch category such as `FIREWALL_REMOTE_ADDRESS_MISMATCH`, and restores
the pre-install state if verification still fails. The operator is explicitly
warned not to broaden the firewall scope manually.

## Current supporting states

Node `33:205` keeps the three-column operational dashboard while adding clear
entry points for Agent connection and device management. It explicitly states
that monitoring is Viewer-owned and stops when the Viewer is closed.

### v11 Agent Setup recovery flow

Nodes `58:72`, `58:97`, `58:120`, and `58:147` are the source of truth for
interrupted Agent Setup recovery.

1. Setup detects an unfinished transaction without changing files, services,
   or firewall rules.
2. A safe pending transaction disables installation and exposes a separate
   `이전 상태 복구` action.
3. Successful recovery re-enables `설치 / 업데이트`, but never starts a new
   installation automatically.
4. A failed recovery shows the original installation failure separately from
   each rollback-stage failure. It does not repeat the generic
   `SETUP_ROLLBACK_FAILED` row.
5. `진단정보 복사` appears only after failure and copies sanitized metadata to
   the clipboard. It does not create a persistent diagnostic file.
6. A corrupt or state-mismatched journal disables both installation and
   recovery and asks for administrator review. Operators must not delete or
   move staging, backup, failed, or journal evidence manually.

The four frames cover recovery required (`58:72`), recovery completed
(`58:97`), retryable recovery failure with diagnostic-copy feedback
(`58:120`), and unsafe recovery state (`58:147`).

### v13 recovery evidence cleanup state

Node `58:120` was updated in place as `Agent Setup / Recovery Failed v13`.
It is the current source of truth when `ROLLBACK_EVIDENCE_CLEANUP_FAILED`
prevents a recovery from being verified.

1. The title is `이전 상태를 완전히 복구하지 못했습니다`; the safe detail is
   `설치 자료 정리 미완료 · 작업 기록 보존`, so neither implies that recovery
   completed.
2. `설치 / 업데이트` remains disabled until a fresh journal inspection proves
   that no pending recovery remains.
3. The operator action is `복구 다시 시도`; repeated failure points to
   `익명 진단 저장`.
4. The screen does not offer manual evidence deletion, a bypass, or automatic
   installation.
5. The failure remains sanitized. The top-level state is
   `SETUP_ROLLBACK_FAILED`, while the result row names the affected safe target
   with one of
   `ROLLBACK_STAGING_CLEANUP_FAILED`,
   `ROLLBACK_BACKUP_CLEANUP_FAILED`,
   `ROLLBACK_FAILED_DIRECTORY_CLEANUP_FAILED`, and
   `ROLLBACK_JOURNAL_CLEANUP_FAILED`. The generic
   `ROLLBACK_EVIDENCE_CLEANUP_FAILED` remains a diagnostic classification; no
   path or file name is shown.

### v12 sanitized field diagnostic flow

Nodes `60:340` and `62:99` are the source of truth for manually saved field
diagnostics.

1. Agent Setup and Viewer never save a diagnostic automatically.
2. `익명 진단 저장` appears only after a check has completed, whether it
   succeeded or failed.
3. The file uses the versioned `SSW_FIELD_DIAGNOSTIC/2` compact text contract,
   no more than 12 lines or 88 characters per line, and UTF-8 with BOM.
4. Stable stage, result and error codes plus compact bounded state are
   retained. IP/CIDR, PC and user names, credentials, certificate details,
   paths, raw firewall data, exception text and switch command output are
   excluded.
5. On the same PC, the operator enters `localhost`, `127.0.0.1`, or a private
   IPv4 address in the normal Agent-address field and runs the standard
   connection check. A same-PC success still does not prove the remote Viewer
   route or switch access.
6. Existing failure-only clipboard diagnostics remain available in Agent
   Setup. The new TXT action does not replace them.

Node `60:340` covers the Agent Setup completed-check state and node `62:99`
covers the Viewer connection-check state. Both preserve the established
compact WPF layout and Noto Sans KR Figma typography.

### v14 failure-only support-code flow

Nodes `58:120` and `62:99` now include the failure context in which the
short support code is used. Component source frames `68:340` and `69:341`
define the Agent Setup and Viewer variants.

1. The label is exactly `지원 코드 · 이 코드만 전달하세요`.
2. The value uses `SWD1-XXXX-XXXX-XXXX-XXXX` and is a selectable, read-only
   text field. No new copy button is introduced.
3. The panel exists only after a failed Agent Setup operation or failed Viewer
   connection check. Running, success, a new attempt, or edited connection
   input clears the stale value.
4. The code is generated and validated offline. It is not a secret,
   authentication token, pairing token, certificate fingerprint, or access
   approval.
5. CRC detects common transcription errors; it does not provide encryption,
   signing, or identity proof.
6. Agent Setup keeps its existing failure-only clipboard diagnostic, while both
   products manually save the one-photo `SSW_FIELD_DIAGNOSTIC/2` TXT. Existing
   `/1` files remain replay-compatible. The short code does not replace either
   diagnostic path.

## Historical references

- Previous simplified dashboard v5: node `25:138`
- Previous HTTP connection v4: node `25:214`
- Previous Agent Setup firewall-overlap state v7: node `42:340`
- Historical v3 pairing wizard: node `20:129`
- Historical-flow warning banner: node `29:205`
- Previous command capability and fallback screen: node `22:131`
- Previous operations dashboard: node `11:64`

Historical frames remain for decision traceability only. Certificate
fingerprints, pairing tokens, `SSW1:` strings, Bearer-token input, Agent-side
device credentials, and Agent-side monitoring schedules must not be
reintroduced into the current flow.

## v0.11 operability-first flow

Nodes `73:72` and `73:362` are the implementation source of truth for the
current release.

1. Viewer asks only for the Agent PC IPv4 or internal DNS name. HTTPS/TCP
   `18443` is fixed and no certificate fingerprint, pairing token, same-PC
   helper, or trust-reset action is exposed.
2. Viewer accepts the Agent's current self-signed TLS certificate automatically.
   API v4 compatibility is the connection gate; a product-version difference is
   shown as a warning and does not block a compatible connection.
3. Agent Setup has no Viewer IPv4 or management-CIDR input. It installs the
   hidden Windows service and applies the three RFC1918 ranges automatically.
4. The product-owned Domain/Private firewall rule is best effort. A firewall,
   local HTTPS, API, or version readiness failure after the service is installed
   is an actionable warning, not a reason to remove the installed Agent.
5. Device credentials remain Viewer-owned, Agent remains stateless, monitoring
   runs only while Viewer is open, and the Agent still accepts one validated
   single-line `show` command per request for Telnet/TCP `23` targets in RFC1918
   space.

This design intentionally prioritizes reliable operation on restricted company
PCs. HTTPS still encrypts the transport, but automatic certificate acceptance
does not authenticate the Agent endpoint. The UI therefore describes the
network boundary plainly instead of presenting the connection as strongly
authenticated.
