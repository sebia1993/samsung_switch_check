# Figma handoff

- File: [Samsung Switch Watch](https://www.figma.com/design/JueYiLj18xFE7enHvGlU2s)
- Target: Windows WPF, 1440x900 dashboard, minimum 1280x720
- Current Viewer dashboard: node `33:205`
- Current device-management dialog: node `37:4`
- Current device-command dashboard: node `37:333`
- Current HTTPS Agent connection and same-PC preflight dialog: node `52:123`
- Current Agent Setup same-PC address helper screen: node `54:363`
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

## v6 operator flow

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

Node `36:2` established the removal of the historical fingerprint,
pairing-token, `SSW1:` pairing, and Bearer-token flows. Node `52:123` preserves
that simplified Agent address and fixed HTTPS port `18443`; access control
belongs to the management network and Windows Firewall rules.

Node `52:123` established the v10 connection layout. The current operator must
explicitly press `Agent와 Viewer가 같은 PC일 때 테스트`; the Viewer never starts this check
automatically. It searches only real private IPv4 addresses on the current PC,
not `localhost` or `127/8`, and separately reports these three scopes:

1. Agent service, TCP/18443, HTTPS, API, and version are reachable.
2. Switch access has not yet been tested and remains under
   `장비 관리 → 접속 시험`.
3. A same-PC success does not prove the route or firewall from the actual
   remote Viewer PC.

Node `46:72` remains the source of truth for the Agent Setup
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
management-network behavior. The current `같은 PC 시험용 주소` action fills a single
detected private IPv4 immediately or lets the operator select among multiple
private IPv4 candidates. If no suitable address exists, Setup shows actionable
feedback instead of inventing a loopback address. Before remote deployment, the
operator must run Setup again with the actual Viewer PC fixed IPv4 so the Agent
API and product-owned Windows Firewall rule return to the intended exact `/32`
scope.

The v8 screen preserves the v7 firewall behavior: another program's inbound
TCP/18443 Allow rule is shown as a warning and is never changed or removed.
The existing `설치 / 업데이트` flow creates the product-owned Viewer `/32`
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

Node `33:205` keeps the three-column operational dashboard while adding clear
entry points for Agent connection and device management. It explicitly states
that monitoring is Viewer-owned and stops when the Viewer is closed.

## v11 Agent Setup recovery flow

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

## v12 sanitized field diagnostic flow

Nodes `60:340` and `62:99` are the source of truth for manually saved field
diagnostics.

1. Agent Setup and Viewer never save a diagnostic automatically.
2. `익명 진단 저장` appears only after a check has completed, whether it
   succeeded or failed.
3. The file uses the versioned `SSW_FIELD_DIAGNOSTIC/1` text contract and
   UTF-8 with BOM so it opens correctly on Korean Windows.
4. Stable stage, result and error codes plus bounded stage timings are
   retained. IP/CIDR, PC and user names, credentials, certificate details,
   paths, raw firewall data, exception text and switch command output are
   excluded.
5. `같은 PC 시험용 주소` and
   `Agent와 Viewer가 같은 PC일 때 테스트` make the local test boundary
   explicit. A same-PC success still does not prove the remote Viewer route or
   switch access.
6. Existing failure-only clipboard diagnostics remain available in Agent
   Setup. The new TXT action does not replace them.

Node `60:340` covers the Agent Setup completed-check state and node `62:99`
covers the Viewer connection-check state. Both preserve the established
compact WPF layout and Noto Sans KR Figma typography.

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
