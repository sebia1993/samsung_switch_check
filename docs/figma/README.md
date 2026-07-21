# Figma handoff

- File: [Samsung Switch Watch](https://www.figma.com/design/JueYiLj18xFE7enHvGlU2s)
- Target: Windows WPF, 1440x900 dashboard, minimum 1280x720
- Approved operations dashboard: node `11:64`
- Mini window: 400x260 offline/recovery frame, node `14:127`
- Popup: 360x150 content frame
- Popup state strip: node `15:129`
- Operational and security state gallery: node `15:148`
- Single-string pairing wizard: node `20:129`
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

The v3 handoff replaces manual fingerprint/token entry with one `SSW1:` pairing string
and documents preferred, fallback, and unsupported command states. The WPF connection
window and collector-health tab implement those operator flows while preserving the
manual fields as an advanced recovery path.
