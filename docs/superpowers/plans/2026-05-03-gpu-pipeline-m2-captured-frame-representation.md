# M2 — `CapturedFrame` Discriminated Representation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend `CapturedFrame` with an additive native-texture representation alongside the existing managed-byte buffer, and confirm `FakeVideoEncoder` continues to work with both representations. No producer creates texture frames yet — that lands in M3. No consumer branches on representation — that lands in M4.

**Architecture:** Add a `FrameRepresentation` discriminator enum (`Bytes` | `Texture`) and two new properties — `nativeTexturePointer` (`nint`, 0 when `representation == Bytes`) and `textureArrayIndex` (`int`, 0 when `representation == Bytes`) — to the existing `CapturedFrame`. Add two static factories: `FromBytes(...)` (preserving the existing constructor's validation and contract) and `FromTexture(...)` (new path). Keep the existing public constructor in place during the transition so no callers need to change in M2; M5 will scope it to internal once production code only constructs texture frames. `FakeVideoEncoder` requires no functional change (it only reads `presentationTimestampMicroseconds`); a unit test pins the cross-representation behaviour so M3+ work doesn't regress it.

**Tech Stack:** C# 12, .NET 8, xUnit, Coverlet. All changes are TFM-agnostic — `CapturedFrame` lives in the multi-targeted `WindowStream.Core` library and the existing `WindowStream.Core.Tests` project (`net8.0`) covers it.

---

## File structure

**Modify:**
- `src/WindowStream.Core/Capture/CapturedFrame.cs` — add `FrameRepresentation` enum, two new properties, `FromBytes` and `FromTexture` static factories, and texture-path validation.
- `tests/WindowStream.Core.Tests/Capture/CapturedFrameTests.cs` — add tests covering the texture path, discriminator, and factory equivalence.
- `tests/WindowStream.Core.Tests/Encode/Testing/FakeVideoEncoderTests.cs` — add a single regression test that `EncodeAsync` works with a texture-representation frame.
- `tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj` — relax the coverage gate to `Threshold=90` line / `Threshold=85` branch per spec's "Coverage gate strategy during transition" section.

**Untouched (verified):** `WgcCapture.cs`, `WgcCaptureSource.cs`, `WgcFrameConverter.cs`, `FFmpegNvencEncoder.cs`, `IVideoEncoder.cs`, `EncoderOptions.cs`, `EncodedChunk.cs`, all hosting code, all CLI code, all viewer code, all test fakes other than `FakeVideoEncoder`. M3 modifies `WgcFrameConverter` to start producing texture frames; M4 modifies `FFmpegNvencEncoder` to consume them.

**Spec deviation: coverage gate location.** The spec ("Coverage gate strategy during transition") says thresholds live in `Directory.Build.props`. They actually live in `tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj` per the project's CLAUDE.md (the .NET 10 SDK doesn't honor a conditional `IsTestProject` block in DBP early enough for VSTest). The plan honours the spec's intent — gate stays on, thresholds relaxed for M2–M4 — at the location where it actually takes effect. Spec is otherwise unchanged.

---

## Task 1: Add `FrameRepresentation` enum and texture-path support to `CapturedFrame`

Adds the discriminator and the texture path to the existing class. The bytes constructor stays public and keeps its current behaviour bit-for-bit so no existing caller breaks. Two static factories (`FromBytes`, `FromTexture`) wrap the corresponding construction paths — the spec's documented entry points.

**Files:**
- Modify: `src/WindowStream.Core/Capture/CapturedFrame.cs`

- [ ] **Step 1: Replace the file contents**

Open `src/WindowStream.Core/Capture/CapturedFrame.cs`. Replace the entire file with:

```csharp
using System;

namespace WindowStream.Core.Capture;

public enum FrameRepresentation
{
    Bytes,
    Texture,
}

public sealed class CapturedFrame
{
    public int widthPixels { get; }
    public int heightPixels { get; }
    public int rowStrideBytes { get; }
    public PixelFormat pixelFormat { get; }
    public long presentationTimestampMicroseconds { get; }
    public FrameRepresentation representation { get; }
    public ReadOnlyMemory<byte> pixelBuffer { get; }
    public nint nativeTexturePointer { get; }
    public int textureArrayIndex { get; }

    /// <summary>
    /// Construct a managed-byte (CPU-resident) <see cref="CapturedFrame"/>.
    /// During the GPU-resident pipeline transition this is the only path
    /// production code uses; M5 scopes this constructor to internal once
    /// production code constructs texture frames exclusively.
    /// </summary>
    public CapturedFrame(
        int widthPixels,
        int heightPixels,
        int rowStrideBytes,
        PixelFormat pixelFormat,
        long presentationTimestampMicroseconds,
        ReadOnlyMemory<byte> pixelBuffer)
    {
        ValidateCommon(widthPixels, heightPixels, rowStrideBytes, pixelFormat, presentationTimestampMicroseconds);

        long expectedLength = pixelFormat == PixelFormat.Nv12
            ? (long)rowStrideBytes * heightPixels * 3 / 2
            : (long)rowStrideBytes * heightPixels;
        if (pixelBuffer.Length < expectedLength)
        {
            throw new ArgumentException(
                "pixelBuffer is smaller than widthPixels * heightPixels for the declared stride and format.",
                nameof(pixelBuffer));
        }

        this.widthPixels = widthPixels;
        this.heightPixels = heightPixels;
        this.rowStrideBytes = rowStrideBytes;
        this.pixelFormat = pixelFormat;
        this.presentationTimestampMicroseconds = presentationTimestampMicroseconds;
        this.representation = FrameRepresentation.Bytes;
        this.pixelBuffer = pixelBuffer;
        this.nativeTexturePointer = 0;
        this.textureArrayIndex = 0;
    }

    private CapturedFrame(
        int widthPixels,
        int heightPixels,
        int rowStrideBytes,
        PixelFormat pixelFormat,
        long presentationTimestampMicroseconds,
        nint nativeTexturePointer,
        int textureArrayIndex)
    {
        ValidateCommon(widthPixels, heightPixels, rowStrideBytes, pixelFormat, presentationTimestampMicroseconds);

        if (nativeTexturePointer == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nativeTexturePointer), "Texture pointer must be non-zero.");
        }
        if (textureArrayIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(textureArrayIndex), "Texture array index must be non-negative.");
        }

        this.widthPixels = widthPixels;
        this.heightPixels = heightPixels;
        this.rowStrideBytes = rowStrideBytes;
        this.pixelFormat = pixelFormat;
        this.presentationTimestampMicroseconds = presentationTimestampMicroseconds;
        this.representation = FrameRepresentation.Texture;
        this.pixelBuffer = ReadOnlyMemory<byte>.Empty;
        this.nativeTexturePointer = nativeTexturePointer;
        this.textureArrayIndex = textureArrayIndex;
    }

    /// <summary>
    /// Construct a managed-byte (CPU-resident) <see cref="CapturedFrame"/>.
    /// Equivalent to the public constructor; provided as the documented
    /// factory entry point for the GPU-resident pipeline transition (paired
    /// with <see cref="FromTexture"/>).
    /// </summary>
    public static CapturedFrame FromBytes(
        int widthPixels,
        int heightPixels,
        int rowStrideBytes,
        PixelFormat pixelFormat,
        long presentationTimestampMicroseconds,
        ReadOnlyMemory<byte> pixelBuffer) =>
        new CapturedFrame(
            widthPixels,
            heightPixels,
            rowStrideBytes,
            pixelFormat,
            presentationTimestampMicroseconds,
            pixelBuffer);

    /// <summary>
    /// Construct a native-texture (GPU-resident) <see cref="CapturedFrame"/>.
    /// <paramref name="nativeTexturePointer"/> is an <c>ID3D11Texture2D*</c>
    /// owned by the producer; the consumer is responsible for honouring the
    /// producer's release contract (the encoder's <c>hw_frames_ctx</c> pool
    /// in the post-M4 pipeline). <paramref name="textureArrayIndex"/> is the
    /// subresource index within the texture array (0 for non-array textures).
    /// </summary>
    public static CapturedFrame FromTexture(
        int widthPixels,
        int heightPixels,
        int rowStrideBytes,
        PixelFormat pixelFormat,
        long presentationTimestampMicroseconds,
        nint nativeTexturePointer,
        int textureArrayIndex) =>
        new CapturedFrame(
            widthPixels,
            heightPixels,
            rowStrideBytes,
            pixelFormat,
            presentationTimestampMicroseconds,
            nativeTexturePointer,
            textureArrayIndex);

    private static void ValidateCommon(
        int widthPixels,
        int heightPixels,
        int rowStrideBytes,
        PixelFormat pixelFormat,
        long presentationTimestampMicroseconds)
    {
        if (widthPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthPixels));
        }
        if (heightPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(heightPixels));
        }

        int minimumStride = pixelFormat switch
        {
            PixelFormat.Bgra32 => widthPixels * 4,
            PixelFormat.Nv12 => widthPixels,
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat)),
        };
        if (rowStrideBytes < minimumStride)
        {
            throw new ArgumentOutOfRangeException(nameof(rowStrideBytes));
        }
        if (presentationTimestampMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(presentationTimestampMicroseconds));
        }
    }
}
```

- [ ] **Step 2: Verify it compiles on both TFMs**

Run: `dotnet build src/WindowStream.Core/WindowStream.Core.csproj`
Expected: success (both `net8.0` and `net8.0-windows10.0.19041.0` build), no warnings, no errors.

- [ ] **Step 3: Run the existing `CapturedFrameTests` and `CapturedFrameNv12Tests` to confirm the byte path is unchanged**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~CapturedFrame" --no-restore`
Expected: all existing tests still pass (the public constructor's behaviour is preserved bit-for-bit; only the post-construction property set has expanded).

If a test fails because total coverage dropped below the gate, that is expected at this point — Task 4 lowers the gate. The test report itself should still show all `CapturedFrame*` tests passing.

- [ ] **Step 4: Do not commit yet** — Tasks 2 and 3 add the tests that exercise the new code; commit them together so the bytes-path-only intermediate state never lands.

---

## Task 2: Add unit tests for the texture path and discriminator on `CapturedFrame`

Tests prove (a) `FromTexture` populates all fields including the discriminator, (b) the bytes-path discriminator is set correctly via both the constructor and `FromBytes`, (c) texture-path validation rejects a zero pointer and a negative array index, (d) common validation (dimensions / stride / timestamp / format) fires the same way on the texture path as on the bytes path.

**Files:**
- Modify: `tests/WindowStream.Core.Tests/Capture/CapturedFrameTests.cs`

- [ ] **Step 1: Append the new tests to the end of the existing test class**

Open `tests/WindowStream.Core.Tests/Capture/CapturedFrameTests.cs`. Just before the closing `}` of the `CapturedFrameTests` class, insert:

```csharp
    [Fact]
    public void Constructor_BytesPath_SetsRepresentationToBytes()
    {
        WindowStream.Core.Capture.CapturedFrame frame = new WindowStream.Core.Capture.CapturedFrame(
            1, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, new byte[4]);
        Assert.Equal(WindowStream.Core.Capture.FrameRepresentation.Bytes, frame.representation);
        Assert.Equal((nint)0, frame.nativeTexturePointer);
        Assert.Equal(0, frame.textureArrayIndex);
    }

    [Fact]
    public void FromBytes_IsEquivalentToConstructor()
    {
        byte[] buffer = new byte[8];
        WindowStream.Core.Capture.CapturedFrame frame = WindowStream.Core.Capture.CapturedFrame.FromBytes(
            widthPixels: 2,
            heightPixels: 1,
            rowStrideBytes: 8,
            pixelFormat: WindowStream.Core.Capture.PixelFormat.Bgra32,
            presentationTimestampMicroseconds: 42,
            pixelBuffer: buffer);
        Assert.Equal(WindowStream.Core.Capture.FrameRepresentation.Bytes, frame.representation);
        Assert.Equal(buffer.Length, frame.pixelBuffer.Length);
        Assert.Equal(42L, frame.presentationTimestampMicroseconds);
    }

    [Fact]
    public void FromTexture_PopulatesAllProperties()
    {
        WindowStream.Core.Capture.CapturedFrame frame = WindowStream.Core.Capture.CapturedFrame.FromTexture(
            widthPixels: 1920,
            heightPixels: 1080,
            rowStrideBytes: 1920,
            pixelFormat: WindowStream.Core.Capture.PixelFormat.Nv12,
            presentationTimestampMicroseconds: 1_000_000,
            nativeTexturePointer: (nint)0xDEADBEEF,
            textureArrayIndex: 3);

        Assert.Equal(WindowStream.Core.Capture.FrameRepresentation.Texture, frame.representation);
        Assert.Equal(1920, frame.widthPixels);
        Assert.Equal(1080, frame.heightPixels);
        Assert.Equal(1920, frame.rowStrideBytes);
        Assert.Equal(WindowStream.Core.Capture.PixelFormat.Nv12, frame.pixelFormat);
        Assert.Equal(1_000_000L, frame.presentationTimestampMicroseconds);
        Assert.Equal((nint)0xDEADBEEF, frame.nativeTexturePointer);
        Assert.Equal(3, frame.textureArrayIndex);
        Assert.Equal(0, frame.pixelBuffer.Length);
    }

    [Fact]
    public void FromTexture_RejectsZeroPointer()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowStream.Core.Capture.CapturedFrame.FromTexture(
                1, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, (nint)0, 0));
    }

    [Fact]
    public void FromTexture_RejectsNegativeArrayIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowStream.Core.Capture.CapturedFrame.FromTexture(
                1, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, (nint)1, -1));
    }

    [Fact]
    public void FromTexture_RejectsNonPositiveDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowStream.Core.Capture.CapturedFrame.FromTexture(
                0, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, (nint)1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowStream.Core.Capture.CapturedFrame.FromTexture(
                1, 0, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, (nint)1, 0));
    }

    [Fact]
    public void FromTexture_RejectsStrideSmallerThanRow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowStream.Core.Capture.CapturedFrame.FromTexture(
                10, 2, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, (nint)1, 0));
    }

    [Fact]
    public void FromTexture_RejectsNegativeTimestamp()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowStream.Core.Capture.CapturedFrame.FromTexture(
                1, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, -1, (nint)1, 0));
    }
```

- [ ] **Step 2: Run the updated test class**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~CapturedFrameTests" --no-restore`
Expected: all tests in `CapturedFrameTests` pass — the original 6 plus the 8 new ones (14 total).

- [ ] **Step 3: Do not commit yet** — Task 3's `FakeVideoEncoder` regression test is part of the same logical change.

---

## Task 3: Add a `FakeVideoEncoder` regression test for the texture representation

`FakeVideoEncoder.EncodeAsync` only reads `frame.presentationTimestampMicroseconds` from the frame, so functionally it already accepts texture frames without code changes. This test pins that fact so M3+ work cannot regress it (and so the spec's "FakeVideoEncoder accepts both representations" requirement is verifiable from the test suite).

**Files:**
- Modify: `tests/WindowStream.Core.Tests/Encode/Testing/FakeVideoEncoderTests.cs`

- [ ] **Step 1: Add a single test at the end of the class**

Open `tests/WindowStream.Core.Tests/Encode/Testing/FakeVideoEncoderTests.cs`. Just before the closing `}` of the `FakeVideoEncoderTests` class, insert:

```csharp
    [Fact]
    public async Task EncodeAsync_AcceptsTextureRepresentationFrame()
    {
        await using FakeVideoEncoder encoder = new FakeVideoEncoder();
        encoder.Configure(new EncoderOptions(2, 2, 30, 1_000_000, 30, 2));

        CapturedFrame textureFrame = CapturedFrame.FromTexture(
            widthPixels: 2,
            heightPixels: 2,
            rowStrideBytes: 2,
            pixelFormat: PixelFormat.Nv12,
            presentationTimestampMicroseconds: 12_345,
            nativeTexturePointer: (nint)0xCAFEBABE,
            textureArrayIndex: 0);

        await encoder.EncodeAsync(textureFrame, CancellationToken.None);
        encoder.CompleteEncoding();

        List<EncodedChunk> chunks = new List<EncodedChunk>();
        await foreach (EncodedChunk chunk in encoder.EncodedChunks)
        {
            chunks.Add(chunk);
        }
        Assert.Single(chunks);
        Assert.Equal(12_345L, chunks[0].presentationTimestampMicroseconds);
    }
```

- [ ] **Step 2: Run the updated test class**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~FakeVideoEncoderTests" --no-restore`
Expected: all tests in `FakeVideoEncoderTests` pass — the original 8 plus the 1 new one (9 total).

- [ ] **Step 3: Do not commit yet** — Task 4's coverage-gate relaxation is part of the same milestone landing.

---

## Task 4: Relax the coverage gate per the spec's transition strategy

Spec: `WindowStream.Core` line coverage 100% → 90% (M2 entry), branch coverage 100% → 85% (M2 entry), restored in M5. As of 2026-05-03 the gate has been red on `main` (94.01% line / 89.85% branch overall — see memory `project_coverage_gate_red_on_main.md`); 90/85 turns it green and codifies the transition window. Place a `<!-- TEMPORARY: M5 restores -->` style comment on each adjusted line so the restore at M5 is unmissable.

The thresholds live in `tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj`, NOT `Directory.Build.props` — see the spec-deviation note in the file-structure section.

**Files:**
- Modify: `tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj`

- [ ] **Step 1: Replace the threshold lines**

Open `tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj`. Find:

```xml
    <Threshold>100</Threshold>
    <ThresholdType>line,branch</ThresholdType>
    <ThresholdStat>total</ThresholdStat>
```

Replace with:

```xml
    <!-- TEMPORARY: M5 of the GPU-resident pipeline transition restores 100/100. See spec section "Coverage gate strategy during transition". -->
    <Threshold>90,85</Threshold>
    <ThresholdType>line,branch</ThresholdType>
    <ThresholdStat>total</ThresholdStat>
```

Note: Coverlet 6.0.2's MSBuild integration accepts `<Threshold>` as a comma-separated list whose positions match the entries in `<ThresholdType>`. So `<Threshold>90,85</Threshold>` + `<ThresholdType>line,branch</ThresholdType>` enforces 90% line and 85% branch.

- [ ] **Step 2: Run the full unit-test suite to confirm the gate passes**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj`
Expected: all 311 tests pass (302 prior + 8 new in `CapturedFrameTests` + 1 new in `FakeVideoEncoderTests`), and the coverage gate reports the 90/85 thresholds satisfied. Final summary line should be `Passed!  - Failed: 0, Passed: 311, Skipped: 0, Total: 311`.

If coverage now drops *below* 90/85 (the new code in Task 1 is mostly covered by Task 2's tests, but a single missed branch could push `WindowStream.Core` under 90), inspect the coverlet report — `tests/WindowStream.Core.Tests/coverage.cobertura.xml` — and add the missing test cases to `CapturedFrameTests` rather than relaxing the threshold further. The 90/85 numbers are the spec's contract for the transition window and should not slip.

- [ ] **Step 3: Commit Tasks 1 + 2 + 3 + 4 as a single atomic landing**

```bash
git add src/WindowStream.Core/Capture/CapturedFrame.cs \
        tests/WindowStream.Core.Tests/Capture/CapturedFrameTests.cs \
        tests/WindowStream.Core.Tests/Encode/Testing/FakeVideoEncoderTests.cs \
        tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj
git commit -m "feat(capture): CapturedFrame discriminated representation (M2)

Adds FrameRepresentation enum and FromTexture factory alongside the
existing bytes-path constructor and FromBytes factory. No producer
creates texture frames yet — that lands in M3. No consumer branches on
representation — that lands in M4.

Relaxes the coverage gate to line=90/branch=85 per the spec's
transition strategy (M5 restores 100/100).
"
```

---

## Task 5: Run integration suite to confirm no regression

The `CapturedFrame` change is purely additive — public constructor signature unchanged, all existing properties preserved, no callers updated. Integration tests should pass with no behavioural difference.

- [ ] **Step 1: Run the full integration test suite**

Run: `dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj`
Expected: all 18 tests in the same shape as M1's Task 7 — 15 pass, 3 expected skips (mDNS loopback, focus relay, placeholder).

- [ ] **Step 2: No commit needed for verification — proceed to Task 6.**

---

## Task 6: Wrap-up

- [ ] **Step 1: Confirm working tree is clean**

Run: `git status`
Expected: nothing to commit, working tree clean.

- [ ] **Step 2: Confirm the M2 commit is on the branch**

Run: `git log --oneline -3`
Expected: the Task 4 commit is at HEAD; the M1 commits (`98a620b`, `99e6e70`, `96c3a6c`, `ba1c14e`) precede it.

- [ ] **Step 3: M2 done.** Hand off to user for review and decision to proceed to M3.

---

## Self-review notes

- **Spec coverage:** M2's four spec bullets — "Add native-texture path to `CapturedFrame` alongside bytes" (Task 1), "Add unit tests for both constructors and the discriminator" (Task 2: 8 new tests covering both factories, the discriminator state for both paths, texture-pointer/array-index validation, and common-validation parity), "Update `FakeVideoEncoder` to accept both representations" (Task 3: regression test pinning the cross-representation behaviour — no `FakeVideoEncoder` code change needed because it only reads `presentationTimestampMicroseconds`), "Nothing produces the texture path yet; everything still flows through bytes" (verified: no production code changes outside `CapturedFrame.cs` itself, and the existing constructor preserves byte-path behaviour bit-for-bit). Coverage-gate relaxation is covered by Task 4.
- **Spec deviation: gate location.** `Directory.Build.props` vs the test csproj — documented in the file-structure section. The relaxation lands at the location that actually takes effect on .NET 10 SDK; spec intent is honoured.
- **No `IVideoEncoder` interface change.** `EncodeAsync(CapturedFrame, CancellationToken)` accepts both representations because the discriminator and texture properties are on the frame, not the method signature. Production encoder (`FFmpegNvencEncoder`) will need to branch on `frame.representation` once M3 starts producing texture frames; that branch is M4's responsibility, not M2's. M2 deliberately leaves the encoder unchanged.
- **Atomic landing.** Tasks 1 + 2 + 3 + 4 commit together so no intermediate state leaves the test suite or the coverage gate in a broken configuration. Task 1 alone would compile but its new code would be uncovered (potential gate fail); Task 2 alone needs Task 1's API; Task 4 alone (just relaxing the gate) would be confusing without the M2 changes that justify it. The spec calls M2 "purely additive plumbing" — landing it as one commit matches that framing.
- **Type consistency.** `FrameRepresentation`, `representation`, `nativeTexturePointer`, `textureArrayIndex`, `FromBytes`, `FromTexture` used identically across Tasks 1, 2, 3. `nint` (not `IntPtr`) used uniformly to match the project's existing convention (`Direct3D11DeviceManager.NativeDevicePointer`, `FFmpegNvencEncoder` style).
- **No placeholders.** Every step has either runnable code, a runnable command with explicit expected output, or a concrete decision (e.g., Task 4 step 2's "if coverage drops below 90/85, add test cases rather than lowering the gate further").
- **Identifier discipline.** Full words used per project convention (`presentationTimestampMicroseconds`, `nativeTexturePointer`, `textureArrayIndex`, no `ts`, `ptr`, `idx`).
