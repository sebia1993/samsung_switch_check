# Figma handoff

- File: [Samsung Switch Watch](https://www.figma.com/design/JueYiLj18xFE7enHvGlU2s)
- Target: Windows WPF, 1440x900 dashboard, minimum 1280x720
- Approved operations dashboard: node `11:64`
- Mini window: 400x260 offline/recovery frame, node `14:127`
- Popup: 360x150 content frame
- Popup state strip: node `15:129`
- Operational and security state gallery: node `15:148`
- Historical v3 single-string pairing wizard: node `20:129`
- Command capability and automatic fallback: node `22:131`
- Figma font: Noto Sans KR
- Windows implementation font: Segoe UI

Pages:

1. Cover
2. Foundations
3. Components
4. Screens

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

The Figma write connection was unavailable during the v0.6.0-poc implementation, so the
actual v4 HTTP connection frame has not been written to the Figma file yet. Node `20:129`
is historical and must not be used as the current connection-flow source. Until the Figma
frame is synchronized, the user-confirmed v4 flow above and the WPF implementation are the
approved exception recorded by this handoff.
