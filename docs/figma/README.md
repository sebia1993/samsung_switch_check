# Figma handoff

- File: [Samsung Switch Watch](https://www.figma.com/design/JueYiLj18xFE7enHvGlU2s)
- Target: Windows WPF, 1440x900 dashboard, minimum 1280x720
- Current simplified dashboard: node `25:138`
- Current HTTP Agent connection screen: node `25:214`
- UX audit board: page `24:2`, root frame `24:3`
- Previous operations dashboard (reference): node `11:64`
- Mini window: 400x260 offline/recovery frame, node `14:127`
- Popup: 360x150 content frame
- Popup state strip: node `15:129`
- Operational and security state gallery: node `15:148`
- Historical v3 single-string pairing wizard: node `20:129`
- Historical-flow warning banner: node `29:205`
- Command capability and automatic fallback: node `22:131`
- Figma font: Noto Sans KR
- Windows implementation font: Segoe UI

Pages:

1. Cover
2. Foundations
3. Components
4. Screens
5. 05 UX Audit

WPF has no supported Figma Code Connect framework label, and this POC file is not a published library. Therefore handoff uses Figma variables, component descriptions, node IDs, screenshots, and `get_design_context`; no false Code Connect mapping is created.

The v2 handoff adds the three supported switch models, per-device capability health,
authoritative event counts, catch-up/recovery states, Agent-offline semantics, pairing,
certificate, real-time channel, and database-integrity states. All text nodes use
Noto Sans KR (Roboto Mono is allowed for code-only content), no text overflows its
parent bounds, and the screens reuse the tracked local variables and components.

The v3 handoff replaced manual fingerprint/token entry with one `SSW1:` pairing string
and documented preferred, fallback, and unsupported command states.

The user-approved v4 implementation removes the certificate fingerprint, pairing code,
`SSW1:` string, and Bearer token. The WPF connection window now contains only an Agent
IPv4/DNS field, an HTTP port field (default `18443`), and the explicit warning
`사내 관리망 전용 · 암호화/인증 없음`. The command-capability screen remains valid.

The v4 HTTP connection flow is synchronized at node `25:214`. It contains only the Agent
address, HTTP port, internal-management-network warning, demo option, tray-start option,
and the two actions. Node `20:129` is retained for history only and is explicitly marked
with warning node `29:205`; certificate fingerprints, pairing tokens, `SSW1:` strings,
and Bearer-token input must not be reintroduced into the current connection flow.

The v5 operator simplification is synchronized at node `25:138`. It keeps the three-column
dashboard, uses Korean check names with explicit `대`/`건` units, distinguishes device
disconnection from switch faults, and places protocol/API/command detail behind `수집 진단`.
The editable frame reuses the existing local variables and component sets.

The `05 UX Audit` page records beginner and administrator scorecards, six sanitized WPF
evidence screenshots, P0-P3 improvement priorities, the five-step target operator flow,
and explicit non-goals. The 2026-07-22 Figma QA found zero missing fonts and zero text-bound
overflows in nodes `24:3`, `25:138`, and `25:214`; all six evidence image fills were present.
No Agent API, database, collection, alert behavior, or executable source changed during this
Figma synchronization.
