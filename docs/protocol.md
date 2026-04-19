# WindowStream Protocol

**Status**: Stub — authoritative content will be fleshed out during implementation.

This document will be the single source of truth for the wire protocol between `WindowStreamServer`/`WindowStream.Cli` (.NET) and `WindowStreamViewer` (Kotlin/Android XR). Both implementations reference this document.

For v1 the design is captured in the approved design spec:
[`docs/superpowers/specs/2026-04-19-windowstream-design.md`](superpowers/specs/2026-04-19-windowstream-design.md)

See the **Protocol (Authoritative)** section of the design spec for:
- mDNS service type and TXT records
- TCP control channel framing and message types
- UDP video channel packet layout
- Error codes
- Keyframe policy

Once implementation begins, this document takes over as the canonical reference, and the design spec becomes a historical record of the decision process.
