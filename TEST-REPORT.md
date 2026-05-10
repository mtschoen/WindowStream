WindowStream test report — 2026-05-09T12:55:49Z
═══════════════════════════════════════════

Status:   PASS
Tests:    344 total (306 unit + 38 integration; 3 hardware-gated skips in integration)
Git:      d356ead (feature/m5-cleanup — M5 cleanup + 100/100 gate restored)
Coverage: 100% line / 100% branch / 100% method on both WindowStream.Core
          and the windowstream CLI module
          0 lines uncovered
          Exclusion annotations (with rationale, kept for native I/O):
            - FFmpegNvencEncoder native FFmpeg call paths (Phase 12 integration tests)
            - TcpConnectionAcceptorAdapter (native socket wrapper; FakeTcp* covers behaviour)
            - TcpControlChannelAdapter (TCP stream wrapper; framing + serialization tested in isolation)
            - UdpVideoSenderAdapter (UDP socket wrapper; FakeUdp* + PacketHeader cover framing)
            - CliServices.CreateDefault (real-hardware DI wiring; constructor itself is tested)

## Per-suite

| Suite                                  | Tests | Skipped | Result |
|----------------------------------------|------:|--------:|--------|
| WindowStream.Core.Tests (xUnit)        |   306 |       0 | PASS   |
| WindowStream.Integration.Tests (xUnit) |    38 |       3 | PASS   |

Integration-test skips are hardware-gated: NVENC-dependent tests skip
when no NVIDIA driver is available, the mDNS loopback test skips when
multicast loopback is blocked, and the focus-relay test skips when
Notepad cannot be launched non-interactively.

## What changed at this report

The 100% line+branch coverage gate was relaxed to 90/85 in M2 (commit
`a708734`) for the GPU-resident pipeline transition window. M5 restores
100/100 by:

1. Marking the pure native-socket adapters (`TcpConnectionAcceptorAdapter`,
   `TcpControlChannelAdapter`, `UdpVideoSenderAdapter`) as
   `[ExcludeFromCodeCoverage]` with rationale, matching the existing
   pattern on `FFmpegNvencEncoder` native paths.
2. Adding focused unit tests for the small v2-era gaps (`CliServices`
   constructor + null guards, `WorkerArguments` record, `IControlChannel`
   default `RemoteIpAddress` impl, `WorkerSupervisor.GetPipe`,
   `FakeVideoEncoder.Stopped`, `StreamStoppedReasonConverter` null path).
3. Restoring `<Threshold>100,100</Threshold>` in
   `tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj` and
   removing the `TEMPORARY: M5 restores` marker.

End-to-end correctness of the GPU-resident pipeline is verified by
`FFmpegNvencEncoderHwaccelTests` (4 resolution × encode-then-decode
round-trips at 640×360, 800×450, 960×540, 1120×630 — all PASS) and
`WorkerProcessIntegrationTests.WorkerEmitsChunksThroughPipe`. The M5 #3
encoder PTS + wallMs alignment did not regress any integration test.

The M5 manual-smoke checkpoint (live demo, Unity 4K → Galaxy XR, capture
[FRAMECOUNT] data, record cap → present p50/p95 in the design spec's
Measured results section) is the remaining deliverable; it does not
affect the test report.
