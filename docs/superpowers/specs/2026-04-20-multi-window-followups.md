# Multi-Window Followups — 2026-04-20

**Context:** Multi-window landed tonight. Two-panel grid validated end-to-end
on Galaxy Z Fold 6 (portable flavor) — simultaneous streams of two Windows
windows rendered into a 1×2 `GridLayout` inside a single `DemoActivity`.
One APK instance hosts N parallel pipelines (one `ControlClient` +
`UdpTransportReceiver` + `MediaCodecDecoder` per panel). This doc captures
known gaps + the direction for the next session.

## Input routing

**Current:** keyboard (soft + physical) always routes to `streamStates[0]`
via `sendKeyEventToPrimary`. There's no way to target the secondary panel.

**Desired UX:** tap-to-focus — tap a panel to make it the active input
target; visible highlight on the focused panel (colored border or tint).

**Implementation sketch:**
- Add `@Volatile var activeStreamIndex: Int = 0` on `DemoActivity`.
- Per-panel touch handler: set `activeStreamIndex` + invalidate highlight.
- Render a 4-dp `View` child in the grid cell wrapping each `SurfaceView`,
  toggle its background between transparent / accent-colored.
- Route `sendKeyEventToPrimary` → `sendKeyEventToActive` targeting
  `streamStates[activeStreamIndex].connection`.
- Existing click handler on root `FrameLayout` (toggles soft keyboard) must
  not conflict — use `ViewGroup.setOnHierarchyChangeListener` or have the
  panel-focus view consume `ACTION_DOWN` and not `ACTION_UP`, letting the
  root still get the ACTION_UP "tap" for keyboard toggle.

## Soft-keyboard polish

1. **Enter doesn't clear input.** After typing `foo`+Enter, the hidden
   `EditText` retains `foo`; next char appends, so `TextWatcher` relays
   `foo` + new char instead of just the new char.

   Fix: in the `TextWatcher.afterTextChanged` (or a separate `onKeyListener`
   on the EditText), detect KEYCODE_ENTER / unicode `\n` → `softInputEditText
   .setText("")` + reset `previousSoftInputLength = 0`. Relay the Enter
   keystroke first, then clear.

2. **Backspace no-op on empty buffer.** `TextWatcher` only fires when text
   shrinks. When the EditText is empty, backspace produces no text-change
   event → no relay. Symptom the user hit first.

   Fix: `softInputEditText.setOnKeyListener` intercepts `KEYCODE_DEL` and
   relays `VK_BACK` regardless of buffer state. Keeps TextWatcher for
   printable chars.

3. **Landscape layout awkwardness.** `windowSoftInputMode="adjustResize"`
   is set but the SurfaceView doesn't always shrink cleanly above the IME,
   particularly in landscape where the keyboard covers ~half the screen.

   Ideas:
   - Wrap in `ConstraintLayout` with explicit `fitsSystemWindows="true"`
     and an IME-aware inset.
   - Switch the preview bar from `Gravity.TOP` (pinned) to docked just-above
     the keyboard via `WindowInsetsCompat.Type.ime()`.
   - Evaluate `TextureView` vs `SurfaceView` for smoother resize.

   **Priority:** phone use case is "a flex," not primary. HMD + BT keyboard
   is the real UX. Polish only on demand.

4. **Physical BT backspace tested with a crappy keyboard.** `KEYCODE_DEL`
   is already in `dispatchKeyEvent`'s translation table. May be a keyboard
   issue rather than an app issue. Re-test with a known-good BT keyboard
   before digging in.

## Visual / UX polish

- **Per-panel identity.** Grid panels have no labels — hard to tell two
  terminals apart at a glance. Overlay the server hostname (from
  `ServerInformation.hostname`) on each panel corner.
- **Loading states.** Panel shows black until the first keyframe arrives.
  Add a "connecting to $hostname…" text while waiting.

## Performance / latency (next session focus)

- **Viewer MediaCodec:** set
  `MediaFormat.KEY_LOW_LATENCY = 1` (API 30+) on the decoder before
  `configure()`. Expected: 60–100 ms shave.
- **Server NVENC:** switch to preset `p1` (lowest latency) + `-tune ull`
  (ultra-low-latency). Need to check `FFmpegNvencEncoder` to see which
  knobs are currently hardcoded vs. configurable via `EncoderOptions`.
  Expected: 30–60 ms shave.
- **End-to-end latency measurement path:** stamp frame timestamp on the
  capture side, thread it through encode → transport → decode, log the
  time-to-first-render every N frames. Optional `--measure` CLI flag.
- **Input RTT measurement:** round-trip a synthetic key event through
  `Win32InputInjector` → a listener in the captured window → back to the
  server as a control-channel pong. Report avg / p50 / p99.
- **Network profile:** Wireshark a session, check for retransmissions,
  packet-size clipping at the IP MTU, bursty-vs-smooth send pattern.
- **Target budget:** current end-to-end latency is qualitatively "janky"
  (hundreds of ms). Target: sub-100 ms frame RTT for remote-desktop feel;
  sub-50 ms input-relay RTT for typing.
- **Diagnosis order:** before writing any fix, measure first. Don't guess
  which link in the chain dominates.

## Multi-window structural followups

- **GXR flavor (Jetpack XR immersive):** multi-panel support requires N
  `SpatialExternalSurface` entities in `WindowStreamScene`. Separate
  codepath from `DemoActivity`. Worth doing once the 2D multi-window UX
  has been validated and we know what to mirror.
- **One-server-many-streams:** the current approach is N servers for N
  streams. The real design has `ActiveStreamDescriptor` singular in
  `ServerHello`; a future protocol revision could let one server advertise
  multiple streams. Benefit: one firewall rule set, one mDNS advertisement,
  simpler "serve the whole desktop" story. Cost: protocol break.
- **Server-side odd-dimension sws_scale bug.** Two candidate source windows
  tonight (Task Manager, Discord) both had odd-height capture frames and
  crashed the encode pump with `sws_scale failed`. Pre-existing bug;
  `SessionHostLauncherAdapter.ProbeCaptureSizeAsync` aligns dims down but
  doesn't crop the live frame. Fix: reconcile src/dst dims in
  `FFmpegNvencEncoder`, either by cropping the incoming frame or by
  configuring `sws_scale` with raw src dims and aligned dst dims.
- **Firewall rule proliferation.** Every session creates two new port-based
  rules. Replace with a single binary-based allow rule on the published
  `windowstream.exe` (and a dev rule on `dotnet.exe` if running
  unpublished).
