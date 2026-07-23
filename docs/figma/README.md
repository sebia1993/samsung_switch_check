# Figma handoff

- File: [Samsung Switch Watch](https://www.figma.com/design/JueYiLj18xFE7enHvGlU2s)
- Target: Windows WPF, 1440x900 dashboard, minimum 1280x720
- Current Viewer dashboard: node `33:205`
- Current device-management dialog: node `37:4`
- Current device-command dashboard: node `37:333`
- Current automatic HTTPS Agent connection dialog: node `36:2`
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

Node `36:2` replaces the historical fingerprint, pairing-token, `SSW1:`
pairing, and Bearer-token flows. The current dialog contains only the Agent
address and fixed HTTPS port `18443`; access control belongs to the management
network and Windows Firewall rules.

Node `33:205` keeps the three-column operational dashboard while adding clear
entry points for Agent connection and device management. It explicitly states
that monitoring is Viewer-owned and stops when the Viewer is closed.

## Historical references

- Previous simplified dashboard v5: node `25:138`
- Previous HTTP connection v4: node `25:214`
- Historical v3 pairing wizard: node `20:129`
- Historical-flow warning banner: node `29:205`
- Previous command capability and fallback screen: node `22:131`
- Previous operations dashboard: node `11:64`

Historical frames remain for decision traceability only. Certificate
fingerprints, pairing tokens, `SSW1:` strings, Bearer-token input, Agent-side
device credentials, and Agent-side monitoring schedules must not be
reintroduced into the current flow.
