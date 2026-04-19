# WindowStream Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the .NET server half of WindowStream — a Windows host that enumerates per-window captures via Windows.Graphics.Capture, encodes with FFmpeg NVENC, advertises itself via mDNS, and streams a chosen window to a single Android XR viewer over a custom UDP protocol with a JSON-over-TCP control channel.

**Architecture:** Three .NET projects share one multi-targeted core library: `WindowStream.Core` (portable protocol + session + transport + platform-abstracted capture and encode interfaces), `WindowStreamServer` (MAUI picker UI, Windows target), and `WindowStream.Cli` (headless console). Platform-specific capture sits behind `#if WINDOWS` guards; the encoder uses portable FFmpeg.AutoGen. All production code is enforced to 100% line and branch coverage by a Coverlet gate in `Directory.Build.props`.

**Tech Stack:** .NET 8 (`net8.0`, `net8.0-windows10.0.19041.0`, `net8.0-maccatalyst`), System.CommandLine, System.Text.Json, Windows.Graphics.Capture (CsWinRT), FFmpeg.AutoGen (h264_nvenc), Makaretu.Dns for mDNS, .NET MAUI for the picker UI, xUnit + Coverlet for tests.

---

## Phase 1: Solution scaffolding

### Task 1: Repository root and solution file

**Files:**
- Create: `C:/Users/mtsch/WindowStream/.editorconfig`
- Create: `C:/Users/mtsch/WindowStream/.gitattributes`
- Create: `C:/Users/mtsch/WindowStream/.gitignore`
- Create: `C:/Users/mtsch/WindowStream/CLAUDE.md`
- Create: `C:/Users/mtsch/WindowStream/README.md`
- Create: `C:/Users/mtsch/WindowStream/.plan`
- Create: `C:/Users/mtsch/WindowStream/WindowStream.sln`
- Create: `C:/Users/mtsch/WindowStream/Directory.Build.props`

- [ ] **Step 1: Initialize git if not already a repository**

```bash
cd /c/Users/mtsch/WindowStream
git init
```

Expected: `Initialized empty Git repository in ...WindowStream/.git/` (or a no-op if already present).

- [ ] **Step 2: Write `.editorconfig` (exact project-conventions template)**

```ini
root = true

[*]
charset = utf-8
end_of_line = crlf
insert_final_newline = true
trim_trailing_whitespace = true
indent_style = space
indent_size = 4

[*.{cs,csproj,xaml,axaml}]
indent_size = 4

[*.{json,yml,yaml}]
indent_size = 2

[*.md]
trim_trailing_whitespace = false

[*.sln]
indent_style = tab
```

- [ ] **Step 3: Write `.gitattributes`**

```
* text=auto
*.cs text eol=crlf
*.csproj text eol=crlf
*.sln text eol=crlf
*.xaml text eol=crlf
*.sh text eol=lf
```

- [ ] **Step 4: Write `.gitignore` for .NET**

```
bin/
obj/
*.user
*.suo
.vs/
.idea/
TestResults/
coverage/
*.coverage
*.coveragexml
*.lcov
*.cobertura.xml
artifacts/
```

- [ ] **Step 5: Write `README.md`**

```markdown
# WindowStream

Windows → Galaxy XR window-streaming. Each operating-system window becomes a
first-class XR panel rendered in the headset's three-dimensional space.

See `docs/superpowers/specs/2026-04-19-windowstream-design.md` for the v1 design
and `docs/protocol.md` for the authoritative wire format.
```

- [ ] **Step 6: Write `CLAUDE.md`**

```markdown
# WindowStream

## Repository layout

- `src/WindowStream.Core/` — multi-targeted library (`net8.0`, `net8.0-windows10.0.19041.0`, `net8.0-maccatalyst`). Protocol, session, discovery, capture/encode interfaces.
- `src/WindowStreamServer/` — .NET MAUI picker GUI (Windows target in v1).
- `src/WindowStream.Cli/` — headless console application.
- `tests/WindowStream.Core.Tests/` — unit tests (xUnit, Coverlet).
- `tests/WindowStream.Integration.Tests/` — capture/encode smoke tests, Windows-only.

## Build

```bash
dotnet restore
dotnet build
```

## Test (100% line + branch coverage gate)

```bash
dotnet test
```

Coverage thresholds are enforced via `Directory.Build.props` and will fail the
build below 100% line or branch coverage on `WindowStream.Core`.

## Conventions

- One type per file.
- Nullable reference types enabled everywhere.
- Full words in identifiers — `maximum`, `configuration`, `sequence`, `arguments` (no `max`, `cfg`, `seq`, `args`).
- `async`/`await` for I/O; `CancellationToken` threaded through public async methods.
- Commit messages in imperative mood. Small, frequent commits.
```

- [ ] **Step 7: Write `.plan`**

```
- phase 1: solution scaffolding + empty projects compile
- phase 2: protocol message types + JSON serializers
- phase 3: TCP length-prefix framing
- phase 4: UDP packet header + NAL fragmenter/reassembler
- phase 5: session state machine
- phase 6: window capture (WGC) + enumerator
- phase 7: FFmpeg NVENC encoder
- phase 8: session pipeline wiring + TCP/UDP transport
- phase 9: mDNS advertiser
- phase 10: CLI commands (list, serve)
- phase 11: MAUI GUI picker + live status
- phase 12: integration smoke test
```

- [ ] **Step 8: Write `Directory.Build.props` with coverage gate**

```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  </PropertyGroup>

  <PropertyGroup Condition="'$(IsTestProject)' == 'true'">
    <CollectCoverage>true</CollectCoverage>
    <CoverletOutputFormat>cobertura</CoverletOutputFormat>
    <Threshold>100</Threshold>
    <ThresholdType>line,branch</ThresholdType>
    <ThresholdStat>total</ThresholdStat>
    <ExcludeByFile>**/Program.cs</ExcludeByFile>
  </PropertyGroup>
</Project>
```

- [ ] **Step 9: Write empty `WindowStream.sln`**

```
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal
```

- [ ] **Step 10: Commit**

```bash
cd /c/Users/mtsch/WindowStream
git add .editorconfig .gitattributes .gitignore README.md CLAUDE.md .plan Directory.Build.props WindowStream.sln
git commit -m "chore: repository scaffolding and coverage gate"
```

---

### Task 2: Create empty `WindowStream.Core` multi-targeted library

**Files:**
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/WindowStream.Core.csproj`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/AssemblyInformation.cs`

- [ ] **Step 1: Write `WindowStream.Core.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFrameworks>net8.0;net8.0-windows10.0.19041.0;net8.0-maccatalyst</TargetFrameworks>
    <RootNamespace>WindowStream.Core</RootNamespace>
    <AssemblyName>WindowStream.Core</AssemblyName>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="WindowStream.Core.Tests" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write a placeholder `AssemblyInformation.cs` so the compile produces output**

```csharp
namespace WindowStream.Core;

internal static class AssemblyInformation
{
    public const string ProductName = "WindowStream.Core";
}
```

- [ ] **Step 3: Add project to solution**

```bash
cd /c/Users/mtsch/WindowStream
dotnet sln WindowStream.sln add src/WindowStream.Core/WindowStream.Core.csproj
```

Expected: `Project ... added to the solution.`

- [ ] **Step 4: Build the library**

```bash
dotnet build src/WindowStream.Core/WindowStream.Core.csproj
```

Expected: `Build succeeded.` (no warnings, no errors).

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/ WindowStream.sln
git commit -m "feat(core): empty multi-targeted WindowStream.Core library"
```

---

### Task 3: Create empty `WindowStreamServer` MAUI project, `WindowStream.Cli` console project, and both test projects

**Files:**
- Create: `C:/Users/mtsch/WindowStream/src/WindowStreamServer/WindowStreamServer.csproj`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStreamServer/Program.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Cli/WindowStream.Cli.csproj`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Cli/Program.cs`
- Create: `C:/Users/mtsch/WindowStream/tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj`
- Create: `C:/Users/mtsch/WindowStream/tests/WindowStream.Core.Tests/PlaceholderTest.cs`
- Create: `C:/Users/mtsch/WindowStream/tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj`
- Create: `C:/Users/mtsch/WindowStream/tests/WindowStream.Integration.Tests/PlaceholderTest.cs`

- [ ] **Step 1: Write `WindowStreamServer.csproj` (MAUI, Windows target only for v1)**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFrameworks>net8.0-windows10.0.19041.0</TargetFrameworks>
    <OutputType>Exe</OutputType>
    <RootNamespace>WindowStreamServer</RootNamespace>
    <UseMaui>true</UseMaui>
    <SingleProject>true</SingleProject>
    <ApplicationTitle>WindowStreamServer</ApplicationTitle>
    <ApplicationId>com.mtschoen.windowstream.server</ApplicationId>
    <ApplicationVersion>1</ApplicationVersion>
    <WindowsPackageType>None</WindowsPackageType>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\WindowStream.Core\WindowStream.Core.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write minimal `WindowStreamServer/Program.cs`**

```csharp
namespace WindowStreamServer;

public static class Program
{
    public static void Main(string[] arguments)
    {
        System.Console.WriteLine($"WindowStreamServer placeholder; {arguments.Length} argument(s).");
    }
}
```

- [ ] **Step 3: Write `WindowStream.Cli.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <RootNamespace>WindowStream.Cli</RootNamespace>
    <AssemblyName>windowstream</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\WindowStream.Core\WindowStream.Core.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Write minimal `WindowStream.Cli/Program.cs`**

```csharp
namespace WindowStream.Cli;

public static class Program
{
    public static int Main(string[] arguments)
    {
        System.Console.WriteLine($"windowstream cli placeholder; {arguments.Length} argument(s).");
        return 0;
    }
}
```

- [ ] **Step 5: Write `WindowStream.Core.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <RootNamespace>WindowStream.Core.Tests</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.10.0" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="coverlet.msbuild" Version="6.0.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\WindowStream.Core\WindowStream.Core.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 6: Write placeholder test so xUnit runner has something to discover**

```csharp
namespace WindowStream.Core.Tests;

public sealed class PlaceholderTest
{
    [Xunit.Fact]
    public void AssemblyProductNameIsSet()
    {
        Xunit.Assert.Equal("WindowStream.Core", WindowStream.Core.AssemblyInformation.ProductName);
    }
}
```

- [ ] **Step 7: Write `WindowStream.Integration.Tests.csproj` (identical shape, different name)**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <RootNamespace>WindowStream.Integration.Tests</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.10.0" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\WindowStream.Core\WindowStream.Core.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 8: Write placeholder integration test**

```csharp
namespace WindowStream.Integration.Tests;

public sealed class PlaceholderTest
{
    [Xunit.Fact(Skip = "integration tests are implemented in phase 12")]
    public void Placeholder()
    {
    }
}
```

- [ ] **Step 9: Add the four projects to the solution**

```bash
cd /c/Users/mtsch/WindowStream
dotnet sln WindowStream.sln add src/WindowStreamServer/WindowStreamServer.csproj src/WindowStream.Cli/WindowStream.Cli.csproj tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj
```

- [ ] **Step 10: Build the solution**

```bash
dotnet build WindowStream.sln
```

Expected: `Build succeeded.` with zero warnings.

- [ ] **Step 11: Run the tests**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj
```

Expected: `Passed! - Failed: 0, Passed: 1, Skipped: 0`. Coverlet output at the bottom: `Total | 100% | 100% | 100%` (placeholder type has no branches yet).

- [ ] **Step 12: Commit**

```bash
git add src/WindowStreamServer/ src/WindowStream.Cli/ tests/WindowStream.Core.Tests/ tests/WindowStream.Integration.Tests/ WindowStream.sln
git commit -m "feat: empty MAUI server, CLI, and test projects wired to core"
```

---

## Phase 2: Protocol types and JSON serialization

### Task 4: Protocol error code enum and nested data types

**Files:**
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Protocol/ProtocolErrorCode.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Protocol/DisplayCapabilities.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Protocol/ActiveStreamInformation.cs`
- Test: `C:/Users/mtsch/WindowStream/tests/WindowStream.Core.Tests/Protocol/ProtocolErrorCodeTests.cs`

- [ ] **Step 1: Write the failing test for error-code string mapping**

```csharp
using System;
using WindowStream.Core.Protocol;
using Xunit;

namespace WindowStream.Core.Tests.Protocol;

public sealed class ProtocolErrorCodeTests
{
    [Theory]
    [InlineData(ProtocolErrorCode.VersionMismatch, "VERSION_MISMATCH")]
    [InlineData(ProtocolErrorCode.ViewerBusy, "VIEWER_BUSY")]
    [InlineData(ProtocolErrorCode.WindowGone, "WINDOW_GONE")]
    [InlineData(ProtocolErrorCode.CaptureFailed, "CAPTURE_FAILED")]
    [InlineData(ProtocolErrorCode.EncodeFailed, "ENCODE_FAILED")]
    [InlineData(ProtocolErrorCode.MalformedMessage, "MALFORMED_MESSAGE")]
    public void WireNameRoundTripsThroughParse(ProtocolErrorCode code, string wireName)
    {
        Assert.Equal(wireName, ProtocolErrorCodeNames.ToWireName(code));
        Assert.Equal(code, ProtocolErrorCodeNames.Parse(wireName));
    }

    [Fact]
    public void ParseThrowsForUnknownValue()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => ProtocolErrorCodeNames.Parse("NOT_A_REAL_CODE"));
        Assert.Contains("NOT_A_REAL_CODE", exception.Message);
    }

    [Fact]
    public void ToWireNameThrowsForUndefinedEnumValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ProtocolErrorCodeNames.ToWireName((ProtocolErrorCode)9999));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~ProtocolErrorCodeTests
```

Expected: FAIL with `error CS0246: The type or namespace name 'ProtocolErrorCode' could not be found`.

- [ ] **Step 3: Implement `ProtocolErrorCode.cs`**

```csharp
namespace WindowStream.Core.Protocol;

public enum ProtocolErrorCode
{
    VersionMismatch,
    ViewerBusy,
    WindowGone,
    CaptureFailed,
    EncodeFailed,
    MalformedMessage
}
```

- [ ] **Step 4: Implement `ProtocolErrorCodeNames.cs` (same file is fine? no — one type per file)**

Create `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Protocol/ProtocolErrorCodeNames.cs`:

```csharp
using System;

namespace WindowStream.Core.Protocol;

public static class ProtocolErrorCodeNames
{
    public static string ToWireName(ProtocolErrorCode code)
    {
        return code switch
        {
            ProtocolErrorCode.VersionMismatch => "VERSION_MISMATCH",
            ProtocolErrorCode.ViewerBusy => "VIEWER_BUSY",
            ProtocolErrorCode.WindowGone => "WINDOW_GONE",
            ProtocolErrorCode.CaptureFailed => "CAPTURE_FAILED",
            ProtocolErrorCode.EncodeFailed => "ENCODE_FAILED",
            ProtocolErrorCode.MalformedMessage => "MALFORMED_MESSAGE",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "unknown error code")
        };
    }

    public static ProtocolErrorCode Parse(string wireName)
    {
        return wireName switch
        {
            "VERSION_MISMATCH" => ProtocolErrorCode.VersionMismatch,
            "VIEWER_BUSY" => ProtocolErrorCode.ViewerBusy,
            "WINDOW_GONE" => ProtocolErrorCode.WindowGone,
            "CAPTURE_FAILED" => ProtocolErrorCode.CaptureFailed,
            "ENCODE_FAILED" => ProtocolErrorCode.EncodeFailed,
            "MALFORMED_MESSAGE" => ProtocolErrorCode.MalformedMessage,
            _ => throw new ArgumentException($"unknown protocol error code: {wireName}", nameof(wireName))
        };
    }
}
```

- [ ] **Step 5: Implement `DisplayCapabilities.cs`**

```csharp
using System.Collections.Generic;

namespace WindowStream.Core.Protocol;

public sealed record DisplayCapabilities(
    int MaximumWidth,
    int MaximumHeight,
    IReadOnlyList<string> SupportedCodecs);
```

- [ ] **Step 6: Implement `ActiveStreamInformation.cs`**

```csharp
namespace WindowStream.Core.Protocol;

public sealed record ActiveStreamInformation(
    int StreamId,
    int UdpPort,
    string Codec,
    int Width,
    int Height,
    int FramesPerSecond);
```

- [ ] **Step 7: Run test to verify it passes**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~ProtocolErrorCodeTests
```

Expected: `Passed: 8, Failed: 0`.

- [ ] **Step 8: Commit**

```bash
git add src/WindowStream.Core/Protocol/ tests/WindowStream.Core.Tests/Protocol/
git commit -m "feat(protocol): error codes and shared payload records"
```

---

### Task 5: `ControlMessage` base and all seven message types (JSON polymorphism)

**Files:**
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Protocol/ControlMessage.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Protocol/HelloMessage.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Protocol/ServerHelloMessage.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Protocol/StreamStartedMessage.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Protocol/StreamStoppedMessage.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Protocol/RequestKeyframeMessage.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Protocol/HeartbeatMessage.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Protocol/ErrorMessage.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Protocol/ControlMessageSerialization.cs`
- Test: `C:/Users/mtsch/WindowStream/tests/WindowStream.Core.Tests/Protocol/ControlMessageSerializationTests.cs`

- [ ] **Step 1: Write the failing test — round-trip every message variant**

```csharp
using System.Collections.Generic;
using WindowStream.Core.Protocol;
using Xunit;

namespace WindowStream.Core.Tests.Protocol;

public sealed class ControlMessageSerializationTests
{
    [Fact]
    public void HelloRoundTrips()
    {
        ControlMessage original = new HelloMessage(
            ViewerVersion: 1,
            DisplayCapabilities: new DisplayCapabilities(3840, 2160, new[] { "h264" }));
        AssertRoundTrip(original);
    }

    [Fact]
    public void ServerHelloWithActiveStreamRoundTrips()
    {
        ControlMessage original = new ServerHelloMessage(
            ServerVersion: 1,
            ActiveStream: new ActiveStreamInformation(7, 51001, "h264", 2560, 1440, 120));
        AssertRoundTrip(original);
    }

    [Fact]
    public void ServerHelloWithNullActiveStreamRoundTrips()
    {
        ControlMessage original = new ServerHelloMessage(ServerVersion: 1, ActiveStream: null);
        AssertRoundTrip(original);
    }

    [Fact]
    public void StreamStartedRoundTrips()
    {
        ControlMessage original = new StreamStartedMessage(7, 51001, "h264", 2560, 1440, 120);
        AssertRoundTrip(original);
    }

    [Fact]
    public void StreamStoppedRoundTrips()
    {
        AssertRoundTrip(new StreamStoppedMessage(7));
    }

    [Fact]
    public void RequestKeyframeRoundTrips()
    {
        AssertRoundTrip(new RequestKeyframeMessage(7));
    }

    [Fact]
    public void HeartbeatRoundTrips()
    {
        AssertRoundTrip(HeartbeatMessage.Instance);
    }

    [Fact]
    public void ErrorRoundTrips()
    {
        AssertRoundTrip(new ErrorMessage(ProtocolErrorCode.ViewerBusy, "already connected"));
    }

    [Fact]
    public void HeartbeatEmitsExactlyTypeField()
    {
        string encoded = ControlMessageSerialization.Serialize(HeartbeatMessage.Instance);
        Assert.Equal("{\"type\":\"HEARTBEAT\"}", encoded);
    }

    [Fact]
    public void UnknownTypeThrowsMalformed()
    {
        MalformedMessageException exception = Assert.Throws<MalformedMessageException>(
            () => ControlMessageSerialization.Deserialize("{\"type\":\"WAT\"}"));
        Assert.Contains("WAT", exception.Message);
    }

    [Fact]
    public void MissingTypeThrowsMalformed()
    {
        Assert.Throws<MalformedMessageException>(
            () => ControlMessageSerialization.Deserialize("{}"));
    }

    [Fact]
    public void BrokenJsonThrowsMalformed()
    {
        Assert.Throws<MalformedMessageException>(
            () => ControlMessageSerialization.Deserialize("not json"));
    }

    private static void AssertRoundTrip(ControlMessage original)
    {
        string encoded = ControlMessageSerialization.Serialize(original);
        ControlMessage decoded = ControlMessageSerialization.Deserialize(encoded);
        Assert.Equal(original, decoded);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~ControlMessageSerializationTests
```

Expected: FAIL with `error CS0246: The type or namespace name 'HelloMessage' could not be found` (and siblings).

- [ ] **Step 3: Implement `ControlMessage.cs` (polymorphic base with System.Text.Json attributes)**

```csharp
using System.Text.Json.Serialization;

namespace WindowStream.Core.Protocol;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), typeDiscriminator: "HELLO")]
[JsonDerivedType(typeof(ServerHelloMessage), typeDiscriminator: "SERVER_HELLO")]
[JsonDerivedType(typeof(StreamStartedMessage), typeDiscriminator: "STREAM_STARTED")]
[JsonDerivedType(typeof(StreamStoppedMessage), typeDiscriminator: "STREAM_STOPPED")]
[JsonDerivedType(typeof(RequestKeyframeMessage), typeDiscriminator: "REQUEST_KEYFRAME")]
[JsonDerivedType(typeof(HeartbeatMessage), typeDiscriminator: "HEARTBEAT")]
[JsonDerivedType(typeof(ErrorMessage), typeDiscriminator: "ERROR")]
public abstract record ControlMessage;
```

- [ ] **Step 4: Implement the seven message records, one per file**

`HelloMessage.cs`:

```csharp
namespace WindowStream.Core.Protocol;

public sealed record HelloMessage(
    int ViewerVersion,
    DisplayCapabilities DisplayCapabilities) : ControlMessage;
```

`ServerHelloMessage.cs`:

```csharp
namespace WindowStream.Core.Protocol;

public sealed record ServerHelloMessage(
    int ServerVersion,
    ActiveStreamInformation? ActiveStream) : ControlMessage;
```

`StreamStartedMessage.cs`:

```csharp
namespace WindowStream.Core.Protocol;

public sealed record StreamStartedMessage(
    int StreamId,
    int UdpPort,
    string Codec,
    int Width,
    int Height,
    int FramesPerSecond) : ControlMessage;
```

`StreamStoppedMessage.cs`:

```csharp
namespace WindowStream.Core.Protocol;

public sealed record StreamStoppedMessage(int StreamId) : ControlMessage;
```

`RequestKeyframeMessage.cs`:

```csharp
namespace WindowStream.Core.Protocol;

public sealed record RequestKeyframeMessage(int StreamId) : ControlMessage;
```

`HeartbeatMessage.cs`:

```csharp
namespace WindowStream.Core.Protocol;

public sealed record HeartbeatMessage : ControlMessage
{
    public static HeartbeatMessage Instance { get; } = new HeartbeatMessage();
}
```

`ErrorMessage.cs`:

```csharp
using System.Text.Json.Serialization;

namespace WindowStream.Core.Protocol;

public sealed record ErrorMessage(
    [property: JsonPropertyName("code")] ProtocolErrorCode Code,
    [property: JsonPropertyName("message")] string Message) : ControlMessage;
```

- [ ] **Step 5: Implement `MalformedMessageException.cs`**

Create `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Protocol/MalformedMessageException.cs`:

```csharp
using System;

namespace WindowStream.Core.Protocol;

public sealed class MalformedMessageException : Exception
{
    public MalformedMessageException(string message) : base(message) { }
    public MalformedMessageException(string message, Exception innerException) : base(message, innerException) { }
}
```

- [ ] **Step 6: Implement `ControlMessageSerialization.cs`**

```csharp
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowStream.Core.Protocol;

public static class ControlMessageSerialization
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new ProtocolErrorCodeConverter() }
    };

    public static string Serialize(ControlMessage message)
    {
        return JsonSerializer.Serialize(message, Options);
    }

    public static ControlMessage Deserialize(string payload)
    {
        try
        {
            ControlMessage? decoded = JsonSerializer.Deserialize<ControlMessage>(payload, Options);
            if (decoded is null)
            {
                throw new MalformedMessageException("payload deserialized to null");
            }
            return decoded;
        }
        catch (JsonException exception)
        {
            throw new MalformedMessageException($"could not parse control message: {exception.Message}", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new MalformedMessageException($"unsupported control message discriminator: {exception.Message}", exception);
        }
    }

    private sealed class ProtocolErrorCodeConverter : JsonConverter<ProtocolErrorCode>
    {
        public override ProtocolErrorCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? wireName = reader.GetString();
            if (wireName is null)
            {
                throw new JsonException("null is not a valid protocol error code");
            }
            return ProtocolErrorCodeNames.Parse(wireName);
        }

        public override void Write(Utf8JsonWriter writer, ProtocolErrorCode value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(ProtocolErrorCodeNames.ToWireName(value));
        }
    }
}
```

- [ ] **Step 7: Run tests — verify all pass**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~ControlMessageSerializationTests
```

Expected: `Passed: 12, Failed: 0`. Coverage reports 100% line AND branch on `Protocol/`.

- [ ] **Step 8: Commit**

```bash
git add src/WindowStream.Core/Protocol/ tests/WindowStream.Core.Tests/Protocol/
git commit -m "feat(protocol): seven control message types with polymorphic JSON"
```

---

## Phase 3: TCP length-prefix framing

### Task 6: `LengthPrefixFraming` encode (synchronous buffer path)

**Files:**
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Transport/LengthPrefixFraming.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Transport/FrameTooLargeException.cs`
- Test: `C:/Users/mtsch/WindowStream/tests/WindowStream.Core.Tests/Transport/LengthPrefixFramingEncodeTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using WindowStream.Core.Transport;
using Xunit;

namespace WindowStream.Core.Tests.Transport;

public sealed class LengthPrefixFramingEncodeTests
{
    [Fact]
    public void EncodePrependsBigEndianLength()
    {
        byte[] payload = new byte[] { 0x41, 0x42, 0x43 };
        byte[] framed = LengthPrefixFraming.Encode(payload);
        Assert.Equal(7, framed.Length);
        Assert.Equal(0x00, framed[0]);
        Assert.Equal(0x00, framed[1]);
        Assert.Equal(0x00, framed[2]);
        Assert.Equal(0x03, framed[3]);
        Assert.Equal(0x41, framed[4]);
        Assert.Equal(0x42, framed[5]);
        Assert.Equal(0x43, framed[6]);
    }

    [Fact]
    public void EncodeAcceptsEmptyPayload()
    {
        byte[] framed = LengthPrefixFraming.Encode(Array.Empty<byte>());
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, framed);
    }

    [Fact]
    public void EncodeRejectsNullPayload()
    {
        Assert.Throws<ArgumentNullException>(() => LengthPrefixFraming.Encode(null!));
    }

    [Fact]
    public void EncodeRejectsOversizedPayload()
    {
        // We won't actually allocate a 16 MiB buffer; we use the Span overload with a fake length.
        Assert.Throws<FrameTooLargeException>(
            () => LengthPrefixFraming.ValidatePayloadLength(LengthPrefixFraming.MaximumPayloadByteLength + 1));
    }

    [Fact]
    public void EncodeAllowsMaximumExactLength()
    {
        LengthPrefixFraming.ValidatePayloadLength(LengthPrefixFraming.MaximumPayloadByteLength);
    }

    [Fact]
    public void ValidatePayloadLengthRejectsNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LengthPrefixFraming.ValidatePayloadLength(-1));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~LengthPrefixFramingEncodeTests
```

Expected: FAIL with `error CS0246: The type or namespace name 'LengthPrefixFraming' could not be found`.

- [ ] **Step 3: Implement `FrameTooLargeException.cs`**

```csharp
using System;

namespace WindowStream.Core.Transport;

public sealed class FrameTooLargeException : Exception
{
    public FrameTooLargeException(int actualLength, int maximumLength)
        : base($"frame payload {actualLength} bytes exceeds maximum {maximumLength}")
    {
        ActualLength = actualLength;
        MaximumLength = maximumLength;
    }

    public int ActualLength { get; }
    public int MaximumLength { get; }
}
```

- [ ] **Step 4: Implement `LengthPrefixFraming.cs` (encode + validation only for now)**

```csharp
using System;
using System.Buffers.Binary;

namespace WindowStream.Core.Transport;

public static class LengthPrefixFraming
{
    public const int LengthPrefixByteLength = 4;

    /// <summary>Sixteen mebibytes — far larger than any JSON control message will ever be.</summary>
    public const int MaximumPayloadByteLength = 16 * 1024 * 1024;

    public static byte[] Encode(byte[] payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }
        ValidatePayloadLength(payload.Length);
        byte[] framed = new byte[LengthPrefixByteLength + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(0, LengthPrefixByteLength), (uint)payload.Length);
        Array.Copy(payload, 0, framed, LengthPrefixByteLength, payload.Length);
        return framed;
    }

    public static void ValidatePayloadLength(int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "length must be non-negative");
        }
        if (length > MaximumPayloadByteLength)
        {
            throw new FrameTooLargeException(length, MaximumPayloadByteLength);
        }
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~LengthPrefixFramingEncodeTests
```

Expected: `Passed: 6, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/WindowStream.Core/Transport/ tests/WindowStream.Core.Tests/Transport/
git commit -m "feat(transport): length-prefix encode + validation"
```

---

### Task 7: Async read helper with partial-read recovery

**Files:**
- Modify: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Transport/LengthPrefixFraming.cs`
- Test: `C:/Users/mtsch/WindowStream/tests/WindowStream.Core.Tests/Transport/LengthPrefixFramingReadTests.cs`

- [ ] **Step 1: Write the failing async-read test**

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Transport;
using Xunit;

namespace WindowStream.Core.Tests.Transport;

public sealed class LengthPrefixFramingReadTests
{
    [Fact]
    public async Task ReadsCompletePayloadInOneCall()
    {
        byte[] framed = LengthPrefixFraming.Encode(new byte[] { 0xAA, 0xBB, 0xCC });
        using MemoryStream stream = new(framed);
        byte[] payload = await LengthPrefixFraming.ReadFrameAsync(stream, CancellationToken.None);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, payload);
    }

    [Fact]
    public async Task ReassemblesAcrossMultipleReads()
    {
        byte[] framed = LengthPrefixFraming.Encode(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 });
        using SlowStream stream = new(framed, chunkSize: 1);
        byte[] payload = await LengthPrefixFraming.ReadFrameAsync(stream, CancellationToken.None);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 }, payload);
    }

    [Fact]
    public async Task EndOfStreamInsideLengthPrefixThrows()
    {
        using MemoryStream stream = new(new byte[] { 0x00, 0x00 });
        await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await LengthPrefixFraming.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task EndOfStreamInsidePayloadThrows()
    {
        // length = 4, payload delivered as only 2 bytes
        byte[] truncated = new byte[] { 0x00, 0x00, 0x00, 0x04, 0xAA, 0xBB };
        using MemoryStream stream = new(truncated);
        await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await LengthPrefixFraming.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsOversizedFrame()
    {
        // length = MaximumPayloadByteLength + 1 encoded big-endian
        uint oversize = (uint)LengthPrefixFraming.MaximumPayloadByteLength + 1;
        byte[] header = new byte[]
        {
            (byte)((oversize >> 24) & 0xFF),
            (byte)((oversize >> 16) & 0xFF),
            (byte)((oversize >> 8) & 0xFF),
            (byte)(oversize & 0xFF)
        };
        using MemoryStream stream = new(header);
        await Assert.ThrowsAsync<FrameTooLargeException>(
            async () => await LengthPrefixFraming.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task PropagatesCancellation()
    {
        using MemoryStream stream = new();
        using CancellationTokenSource source = new();
        source.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await LengthPrefixFraming.ReadFrameAsync(stream, source.Token));
    }

    [Fact]
    public async Task ReadsEmptyPayload()
    {
        byte[] framed = LengthPrefixFraming.Encode(Array.Empty<byte>());
        using MemoryStream stream = new(framed);
        byte[] payload = await LengthPrefixFraming.ReadFrameAsync(stream, CancellationToken.None);
        Assert.Empty(payload);
    }

    // Helper: a Stream that returns at most `chunkSize` bytes per read call, forcing the
    // reader to loop until the requested length is satisfied.
    private sealed class SlowStream : Stream
    {
        private readonly byte[] data;
        private readonly int chunkSize;
        private int position;

        public SlowStream(byte[] data, int chunkSize)
        {
            this.data = data;
            this.chunkSize = chunkSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int actual = Math.Min(Math.Min(count, chunkSize), data.Length - position);
            Array.Copy(data, position, buffer, offset, actual);
            position += actual;
            return actual;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~LengthPrefixFramingReadTests
```

Expected: FAIL with `error CS0117: 'LengthPrefixFraming' does not contain a definition for 'ReadFrameAsync'`.

- [ ] **Step 3: Extend `LengthPrefixFraming.cs` with async read helpers**

Add the following members to the existing `LengthPrefixFraming` class:

```csharp
    public static async System.Threading.Tasks.Task<byte[]> ReadFrameAsync(
        System.IO.Stream stream,
        System.Threading.CancellationToken cancellationToken)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }
        cancellationToken.ThrowIfCancellationRequested();

        byte[] lengthBuffer = new byte[LengthPrefixByteLength];
        await ReadExactlyAsync(stream, lengthBuffer, 0, LengthPrefixByteLength, cancellationToken).ConfigureAwait(false);

        uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBuffer);
        if (payloadLength > (uint)MaximumPayloadByteLength)
        {
            throw new FrameTooLargeException((int)Math.Min(payloadLength, int.MaxValue), MaximumPayloadByteLength);
        }
        byte[] payload = new byte[payloadLength];
        if (payloadLength > 0)
        {
            await ReadExactlyAsync(stream, payload, 0, (int)payloadLength, cancellationToken).ConfigureAwait(false);
        }
        return payload;
    }

    public static async System.Threading.Tasks.Task WriteFrameAsync(
        System.IO.Stream stream,
        byte[] payload,
        System.Threading.CancellationToken cancellationToken)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }
        byte[] framed = Encode(payload);
        await stream.WriteAsync(framed.AsMemory(0, framed.Length), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async System.Threading.Tasks.Task ReadExactlyAsync(
        System.IO.Stream stream,
        byte[] buffer,
        int offset,
        int count,
        System.Threading.CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int readThisCall = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), cancellationToken).ConfigureAwait(false);
            if (readThisCall == 0)
            {
                throw new System.IO.EndOfStreamException(
                    $"stream ended after {totalRead} of {count} bytes");
            }
            totalRead += readThisCall;
        }
    }
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~LengthPrefixFramingReadTests
```

Expected: `Passed: 7, Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Transport/LengthPrefixFraming.cs tests/WindowStream.Core.Tests/Transport/LengthPrefixFramingReadTests.cs
git commit -m "feat(transport): async ReadFrameAsync/WriteFrameAsync with partial-read recovery"
```

---

## Phase 4: UDP packet header + NAL fragmentation/reassembly

### Task 8: `PacketHeader` struct — write and parse

**Files:**
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Transport/PacketHeader.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Transport/PacketFlags.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Transport/MalformedPacketException.cs`
- Test: `C:/Users/mtsch/WindowStream/tests/WindowStream.Core.Tests/Transport/PacketHeaderTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using WindowStream.Core.Transport;
using Xunit;

namespace WindowStream.Core.Tests.Transport;

public sealed class PacketHeaderTests
{
    [Fact]
    public void WriteProducesExpectedByteLayout()
    {
        byte[] buffer = new byte[PacketHeader.HeaderByteLength];
        PacketHeader header = new(
            StreamId: 0x01020304,
            Sequence: 0x10203040,
            PresentationTimestampMicroseconds: 0x1122334455667788,
            Flags: PacketFlags.IdrFrame | PacketFlags.LastFragment,
            FragmentIndex: 2,
            FragmentTotal: 3);
        header.WriteTo(buffer);

        // magic 'WSTR' = 0x57535452
        Assert.Equal(0x57, buffer[0]);
        Assert.Equal(0x53, buffer[1]);
        Assert.Equal(0x54, buffer[2]);
        Assert.Equal(0x52, buffer[3]);
        Assert.Equal(0x01, buffer[4]);
        Assert.Equal(0x02, buffer[5]);
        Assert.Equal(0x03, buffer[6]);
        Assert.Equal(0x04, buffer[7]);
        Assert.Equal(0x10, buffer[8]);
        Assert.Equal(0x20, buffer[9]);
        Assert.Equal(0x30, buffer[10]);
        Assert.Equal(0x40, buffer[11]);
        Assert.Equal(0x11, buffer[12]);
        Assert.Equal(0x22, buffer[13]);
        Assert.Equal(0x33, buffer[14]);
        Assert.Equal(0x44, buffer[15]);
        Assert.Equal(0x55, buffer[16]);
        Assert.Equal(0x66, buffer[17]);
        Assert.Equal(0x77, buffer[18]);
        Assert.Equal(0x88, buffer[19]);
        Assert.Equal(0x03, buffer[20]);  // flags: IDR | LAST
        Assert.Equal(0x02, buffer[21]);  // fragmentIndex
        Assert.Equal(0x03, buffer[22]);  // fragmentTotal
        Assert.Equal(0x00, buffer[23]);  // reserved
    }

    [Fact]
    public void ParseRecoversWrittenValues()
    {
        byte[] buffer = new byte[PacketHeader.HeaderByteLength + 5];
        PacketHeader original = new(
            StreamId: 7,
            Sequence: 9001,
            PresentationTimestampMicroseconds: 1_234_567_890,
            Flags: PacketFlags.IdrFrame,
            FragmentIndex: 0,
            FragmentTotal: 1);
        original.WriteTo(buffer);
        PacketHeader parsed = PacketHeader.Parse(buffer);
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void IsIdrFrameAndIsLastFragmentReflectFlags()
    {
        PacketHeader idrOnly = MakeHeader(PacketFlags.IdrFrame, fragmentIndex: 0, fragmentTotal: 1);
        PacketHeader lastOnly = MakeHeader(PacketFlags.LastFragment, fragmentIndex: 0, fragmentTotal: 1);
        PacketHeader both = MakeHeader(PacketFlags.IdrFrame | PacketFlags.LastFragment, 0, 1);
        PacketHeader none = MakeHeader(PacketFlags.None, 0, 1);
        Assert.True(idrOnly.IsIdrFrame);
        Assert.False(idrOnly.IsLastFragment);
        Assert.False(lastOnly.IsIdrFrame);
        Assert.True(lastOnly.IsLastFragment);
        Assert.True(both.IsIdrFrame);
        Assert.True(both.IsLastFragment);
        Assert.False(none.IsIdrFrame);
        Assert.False(none.IsLastFragment);
    }

    [Fact]
    public void ParseRejectsShortBuffer()
    {
        Assert.Throws<MalformedPacketException>(
            () => PacketHeader.Parse(new byte[PacketHeader.HeaderByteLength - 1]));
    }

    [Fact]
    public void ParseRejectsWrongMagic()
    {
        byte[] buffer = new byte[PacketHeader.HeaderByteLength];
        buffer[0] = 0x00; buffer[1] = 0x00; buffer[2] = 0x00; buffer[3] = 0x00;
        buffer[22] = 0x01;  // fragmentTotal must be > 0 to reach the magic check first? it's ok — magic comes first.
        Assert.Throws<MalformedPacketException>(() => PacketHeader.Parse(buffer));
    }

    [Fact]
    public void ParseRejectsZeroFragmentTotal()
    {
        byte[] buffer = new byte[PacketHeader.HeaderByteLength];
        new PacketHeader(1, 1, 1, PacketFlags.None, FragmentIndex: 0, FragmentTotal: 1).WriteTo(buffer);
        buffer[22] = 0x00;
        Assert.Throws<MalformedPacketException>(() => PacketHeader.Parse(buffer));
    }

    [Fact]
    public void ParseRejectsFragmentIndexAtOrAboveTotal()
    {
        byte[] buffer = new byte[PacketHeader.HeaderByteLength];
        new PacketHeader(1, 1, 1, PacketFlags.None, FragmentIndex: 0, FragmentTotal: 1).WriteTo(buffer);
        buffer[21] = 0x05;  // index
        buffer[22] = 0x03;  // total
        Assert.Throws<MalformedPacketException>(() => PacketHeader.Parse(buffer));
    }

    [Fact]
    public void WriteRejectsShortBuffer()
    {
        PacketHeader header = MakeHeader(PacketFlags.None, 0, 1);
        Assert.Throws<ArgumentException>(() => header.WriteTo(new byte[PacketHeader.HeaderByteLength - 1]));
    }

    [Fact]
    public void ConstructorRejectsFragmentIndexAtOrAboveTotal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PacketHeader(1, 1, 1, PacketFlags.None, FragmentIndex: 3, FragmentTotal: 3));
    }

    [Fact]
    public void ConstructorRejectsZeroFragmentTotal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PacketHeader(1, 1, 1, PacketFlags.None, FragmentIndex: 0, FragmentTotal: 0));
    }

    private static PacketHeader MakeHeader(PacketFlags flags, int fragmentIndex, int fragmentTotal)
    {
        return new PacketHeader(
            StreamId: 1,
            Sequence: 1,
            PresentationTimestampMicroseconds: 1,
            Flags: flags,
            FragmentIndex: fragmentIndex,
            FragmentTotal: fragmentTotal);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~PacketHeaderTests
```

Expected: FAIL — missing `PacketHeader`, `PacketFlags`, `MalformedPacketException`.

- [ ] **Step 3: Implement `PacketFlags.cs`**

```csharp
using System;

namespace WindowStream.Core.Transport;

[Flags]
public enum PacketFlags : byte
{
    None = 0x00,
    IdrFrame = 0x01,
    LastFragment = 0x02
}
```

- [ ] **Step 4: Implement `MalformedPacketException.cs`**

```csharp
using System;

namespace WindowStream.Core.Transport;

public sealed class MalformedPacketException : Exception
{
    public MalformedPacketException(string message) : base(message) { }
}
```

- [ ] **Step 5: Implement `PacketHeader.cs`**

```csharp
using System;
using System.Buffers.Binary;

namespace WindowStream.Core.Transport;

public readonly record struct PacketHeader(
    uint StreamId,
    uint Sequence,
    ulong PresentationTimestampMicroseconds,
    PacketFlags Flags,
    byte FragmentIndex,
    byte FragmentTotal)
{
    public const int HeaderByteLength = 24;
    public const int MaximumPayloadByteLength = 1200;
    public const uint MagicValue = 0x57535452; // 'WSTR'

    public PacketHeader(
        int StreamId,
        int Sequence,
        long PresentationTimestampMicroseconds,
        PacketFlags Flags,
        int FragmentIndex,
        int FragmentTotal)
        : this(
            checked((uint)StreamId),
            checked((uint)Sequence),
            checked((ulong)PresentationTimestampMicroseconds),
            Flags,
            checked((byte)FragmentIndex),
            checked((byte)FragmentTotal))
    {
        if (FragmentTotal <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FragmentTotal), FragmentTotal, "fragmentTotal must be at least 1");
        }
        if ((uint)FragmentIndex >= (uint)FragmentTotal)
        {
            throw new ArgumentOutOfRangeException(nameof(FragmentIndex), FragmentIndex, $"fragmentIndex must be less than fragmentTotal {FragmentTotal}");
        }
    }

    public bool IsIdrFrame => (Flags & PacketFlags.IdrFrame) != 0;
    public bool IsLastFragment => (Flags & PacketFlags.LastFragment) != 0;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < HeaderByteLength)
        {
            throw new ArgumentException(
                $"destination must be at least {HeaderByteLength} bytes, got {destination.Length}",
                nameof(destination));
        }
        BinaryPrimitives.WriteUInt32BigEndian(destination[0..4], MagicValue);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..8], StreamId);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..12], Sequence);
        BinaryPrimitives.WriteUInt64BigEndian(destination[12..20], PresentationTimestampMicroseconds);
        destination[20] = (byte)Flags;
        destination[21] = FragmentIndex;
        destination[22] = FragmentTotal;
        destination[23] = 0x00;
    }

    public static PacketHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderByteLength)
        {
            throw new MalformedPacketException(
                $"packet is {source.Length} bytes, minimum is {HeaderByteLength}");
        }
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(source[0..4]);
        if (magic != MagicValue)
        {
            throw new MalformedPacketException($"unexpected magic: 0x{magic:X8}");
        }
        uint streamId = BinaryPrimitives.ReadUInt32BigEndian(source[4..8]);
        uint sequence = BinaryPrimitives.ReadUInt32BigEndian(source[8..12]);
        ulong presentationTimestamp = BinaryPrimitives.ReadUInt64BigEndian(source[12..20]);
        byte flags = source[20];
        byte fragmentIndex = source[21];
        byte fragmentTotal = source[22];
        if (fragmentTotal == 0)
        {
            throw new MalformedPacketException("fragmentTotal must be at least 1");
        }
        if (fragmentIndex >= fragmentTotal)
        {
            throw new MalformedPacketException(
                $"fragmentIndex {fragmentIndex} is not less than fragmentTotal {fragmentTotal}");
        }
        return new PacketHeader(
            StreamId: streamId,
            Sequence: sequence,
            PresentationTimestampMicroseconds: presentationTimestamp,
            Flags: (PacketFlags)flags,
            FragmentIndex: fragmentIndex,
            FragmentTotal: fragmentTotal);
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~PacketHeaderTests
```

Expected: `Passed: 10, Failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add src/WindowStream.Core/Transport/PacketHeader.cs src/WindowStream.Core/Transport/PacketFlags.cs src/WindowStream.Core/Transport/MalformedPacketException.cs tests/WindowStream.Core.Tests/Transport/PacketHeaderTests.cs
git commit -m "feat(transport): 24-byte packet header with parse/write"
```

---

### Task 9: `NalFragmenter` — split one NAL unit into ≤1200-byte fragments

**Files:**
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Transport/FragmentedPacket.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Transport/NalFragmenter.cs`
- Test: `C:/Users/mtsch/WindowStream/tests/WindowStream.Core.Tests/Transport/NalFragmenterTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.Collections.Generic;
using WindowStream.Core.Transport;
using Xunit;

namespace WindowStream.Core.Tests.Transport;

public sealed class NalFragmenterTests
{
    [Fact]
    public void SmallNalProducesSinglePacketWithLastFlag()
    {
        byte[] nalUnit = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67, 0x42 }; // SPS-ish
        NalFragmenter fragmenter = new();
        List<FragmentedPacket> packets = new(fragmenter.Fragment(
            streamId: 7,
            sequence: 100,
            presentationTimestampMicroseconds: 1000,
            isIdrFrame: false,
            nalUnit: nalUnit));
        Assert.Single(packets);
        Assert.Equal((byte)0, packets[0].Header.FragmentIndex);
        Assert.Equal((byte)1, packets[0].Header.FragmentTotal);
        Assert.True(packets[0].Header.IsLastFragment);
        Assert.False(packets[0].Header.IsIdrFrame);
        Assert.Equal(nalUnit, packets[0].Payload.ToArray());
    }

    [Fact]
    public void IdrFlagIsSetOnEveryFragmentWhenRequested()
    {
        byte[] nalUnit = new byte[PacketHeader.MaximumPayloadByteLength * 2 + 100];
        new Random(42).NextBytes(nalUnit);
        NalFragmenter fragmenter = new();
        List<FragmentedPacket> packets = new(fragmenter.Fragment(
            streamId: 1, sequence: 1, presentationTimestampMicroseconds: 1,
            isIdrFrame: true, nalUnit: nalUnit));
        Assert.Equal(3, packets.Count);
        foreach (FragmentedPacket packet in packets)
        {
            Assert.True(packet.Header.IsIdrFrame);
        }
        Assert.False(packets[0].Header.IsLastFragment);
        Assert.False(packets[1].Header.IsLastFragment);
        Assert.True(packets[2].Header.IsLastFragment);
    }

    [Fact]
    public void FragmentIndicesAreContiguousAndTotalMatches()
    {
        byte[] nalUnit = new byte[PacketHeader.MaximumPayloadByteLength * 3];
        NalFragmenter fragmenter = new();
        List<FragmentedPacket> packets = new(fragmenter.Fragment(1, 1, 1, false, nalUnit));
        Assert.Equal(3, packets.Count);
        for (int index = 0; index < packets.Count; index++)
        {
            Assert.Equal((byte)index, packets[index].Header.FragmentIndex);
            Assert.Equal((byte)3, packets[index].Header.FragmentTotal);
        }
    }

    [Fact]
    public void PayloadIsConcatenationOfAllFragmentsInOrder()
    {
        byte[] nalUnit = new byte[PacketHeader.MaximumPayloadByteLength + 500];
        for (int index = 0; index < nalUnit.Length; index++)
        {
            nalUnit[index] = (byte)(index & 0xFF);
        }
        NalFragmenter fragmenter = new();
        List<FragmentedPacket> packets = new(fragmenter.Fragment(1, 1, 1, false, nalUnit));
        byte[] reassembled = new byte[nalUnit.Length];
        int cursor = 0;
        foreach (FragmentedPacket packet in packets)
        {
            packet.Payload.Span.CopyTo(reassembled.AsSpan(cursor));
            cursor += packet.Payload.Length;
        }
        Assert.Equal(nalUnit, reassembled);
    }

    [Fact]
    public void EmptyNalUnitThrows()
    {
        NalFragmenter fragmenter = new();
        Assert.Throws<ArgumentException>(
            () =>
            {
                foreach (FragmentedPacket _ in fragmenter.Fragment(1, 1, 1, false, Array.Empty<byte>())) { }
            });
    }

    [Fact]
    public void NullNalUnitThrows()
    {
        NalFragmenter fragmenter = new();
        Assert.Throws<ArgumentNullException>(
            () =>
            {
                foreach (FragmentedPacket _ in fragmenter.Fragment(1, 1, 1, false, null!)) { }
            });
    }

    [Fact]
    public void TooManyFragmentsThrows()
    {
        // 256 fragments is the byte-size limit on fragmentTotal; exactly 256 should pass, 257 should fail.
        byte[] nalUnit = new byte[PacketHeader.MaximumPayloadByteLength * 256 + 1];
        NalFragmenter fragmenter = new();
        Assert.Throws<ArgumentException>(
            () =>
            {
                foreach (FragmentedPacket _ in fragmenter.Fragment(1, 1, 1, false, nalUnit)) { }
            });
    }

    [Fact]
    public void HeaderStreamIdAndSequenceArePropagated()
    {
        byte[] nalUnit = new byte[100];
        NalFragmenter fragmenter = new();
        List<FragmentedPacket> packets = new(fragmenter.Fragment(
            streamId: 42, sequence: 99, presentationTimestampMicroseconds: 1234, false, nalUnit));
        Assert.Equal((uint)42, packets[0].Header.StreamId);
        Assert.Equal((uint)99, packets[0].Header.Sequence);
        Assert.Equal((ulong)1234, packets[0].Header.PresentationTimestampMicroseconds);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~NalFragmenterTests
```

Expected: FAIL with `NalFragmenter` / `FragmentedPacket` missing.

- [ ] **Step 3: Implement `FragmentedPacket.cs`**

```csharp
using System;

namespace WindowStream.Core.Transport;

public readonly record struct FragmentedPacket(PacketHeader Header, ReadOnlyMemory<byte> Payload);
```

- [ ] **Step 4: Implement `NalFragmenter.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace WindowStream.Core.Transport;

public sealed class NalFragmenter
{
    public IEnumerable<FragmentedPacket> Fragment(
        int streamId,
        int sequence,
        long presentationTimestampMicroseconds,
        bool isIdrFrame,
        byte[] nalUnit)
    {
        if (nalUnit is null)
        {
            throw new ArgumentNullException(nameof(nalUnit));
        }
        if (nalUnit.Length == 0)
        {
            throw new ArgumentException("nalUnit must not be empty", nameof(nalUnit));
        }
        int fragmentTotal = (nalUnit.Length + PacketHeader.MaximumPayloadByteLength - 1) / PacketHeader.MaximumPayloadByteLength;
        if (fragmentTotal > 256)
        {
            throw new ArgumentException(
                $"nalUnit too large: {nalUnit.Length} bytes would require {fragmentTotal} fragments (maximum 256)",
                nameof(nalUnit));
        }
        return EnumerateFragments(streamId, sequence, presentationTimestampMicroseconds, isIdrFrame, nalUnit, fragmentTotal);
    }

    private static IEnumerable<FragmentedPacket> EnumerateFragments(
        int streamId,
        int sequence,
        long presentationTimestampMicroseconds,
        bool isIdrFrame,
        byte[] nalUnit,
        int fragmentTotal)
    {
        int offset = 0;
        for (int fragmentIndex = 0; fragmentIndex < fragmentTotal; fragmentIndex++)
        {
            int fragmentLength = Math.Min(PacketHeader.MaximumPayloadByteLength, nalUnit.Length - offset);
            bool isLast = fragmentIndex == fragmentTotal - 1;
            PacketFlags flags = PacketFlags.None;
            if (isIdrFrame)
            {
                flags |= PacketFlags.IdrFrame;
            }
            if (isLast)
            {
                flags |= PacketFlags.LastFragment;
            }
            PacketHeader header = new(
                StreamId: streamId,
                Sequence: sequence,
                PresentationTimestampMicroseconds: presentationTimestampMicroseconds,
                Flags: flags,
                FragmentIndex: fragmentIndex,
                FragmentTotal: fragmentTotal);
            yield return new FragmentedPacket(header, new ReadOnlyMemory<byte>(nalUnit, offset, fragmentLength));
            offset += fragmentLength;
        }
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~NalFragmenterTests
```

Expected: `Passed: 8, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/WindowStream.Core/Transport/FragmentedPacket.cs src/WindowStream.Core/Transport/NalFragmenter.cs tests/WindowStream.Core.Tests/Transport/NalFragmenterTests.cs
git commit -m "feat(transport): NAL fragmenter splits into ≤1200-byte packets"
```

---

### Task 10: `NalReassembler` — buffer fragments and emit complete NAL units

**Files:**
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Transport/ReassembledNalUnit.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Transport/NalReassembler.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Transport/IClock.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Transport/SystemClock.cs`
- Test: `C:/Users/mtsch/WindowStream/tests/WindowStream.Core.Tests/Transport/NalReassemblerTests.cs`
- Test: `C:/Users/mtsch/WindowStream/tests/WindowStream.Core.Tests/Transport/FakeClock.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.Linq;
using WindowStream.Core.Transport;
using Xunit;

namespace WindowStream.Core.Tests.Transport;

public sealed class NalReassemblerTests
{
    [Fact]
    public void SinglePacketNalIsEmittedImmediately()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader header = new(1, 1, 1000, PacketFlags.LastFragment, FragmentIndex: 0, FragmentTotal: 1);
        ReassembledNalUnit? result = reassembler.Offer(header, new byte[] { 0x41, 0x42 });
        Assert.NotNull(result);
        Assert.Equal(new byte[] { 0x41, 0x42 }, result!.Value.NalUnit);
        Assert.Equal((uint)1, result.Value.StreamId);
        Assert.False(result.Value.IsIdrFrame);
    }

    [Fact]
    public void IdrFlagPropagatesToReassembledUnit()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader header = new(1, 1, 1, PacketFlags.IdrFrame | PacketFlags.LastFragment, 0, 1);
        ReassembledNalUnit? result = reassembler.Offer(header, new byte[] { 0x65 });
        Assert.NotNull(result);
        Assert.True(result!.Value.IsIdrFrame);
    }

    [Fact]
    public void MultiFragmentInOrderProducesConcatenation()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader first = new(1, 10, 100, PacketFlags.None, 0, 3);
        PacketHeader second = new(1, 10, 100, PacketFlags.None, 1, 3);
        PacketHeader third = new(1, 10, 100, PacketFlags.LastFragment, 2, 3);
        Assert.Null(reassembler.Offer(first, new byte[] { 0x01, 0x02 }));
        Assert.Null(reassembler.Offer(second, new byte[] { 0x03, 0x04 }));
        ReassembledNalUnit? result = reassembler.Offer(third, new byte[] { 0x05 });
        Assert.NotNull(result);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }, result!.Value.NalUnit);
    }

    [Fact]
    public void OutOfOrderFragmentsStillReassemble()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader first = new(1, 10, 100, PacketFlags.None, 0, 3);
        PacketHeader second = new(1, 10, 100, PacketFlags.None, 1, 3);
        PacketHeader third = new(1, 10, 100, PacketFlags.LastFragment, 2, 3);
        Assert.Null(reassembler.Offer(third, new byte[] { 0x05 }));
        Assert.Null(reassembler.Offer(first, new byte[] { 0x01, 0x02 }));
        ReassembledNalUnit? result = reassembler.Offer(second, new byte[] { 0x03, 0x04 });
        Assert.NotNull(result);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }, result!.Value.NalUnit);
    }

    [Fact]
    public void DuplicateFragmentIsIgnored()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader first = new(1, 10, 100, PacketFlags.None, 0, 2);
        PacketHeader duplicate = new(1, 10, 100, PacketFlags.None, 0, 2);
        PacketHeader second = new(1, 10, 100, PacketFlags.LastFragment, 1, 2);
        Assert.Null(reassembler.Offer(first, new byte[] { 0x01 }));
        Assert.Null(reassembler.Offer(duplicate, new byte[] { 0xFF }));  // should NOT overwrite
        ReassembledNalUnit? result = reassembler.Offer(second, new byte[] { 0x02 });
        Assert.NotNull(result);
        Assert.Equal(new byte[] { 0x01, 0x02 }, result!.Value.NalUnit);
    }

    [Fact]
    public void DifferentStreamsAreBufferedIndependently()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader streamOneFirst = new(1, 1, 1, PacketFlags.None, 0, 2);
        PacketHeader streamOneLast = new(1, 1, 1, PacketFlags.LastFragment, 1, 2);
        PacketHeader streamTwoFirst = new(2, 1, 1, PacketFlags.None, 0, 2);
        PacketHeader streamTwoLast = new(2, 1, 1, PacketFlags.LastFragment, 1, 2);
        Assert.Null(reassembler.Offer(streamOneFirst, new byte[] { 0x11 }));
        Assert.Null(reassembler.Offer(streamTwoFirst, new byte[] { 0x22 }));
        ReassembledNalUnit? one = reassembler.Offer(streamOneLast, new byte[] { 0x1A });
        ReassembledNalUnit? two = reassembler.Offer(streamTwoLast, new byte[] { 0x2A });
        Assert.Equal(new byte[] { 0x11, 0x1A }, one!.Value.NalUnit);
        Assert.Equal(new byte[] { 0x22, 0x2A }, two!.Value.NalUnit);
    }

    [Fact]
    public void PartialAssemblyIsDiscardedAfterTimeout()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader first = new(1, 10, 100, PacketFlags.None, 0, 2);
        PacketHeader second = new(1, 10, 100, PacketFlags.LastFragment, 1, 2);
        Assert.Null(reassembler.Offer(first, new byte[] { 0x01 }));
        clock.Advance(TimeSpan.FromMilliseconds(501));
        // Second fragment arrives too late — reassembler should treat this as a fresh (incomplete) batch
        // and NOT emit anything.
        Assert.Null(reassembler.Offer(second, new byte[] { 0x02 }));
        Assert.Equal(1, reassembler.PurgeExpired());
    }

    [Fact]
    public void PurgeExpiredRemovesOnlyTimedOutEntries()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader staleFirst = new(1, 10, 100, PacketFlags.None, 0, 2);
        Assert.Null(reassembler.Offer(staleFirst, new byte[] { 0x01 }));
        clock.Advance(TimeSpan.FromMilliseconds(300));
        PacketHeader freshFirst = new(1, 11, 100, PacketFlags.None, 0, 2);
        Assert.Null(reassembler.Offer(freshFirst, new byte[] { 0x02 }));
        clock.Advance(TimeSpan.FromMilliseconds(300));  // total 600 for sequence 10, 300 for sequence 11
        Assert.Equal(1, reassembler.PurgeExpired());
        PacketHeader freshLast = new(1, 11, 100, PacketFlags.LastFragment, 1, 2);
        ReassembledNalUnit? result = reassembler.Offer(freshLast, new byte[] { 0x03 });
        Assert.NotNull(result);
    }

    [Fact]
    public void OfferRejectsPayloadLargerThanMaximum()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader header = new(1, 1, 1, PacketFlags.LastFragment, 0, 1);
        Assert.Throws<ArgumentException>(
            () => reassembler.Offer(header, new byte[PacketHeader.MaximumPayloadByteLength + 1]));
    }

    [Fact]
    public void OfferRejectsNullPayload()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader header = new(1, 1, 1, PacketFlags.LastFragment, 0, 1);
        Assert.Throws<ArgumentNullException>(() => reassembler.Offer(header, null!));
    }

    [Fact]
    public void ConstructorRejectsNegativeTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NalReassembler(new FakeClock(), TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void ConstructorRejectsNullClock()
    {
        Assert.Throws<ArgumentNullException>(
            () => new NalReassembler(null!, TimeSpan.FromMilliseconds(500)));
    }
}
```

And `FakeClock.cs`:

```csharp
using System;
using WindowStream.Core.Transport;

namespace WindowStream.Core.Tests.Transport;

internal sealed class FakeClock : IClock
{
    private DateTimeOffset now = DateTimeOffset.UnixEpoch;
    public DateTimeOffset UtcNow => now;
    public void Advance(TimeSpan delta) => now += delta;
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~NalReassemblerTests
```

Expected: FAIL — missing `NalReassembler`, `IClock`, etc.

- [ ] **Step 3: Implement `IClock.cs` and `SystemClock.cs`**

`IClock.cs`:

```csharp
using System;

namespace WindowStream.Core.Transport;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
```

`SystemClock.cs`:

```csharp
using System;

namespace WindowStream.Core.Transport;

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new SystemClock();
    private SystemClock() { }
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
```

- [ ] **Step 4: Implement `ReassembledNalUnit.cs`**

```csharp
namespace WindowStream.Core.Transport;

public readonly record struct ReassembledNalUnit(
    uint StreamId,
    uint Sequence,
    ulong PresentationTimestampMicroseconds,
    bool IsIdrFrame,
    byte[] NalUnit);
```

- [ ] **Step 5: Implement `NalReassembler.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace WindowStream.Core.Transport;

public sealed class NalReassembler
{
    private readonly IClock clock;
    private readonly TimeSpan reassemblyTimeout;
    private readonly Dictionary<FragmentKey, FragmentBuffer> buffers = new();

    public NalReassembler(IClock clock, TimeSpan reassemblyTimeout)
    {
        if (clock is null)
        {
            throw new ArgumentNullException(nameof(clock));
        }
        if (reassemblyTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reassemblyTimeout), reassemblyTimeout, "timeout must be non-negative");
        }
        this.clock = clock;
        this.reassemblyTimeout = reassemblyTimeout;
    }

    public ReassembledNalUnit? Offer(PacketHeader header, byte[] payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }
        if (payload.Length > PacketHeader.MaximumPayloadByteLength)
        {
            throw new ArgumentException(
                $"payload {payload.Length} bytes exceeds maximum {PacketHeader.MaximumPayloadByteLength}",
                nameof(payload));
        }

        FragmentKey key = new(header.StreamId, header.Sequence);
        DateTimeOffset now = clock.UtcNow;

        if (!buffers.TryGetValue(key, out FragmentBuffer? buffer))
        {
            buffer = new FragmentBuffer(header.FragmentTotal, header.IsIdrFrame, header.PresentationTimestampMicroseconds, now);
            buffers[key] = buffer;
        }
        else if (now - buffer.FirstSeenAt > reassemblyTimeout)
        {
            // Previous partial assembly expired — discard it and start fresh.
            buffer = new FragmentBuffer(header.FragmentTotal, header.IsIdrFrame, header.PresentationTimestampMicroseconds, now);
            buffers[key] = buffer;
        }

        if (!buffer.AddFragment(header.FragmentIndex, payload))
        {
            return null;  // duplicate fragment index
        }

        if (!buffer.IsComplete)
        {
            return null;
        }

        buffers.Remove(key);
        return new ReassembledNalUnit(
            StreamId: header.StreamId,
            Sequence: header.Sequence,
            PresentationTimestampMicroseconds: buffer.PresentationTimestampMicroseconds,
            IsIdrFrame: buffer.IsIdrFrame,
            NalUnit: buffer.Concatenate());
    }

    public int PurgeExpired()
    {
        DateTimeOffset now = clock.UtcNow;
        List<FragmentKey> expiredKeys = new();
        foreach (KeyValuePair<FragmentKey, FragmentBuffer> entry in buffers)
        {
            if (now - entry.Value.FirstSeenAt > reassemblyTimeout)
            {
                expiredKeys.Add(entry.Key);
            }
        }
        foreach (FragmentKey expiredKey in expiredKeys)
        {
            buffers.Remove(expiredKey);
        }
        return expiredKeys.Count;
    }

    private readonly record struct FragmentKey(uint StreamId, uint Sequence);

    private sealed class FragmentBuffer
    {
        private readonly byte[]?[] fragments;
        private int receivedCount;

        public FragmentBuffer(int fragmentTotal, bool isIdrFrame, ulong presentationTimestampMicroseconds, DateTimeOffset firstSeenAt)
        {
            fragments = new byte[fragmentTotal][];
            IsIdrFrame = isIdrFrame;
            PresentationTimestampMicroseconds = presentationTimestampMicroseconds;
            FirstSeenAt = firstSeenAt;
        }

        public bool IsIdrFrame { get; }
        public ulong PresentationTimestampMicroseconds { get; }
        public DateTimeOffset FirstSeenAt { get; }
        public bool IsComplete => receivedCount == fragments.Length;

        public bool AddFragment(int index, byte[] payload)
        {
            if (fragments[index] is not null)
            {
                return false;
            }
            fragments[index] = payload;
            receivedCount++;
            return true;
        }

        public byte[] Concatenate()
        {
            int totalLength = 0;
            for (int index = 0; index < fragments.Length; index++)
            {
                totalLength += fragments[index]!.Length;
            }
            byte[] result = new byte[totalLength];
            int cursor = 0;
            for (int index = 0; index < fragments.Length; index++)
            {
                byte[] fragment = fragments[index]!;
                Array.Copy(fragment, 0, result, cursor, fragment.Length);
                cursor += fragment.Length;
            }
            return result;
        }
    }
}
```

- [ ] **Step 6: Run test to verify it passes**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~NalReassemblerTests
```

Expected: `Passed: 12, Failed: 0`. Coverage on `Transport/` reports 100% line + branch.

- [ ] **Step 7: Commit**

```bash
git add src/WindowStream.Core/Transport/ReassembledNalUnit.cs src/WindowStream.Core/Transport/NalReassembler.cs src/WindowStream.Core/Transport/IClock.cs src/WindowStream.Core/Transport/SystemClock.cs tests/WindowStream.Core.Tests/Transport/NalReassemblerTests.cs tests/WindowStream.Core.Tests/Transport/FakeClock.cs
git commit -m "feat(transport): NAL reassembler with 500ms timeout, out-of-order and dup handling"
```

---

## Phase 5: Session state machine

### Task 11: `SessionState` enum and `InvalidSessionTransitionException`

**Files:**
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Session/SessionState.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Session/InvalidSessionTransitionException.cs`
- Test: `C:/Users/mtsch/WindowStream/tests/WindowStream.Core.Tests/Session/InvalidSessionTransitionExceptionTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using WindowStream.Core.Session;
using Xunit;

namespace WindowStream.Core.Tests.Session;

public sealed class InvalidSessionTransitionExceptionTests
{
    [Fact]
    public void MessageMentionsFromAndToStates()
    {
        InvalidSessionTransitionException exception = new(SessionState.Stopped, SessionState.Capturing);
        Assert.Contains("Stopped", exception.Message);
        Assert.Contains("Capturing", exception.Message);
        Assert.Equal(SessionState.Stopped, exception.FromState);
        Assert.Equal(SessionState.Capturing, exception.ToState);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~InvalidSessionTransitionExceptionTests
```

Expected: FAIL — `SessionState` / `InvalidSessionTransitionException` missing.

- [ ] **Step 3: Implement `SessionState.cs`**

```csharp
namespace WindowStream.Core.Session;

public enum SessionState
{
    Idle,
    Capturing,
    Streaming,
    Stopped
}
```

- [ ] **Step 4: Implement `InvalidSessionTransitionException.cs`**

```csharp
using System;

namespace WindowStream.Core.Session;

public sealed class InvalidSessionTransitionException : InvalidOperationException
{
    public InvalidSessionTransitionException(SessionState fromState, SessionState toState)
        : base($"invalid session transition: {fromState} -> {toState}")
    {
        FromState = fromState;
        ToState = toState;
    }

    public SessionState FromState { get; }
    public SessionState ToState { get; }
}
```

- [ ] **Step 5: Run test to verify it passes**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~InvalidSessionTransitionExceptionTests
```

Expected: `Passed: 1, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/WindowStream.Core/Session/ tests/WindowStream.Core.Tests/Session/
git commit -m "feat(session): SessionState enum and invalid-transition exception"
```

---

### Task 12: `Session` owner with lifecycle methods and validated transitions

**Files:**
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Session/ISessionObserver.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Session/SessionStateChangedEventArguments.cs`
- Create: `C:/Users/mtsch/WindowStream/src/WindowStream.Core/Session/Session.cs`
- Test: `C:/Users/mtsch/WindowStream/tests/WindowStream.Core.Tests/Session/SessionTests.cs`

- [ ] **Step 1: Write the failing test — happy path + every invalid transition**

```csharp
using System.Collections.Generic;
using WindowStream.Core.Session;
using Xunit;

namespace WindowStream.Core.Tests.Session;

public sealed class SessionTests
{
    [Fact]
    public void NewSessionStartsIdle()
    {
        Session session = new();
        Assert.Equal(SessionState.Idle, session.CurrentState);
    }

    [Fact]
    public void HappyPathTransitionsIdleToCapturingToStreamingToStopped()
    {
        Session session = new();
        session.BeginCapturing();
        Assert.Equal(SessionState.Capturing, session.CurrentState);
        session.BeginStreaming();
        Assert.Equal(SessionState.Streaming, session.CurrentState);
        session.Stop();
        Assert.Equal(SessionState.Stopped, session.CurrentState);
    }

    [Fact]
    public void TransitionsRaiseStateChangedEventWithOldAndNew()
    {
        Session session = new();
        List<SessionStateChangedEventArguments> transitions = new();
        session.StateChanged += (_, eventArguments) => transitions.Add(eventArguments);
        session.BeginCapturing();
        session.BeginStreaming();
        session.Stop();
        Assert.Equal(3, transitions.Count);
        Assert.Equal(SessionState.Idle, transitions[0].FromState);
        Assert.Equal(SessionState.Capturing, transitions[0].ToState);
        Assert.Equal(SessionState.Capturing, transitions[1].FromState);
        Assert.Equal(SessionState.Streaming, transitions[1].ToState);
        Assert.Equal(SessionState.Streaming, transitions[2].FromState);
        Assert.Equal(SessionState.Stopped, transitions[2].ToState);
    }

    [Fact]
    public void ViewerDisconnectedFromStreamingReturnsToCapturing()
    {
        Session session = new();
        session.BeginCapturing();
        session.BeginStreaming();
        session.ViewerDisconnected();
        Assert.Equal(SessionState.Capturing, session.CurrentState);
    }

    [Fact]
    public void ViewerReconnectedFromCapturingReturnsToStreaming()
    {
        Session session = new();
        session.BeginCapturing();
        session.BeginStreaming();
        session.ViewerDisconnected();
        session.BeginStreaming();  // reconnect path is the same transition as initial viewer connect
        Assert.Equal(SessionState.Streaming, session.CurrentState);
    }

    [Fact]
    public void BeginCapturingFromCapturingThrows()
    {
        Session session = new();
        session.BeginCapturing();
        InvalidSessionTransitionException exception = Assert.Throws<InvalidSessionTransitionException>(
            () => session.BeginCapturing());
        Assert.Equal(SessionState.Capturing, exception.FromState);
        Assert.Equal(SessionState.Capturing, exception.ToState);
    }

    [Fact]
    public void BeginCapturingFromStreamingThrows()
    {
        Session session = new();
        session.BeginCapturing();
        session.BeginStreaming();
        Assert.Throws<InvalidSessionTransitionException>(() => session.BeginCapturing());
    }

    [Fact]
    public void BeginCapturingFromStoppedThrows()
    {
        Session session = new();
        session.BeginCapturing();
        session.Stop();
        Assert.Throws<InvalidSessionTransitionException>(() => session.BeginCapturing());
    }

    [Fact]
    public void BeginStreamingFromIdleThrows()
    {
        Session session = new();
        Assert.Throws<InvalidSessionTransitionException>(() => session.BeginStreaming());
    }

    [Fact]
    public void BeginStreamingFromStreamingThrows()
    {
        Session session = new();
        session.BeginCapturing();
        session.BeginStreaming();
        Assert.Throws<InvalidSessionTransitionException>(() => session.BeginStreaming());
    }

    [Fact]
    public void BeginStreamingFromStoppedThrows()
    {
        Session session = new();
        session.BeginCapturing();
        session.Stop();
        Assert.Throws<InvalidSessionTransitionException>(() => session.BeginStreaming());
    }

    [Fact]
    public void ViewerDisconnectedFromIdleThrows()
    {
        Session session = new();
        Assert.Throws<InvalidSessionTransitionException>(() => session.ViewerDisconnected());
    }

    [Fact]
    public void ViewerDisconnectedFromCapturingThrows()
    {
        Session session = new();
        session.BeginCapturing();
        Assert.Throws<InvalidSessionTransitionException>(() => session.ViewerDisconnected());
    }

    [Fact]
    public void ViewerDisconnectedFromStoppedThrows()
    {
        Session session = new();
        session.BeginCapturing();
        session.Stop();
        Assert.Throws<InvalidSessionTransitionException>(() => session.ViewerDisconnected());
    }

    [Fact]
    public void StopFromIdleThrows()
    {
        Session session = new();
        Assert.Throws<InvalidSessionTransitionException>(() => session.Stop());
    }

    [Fact]
    public void StopFromCapturingSucceeds()
    {
        Session session = new();
        session.BeginCapturing();
        session.Stop();
        Assert.Equal(SessionState.Stopped, session.CurrentState);
    }

    [Fact]
    public void StopFromStreamingSucceeds()
    {
        Session session = new();
        session.BeginCapturing();
        session.BeginStreaming();
        session.Stop();
        Assert.Equal(SessionState.Stopped, session.CurrentState);
    }

    [Fact]
    public void StopFromStoppedThrows()
    {
        Session session = new();
        session.BeginCapturing();
        session.Stop();
        Assert.Throws<InvalidSessionTransitionException>(() => session.Stop());
    }

    [Fact]
    public void EventArgumentsRecordValuesMatchProperties()
    {
        SessionStateChangedEventArguments args = new(SessionState.Idle, SessionState.Capturing);
        Assert.Equal(SessionState.Idle, args.FromState);
        Assert.Equal(SessionState.Capturing, args.ToState);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~SessionTests
```

Expected: FAIL — `Session`, `SessionStateChangedEventArguments` missing.

- [ ] **Step 3: Implement `SessionStateChangedEventArguments.cs`**

```csharp
using System;

namespace WindowStream.Core.Session;

public sealed class SessionStateChangedEventArguments : EventArgs
{
    public SessionStateChangedEventArguments(SessionState fromState, SessionState toState)
    {
        FromState = fromState;
        ToState = toState;
    }

    public SessionState FromState { get; }
    public SessionState ToState { get; }
}
```

- [ ] **Step 4: Implement `ISessionObserver.cs` (used in phase 8 wiring; included now so the interface is stable)**

```csharp
namespace WindowStream.Core.Session;

public interface ISessionObserver
{
    void OnStateChanged(SessionState fromState, SessionState toState);
}
```

- [ ] **Step 5: Implement `Session.cs`**

```csharp
using System;

namespace WindowStream.Core.Session;

public sealed class Session
{
    public SessionState CurrentState { get; private set; } = SessionState.Idle;

    public event EventHandler<SessionStateChangedEventArguments>? StateChanged;

    public void BeginCapturing()
    {
        TransitionTo(SessionState.Capturing, allowedFromStates: new[] { SessionState.Idle });
    }

    public void BeginStreaming()
    {
        TransitionTo(SessionState.Streaming, allowedFromStates: new[] { SessionState.Capturing });
    }

    public void ViewerDisconnected()
    {
        TransitionTo(SessionState.Capturing, allowedFromStates: new[] { SessionState.Streaming });
    }

    public void Stop()
    {
        TransitionTo(SessionState.Stopped, allowedFromStates: new[] { SessionState.Capturing, SessionState.Streaming });
    }

    private void TransitionTo(SessionState toState, SessionState[] allowedFromStates)
    {
        bool allowed = false;
        for (int index = 0; index < allowedFromStates.Length; index++)
        {
            if (allowedFromStates[index] == CurrentState)
            {
                allowed = true;
                break;
            }
        }
        if (!allowed)
        {
            throw new InvalidSessionTransitionException(CurrentState, toState);
        }
        SessionState fromState = CurrentState;
        CurrentState = toState;
        StateChanged?.Invoke(this, new SessionStateChangedEventArguments(fromState, toState));
    }
}
```

- [ ] **Step 6: Run test to verify it passes**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~SessionTests
```

Expected: `Passed: 18, Failed: 0`.

- [ ] **Step 7: Verify full-suite coverage threshold holds**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj
```

Expected: `Passed: 64+`, and Coverlet summary reports `Total | 100% | 100% | 100%` for line, branch, and method. If the 100% threshold is not met, the build FAILS with `ThresholdStat total` messages — fix the gap before committing.

- [ ] **Step 8: Commit**

```bash
git add src/WindowStream.Core/Session/ tests/WindowStream.Core.Tests/Session/
git commit -m "feat(session): Session state machine with validated transitions"
```

## Phase 6: mDNS ServerAdvertiser

**Library choice:** `Makaretu.Dns.Multicast` (package `Makaretu.Dns`). Pure-managed, publishes services with TXT records via `ServiceDiscovery.Advertise`, supports loopback by listening on the same `MulticastService` — justifying the pick because Tmds.MDns is Linux-leaning and has a narrower service-publication API on .NET 8. The advertiser is portable and therefore compiles under every target framework (`net8.0`, `net8.0-windows10.0.19041.0`, `net8.0-maccatalyst`).

### Task 13: TXT record builder

**Files:**
- Create: `src/WindowStream.Core/Discovery/AdvertisementOptions.cs`
- Create: `src/WindowStream.Core/Discovery/ServiceTextRecords.cs`
- Test: `tests/WindowStream.Core.Tests/Discovery/ServiceTextRecordsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using Xunit;

namespace WindowStream.Core.Tests.Discovery;

public sealed class ServiceTextRecordsTests
{
    [Fact]
    public void Build_EmitsRequiredKeys()
    {
        AdvertisementOptions options = new AdvertisementOptions(
            hostname: "mtsch-desktop",
            protocolMajorVersion: 1,
            protocolRevision: 1);

        IReadOnlyList<string> records = ServiceTextRecords.Build(options);

        Assert.Contains("version=1", records);
        Assert.Contains("hostname=mtsch-desktop", records);
        Assert.Contains("protocolRev=1", records);
        Assert.Equal(3, records.Count);
    }

    [Fact]
    public void Build_RejectsEmptyHostname()
    {
        AdvertisementOptions options = new AdvertisementOptions(
            hostname: "",
            protocolMajorVersion: 1,
            protocolRevision: 1);

        Assert.Throws<System.ArgumentException>(() => ServiceTextRecords.Build(options));
    }

    [Fact]
    public void Build_RejectsHostnameWithEqualsSign()
    {
        AdvertisementOptions options = new AdvertisementOptions(
            hostname: "bad=name",
            protocolMajorVersion: 1,
            protocolRevision: 1);

        Assert.Throws<System.ArgumentException>(() => ServiceTextRecords.Build(options));
    }

    [Fact]
    public void Build_RejectsNegativeVersion()
    {
        AdvertisementOptions options = new AdvertisementOptions(
            hostname: "h",
            protocolMajorVersion: -1,
            protocolRevision: 0);

        Assert.Throws<System.ArgumentOutOfRangeException>(() => ServiceTextRecords.Build(options));
    }

    [Fact]
    public void Build_RejectsNegativeRevision()
    {
        AdvertisementOptions options = new AdvertisementOptions(
            hostname: "h",
            protocolMajorVersion: 1,
            protocolRevision: -1);

        Assert.Throws<System.ArgumentOutOfRangeException>(() => ServiceTextRecords.Build(options));
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~ServiceTextRecordsTests"`
Expected: FAIL — `ServiceTextRecords`/`AdvertisementOptions` do not exist.

- [ ] **Step 3: Add the options record**

```csharp
namespace WindowStream.Core.Discovery;

public sealed record AdvertisementOptions(
    string hostname,
    int protocolMajorVersion,
    int protocolRevision);
```

- [ ] **Step 4: Add the text-record builder**

```csharp
using System;
using System.Collections.Generic;

namespace WindowStream.Core.Discovery;

public static class ServiceTextRecords
{
    public static IReadOnlyList<string> Build(AdvertisementOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (options.protocolMajorVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.protocolMajorVersion,
                "Protocol major version must be non-negative.");
        }

        if (options.protocolRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.protocolRevision,
                "Protocol revision must be non-negative.");
        }

        if (string.IsNullOrWhiteSpace(options.hostname))
        {
            throw new ArgumentException("Hostname must not be empty.", nameof(options));
        }

        if (options.hostname.Contains('='))
        {
            throw new ArgumentException("Hostname must not contain '='.", nameof(options));
        }

        return new string[]
        {
            "version=" + options.protocolMajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hostname=" + options.hostname,
            "protocolRev=" + options.protocolRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
```

- [ ] **Step 5: Run tests to confirm pass**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~ServiceTextRecordsTests"`
Expected: PASS (5/5).

- [ ] **Step 6: Commit**

```bash
git add src/WindowStream.Core/Discovery/AdvertisementOptions.cs src/WindowStream.Core/Discovery/ServiceTextRecords.cs tests/WindowStream.Core.Tests/Discovery/ServiceTextRecordsTests.cs
git commit -m "feat(core): add mDNS TXT record builder with validation"
```

### Task 14: ServerAdvertiser publishing lifecycle

**Files:**
- Create: `src/WindowStream.Core/Discovery/IMulticastServiceHost.cs`
- Create: `src/WindowStream.Core/Discovery/MakaretuMulticastServiceHost.cs`
- Create: `src/WindowStream.Core/Discovery/ServerAdvertiser.cs`
- Modify: `src/WindowStream.Core/WindowStream.Core.csproj` (add `Makaretu.Dns` NuGet reference)
- Test: `tests/WindowStream.Core.Tests/Discovery/ServerAdvertiserTests.cs`

- [ ] **Step 1: Write the failing test using an injected fake host**

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WindowStream.Core.Tests.Discovery;

public sealed class ServerAdvertiserTests
{
    private sealed class FakeMulticastServiceHost : WindowStream.Core.Discovery.IMulticastServiceHost
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public string? ServiceInstance { get; private set; }
        public string? ServiceType { get; private set; }
        public int? Port { get; private set; }
        public IReadOnlyList<string>? TextRecords { get; private set; }

        public Task StartAdvertisingAsync(
            string serviceInstance,
            string serviceType,
            int port,
            IReadOnlyList<string> textRecords,
            CancellationToken cancellationToken)
        {
            StartCount++;
            ServiceInstance = serviceInstance;
            ServiceType = serviceType;
            Port = port;
            TextRecords = textRecords;
            return Task.CompletedTask;
        }

        public Task StopAdvertisingAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task StartAsync_PublishesExpectedServiceTypeAndText()
    {
        FakeMulticastServiceHost host = new FakeMulticastServiceHost();
        WindowStream.Core.Discovery.AdvertisementOptions options =
            new WindowStream.Core.Discovery.AdvertisementOptions("desk", 1, 1);

        await using WindowStream.Core.Discovery.ServerAdvertiser advertiser =
            new WindowStream.Core.Discovery.ServerAdvertiser(host);

        await advertiser.StartAsync(options, controlPort: 47813, CancellationToken.None);

        Assert.Equal(1, host.StartCount);
        Assert.Equal("_windowstream._tcp.local.", host.ServiceType);
        Assert.Equal("desk", host.ServiceInstance);
        Assert.Equal(47813, host.Port);
        Assert.NotNull(host.TextRecords);
        Assert.Contains("version=1", host.TextRecords!);
        Assert.Contains("hostname=desk", host.TextRecords!);
        Assert.Contains("protocolRev=1", host.TextRecords!);
    }

    [Fact]
    public async Task StartAsync_Twice_ThrowsInvalidOperation()
    {
        FakeMulticastServiceHost host = new FakeMulticastServiceHost();
        await using WindowStream.Core.Discovery.ServerAdvertiser advertiser =
            new WindowStream.Core.Discovery.ServerAdvertiser(host);

        await advertiser.StartAsync(
            new WindowStream.Core.Discovery.AdvertisementOptions("desk", 1, 1),
            controlPort: 1,
            CancellationToken.None);

        await Assert.ThrowsAsync<System.InvalidOperationException>(() =>
            advertiser.StartAsync(
                new WindowStream.Core.Discovery.AdvertisementOptions("desk", 1, 1),
                controlPort: 1,
                CancellationToken.None));
    }

    [Fact]
    public async Task StopAsync_BeforeStart_IsNoOp()
    {
        FakeMulticastServiceHost host = new FakeMulticastServiceHost();
        await using WindowStream.Core.Discovery.ServerAdvertiser advertiser =
            new WindowStream.Core.Discovery.ServerAdvertiser(host);

        await advertiser.StopAsync(CancellationToken.None);

        Assert.Equal(0, host.StopCount);
    }

    [Fact]
    public async Task DisposeAsync_StopsIfStarted()
    {
        FakeMulticastServiceHost host = new FakeMulticastServiceHost();
        WindowStream.Core.Discovery.ServerAdvertiser advertiser =
            new WindowStream.Core.Discovery.ServerAdvertiser(host);
        await advertiser.StartAsync(
            new WindowStream.Core.Discovery.AdvertisementOptions("desk", 1, 1),
            controlPort: 1,
            CancellationToken.None);

        await advertiser.DisposeAsync();

        Assert.Equal(1, host.StopCount);
    }

    [Fact]
    public async Task StartAsync_PortBelowOne_Throws()
    {
        FakeMulticastServiceHost host = new FakeMulticastServiceHost();
        await using WindowStream.Core.Discovery.ServerAdvertiser advertiser =
            new WindowStream.Core.Discovery.ServerAdvertiser(host);

        await Assert.ThrowsAsync<System.ArgumentOutOfRangeException>(() =>
            advertiser.StartAsync(
                new WindowStream.Core.Discovery.AdvertisementOptions("desk", 1, 1),
                controlPort: 0,
                CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~ServerAdvertiserTests"`
Expected: FAIL — types missing.

- [ ] **Step 3: Add the interface**

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WindowStream.Core.Discovery;

public interface IMulticastServiceHost : System.IAsyncDisposable
{
    Task StartAdvertisingAsync(
        string serviceInstance,
        string serviceType,
        int port,
        IReadOnlyList<string> textRecords,
        CancellationToken cancellationToken);

    Task StopAdvertisingAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Add the advertiser**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WindowStream.Core.Discovery;

public sealed class ServerAdvertiser : IAsyncDisposable
{
    public const string ServiceType = "_windowstream._tcp.local.";

    private readonly IMulticastServiceHost multicastServiceHost;
    private bool started;
    private bool disposed;

    public ServerAdvertiser(IMulticastServiceHost multicastServiceHost)
    {
        this.multicastServiceHost = multicastServiceHost
            ?? throw new ArgumentNullException(nameof(multicastServiceHost));
    }

    public async Task StartAsync(
        AdvertisementOptions options,
        int controlPort,
        CancellationToken cancellationToken)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        if (controlPort < 1 || controlPort > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(controlPort),
                controlPort,
                "controlPort must be in [1, 65535].");
        }
        if (started)
        {
            throw new InvalidOperationException("ServerAdvertiser has already been started.");
        }

        IReadOnlyList<string> textRecords = ServiceTextRecords.Build(options);
        await multicastServiceHost.StartAdvertisingAsync(
            serviceInstance: options.hostname,
            serviceType: ServiceType,
            port: controlPort,
            textRecords: textRecords,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        started = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!started)
        {
            return;
        }
        await multicastServiceHost.StopAdvertisingAsync(cancellationToken).ConfigureAwait(false);
        started = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await multicastServiceHost.DisposeAsync().ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 5: Run tests to confirm pass**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~ServerAdvertiserTests"`
Expected: PASS (5/5).

- [ ] **Step 6: Commit**

```bash
git add src/WindowStream.Core/Discovery/IMulticastServiceHost.cs src/WindowStream.Core/Discovery/ServerAdvertiser.cs tests/WindowStream.Core.Tests/Discovery/ServerAdvertiserTests.cs
git commit -m "feat(core): add ServerAdvertiser lifecycle over injected multicast host"
```

### Task 15: Makaretu multicast host adapter + loopback integration test

**Files:**
- Modify: `src/WindowStream.Core/WindowStream.Core.csproj` (add `<PackageReference Include="Makaretu.Dns" Version="2.0.1" />`)
- Create: `src/WindowStream.Core/Discovery/MakaretuMulticastServiceHost.cs`
- Test: `tests/WindowStream.Integration.Tests/Discovery/ServerAdvertiserLoopbackTests.cs`

- [ ] **Step 1: Add the NuGet reference**

```xml
<ItemGroup>
  <PackageReference Include="Makaretu.Dns" Version="2.0.1" />
</ItemGroup>
```

- [ ] **Step 2: Write the failing loopback integration test**

```csharp
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;
using Xunit;

namespace WindowStream.Integration.Tests.Discovery;

public sealed class ServerAdvertiserLoopbackTests
{
    [Fact(Timeout = 10000)]
    public async Task Advertised_Service_Is_Visible_To_Local_ServiceDiscovery()
    {
        WindowStream.Core.Discovery.MakaretuMulticastServiceHost host =
            new WindowStream.Core.Discovery.MakaretuMulticastServiceHost();
        await using WindowStream.Core.Discovery.ServerAdvertiser advertiser =
            new WindowStream.Core.Discovery.ServerAdvertiser(host);

        string uniqueHostname = "wstest-" + Guid.NewGuid().ToString("N")[..8];
        WindowStream.Core.Discovery.AdvertisementOptions options =
            new WindowStream.Core.Discovery.AdvertisementOptions(uniqueHostname, 1, 1);

        await advertiser.StartAsync(options, controlPort: 48000, CancellationToken.None);

        TaskCompletionSource<ServiceInstanceDiscoveryEventArgs> discovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using MulticastService listener = new MulticastService();
        using ServiceDiscovery discovery = new ServiceDiscovery(listener);
        discovery.ServiceInstanceDiscovered += (sender, eventArguments) =>
        {
            if (eventArguments.ServiceInstanceName.ToString()
                .StartsWith(uniqueHostname, StringComparison.OrdinalIgnoreCase))
            {
                discovered.TrySetResult(eventArguments);
            }
        };
        listener.Start();
        discovery.QueryServiceInstances(WindowStream.Core.Discovery.ServerAdvertiser.ServiceType);

        ServiceInstanceDiscoveryEventArgs hit = await discovered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Contains(uniqueHostname, hit.ServiceInstanceName.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 3: Run it to see it fail**

Run: `dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~ServerAdvertiserLoopbackTests"`
Expected: FAIL — `MakaretuMulticastServiceHost` undefined.

- [ ] **Step 4: Implement the adapter**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;

namespace WindowStream.Core.Discovery;

public sealed class MakaretuMulticastServiceHost : IMulticastServiceHost
{
    private MulticastService? multicastService;
    private ServiceDiscovery? serviceDiscovery;
    private ServiceProfile? serviceProfile;

    public Task StartAdvertisingAsync(
        string serviceInstance,
        string serviceType,
        int port,
        IReadOnlyList<string> textRecords,
        CancellationToken cancellationToken)
    {
        if (multicastService is not null)
        {
            throw new InvalidOperationException("Already advertising.");
        }

        // ServiceProfile expects a service type in the form "_windowstream._tcp"
        // (without the trailing ".local."). Strip if present.
        string normalizedType = serviceType;
        if (normalizedType.EndsWith(".local.", StringComparison.OrdinalIgnoreCase))
        {
            normalizedType = normalizedType[..^".local.".Length];
        }

        ServiceProfile profile = new ServiceProfile(
            instanceName: serviceInstance,
            serviceName: normalizedType,
            port: (ushort)port);

        foreach (string record in textRecords)
        {
            profile.AddProperty(
                key: record.Split('=', 2)[0],
                value: record.Split('=', 2).Length == 2 ? record.Split('=', 2)[1] : string.Empty);
        }

        MulticastService multicast = new MulticastService();
        ServiceDiscovery discovery = new ServiceDiscovery(multicast);
        discovery.Advertise(profile);
        multicast.Start();

        multicastService = multicast;
        serviceDiscovery = discovery;
        serviceProfile = profile;
        return Task.CompletedTask;
    }

    public Task StopAdvertisingAsync(CancellationToken cancellationToken)
    {
        if (serviceDiscovery is not null && serviceProfile is not null)
        {
            serviceDiscovery.Unadvertise(serviceProfile);
        }
        multicastService?.Stop();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        serviceDiscovery?.Dispose();
        multicastService?.Dispose();
        serviceDiscovery = null;
        multicastService = null;
        serviceProfile = null;
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 5: Rerun loopback test**

Run: `dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~ServerAdvertiserLoopbackTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/WindowStream.Core/WindowStream.Core.csproj src/WindowStream.Core/Discovery/MakaretuMulticastServiceHost.cs tests/WindowStream.Integration.Tests/Discovery/ServerAdvertiserLoopbackTests.cs
git commit -m "feat(core): add Makaretu mDNS adapter with loopback advertisement test"
```

---

## Phase 7: Capture abstraction + WGC implementation

### Task 16: Capture data types and interface

**Files:**
- Create: `src/WindowStream.Core/Capture/WindowHandle.cs`
- Create: `src/WindowStream.Core/Capture/WindowInformation.cs`
- Create: `src/WindowStream.Core/Capture/CaptureOptions.cs`
- Create: `src/WindowStream.Core/Capture/CapturedFrame.cs`
- Create: `src/WindowStream.Core/Capture/PixelFormat.cs`
- Create: `src/WindowStream.Core/Capture/IWindowCapture.cs`
- Create: `src/WindowStream.Core/Capture/IWindowCaptureSource.cs`
- Create: `src/WindowStream.Core/Capture/WindowCaptureException.cs`
- Create: `src/WindowStream.Core/Capture/WindowGoneException.cs`
- Test: `tests/WindowStream.Core.Tests/Capture/CapturedFrameTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using Xunit;

namespace WindowStream.Core.Tests.Capture;

public sealed class CapturedFrameTests
{
    [Fact]
    public void Constructor_PopulatesAllProperties()
    {
        byte[] buffer = new byte[3 * 2 * 4];
        WindowStream.Core.Capture.CapturedFrame frame = new WindowStream.Core.Capture.CapturedFrame(
            widthPixels: 3,
            heightPixels: 2,
            rowStrideBytes: 12,
            pixelFormat: WindowStream.Core.Capture.PixelFormat.Bgra32,
            presentationTimestampMicroseconds: 1_234_567,
            pixelBuffer: buffer);

        Assert.Equal(3, frame.widthPixels);
        Assert.Equal(2, frame.heightPixels);
        Assert.Equal(12, frame.rowStrideBytes);
        Assert.Equal(WindowStream.Core.Capture.PixelFormat.Bgra32, frame.pixelFormat);
        Assert.Equal(1_234_567L, frame.presentationTimestampMicroseconds);
        Assert.Equal(buffer.Length, frame.pixelBuffer.Length);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowStream.Core.Capture.CapturedFrame(
                0, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, new byte[4]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowStream.Core.Capture.CapturedFrame(
                1, 0, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, new byte[4]));
    }

    [Fact]
    public void Constructor_RejectsStrideSmallerThanRow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowStream.Core.Capture.CapturedFrame(
                10, 2, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, new byte[80]));
    }

    [Fact]
    public void Constructor_RejectsBufferTooSmall()
    {
        Assert.Throws<ArgumentException>(() =>
            new WindowStream.Core.Capture.CapturedFrame(
                10, 2, 40, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, new byte[4]));
    }

    [Fact]
    public void Constructor_AllowsZeroTimestamp()
    {
        WindowStream.Core.Capture.CapturedFrame frame = new WindowStream.Core.Capture.CapturedFrame(
            1, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, new byte[4]);
        Assert.Equal(0L, frame.presentationTimestampMicroseconds);
    }

    [Fact]
    public void Constructor_RejectsNegativeTimestamp()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowStream.Core.Capture.CapturedFrame(
                1, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, -1, new byte[4]));
    }
}
```

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~CapturedFrameTests"`
Expected: FAIL — types missing.

- [ ] **Step 3: Add the types**

`WindowHandle.cs`:
```csharp
namespace WindowStream.Core.Capture;

public readonly record struct WindowHandle(long value)
{
    public override string ToString() => "0x" + value.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
}
```

`WindowInformation.cs`:
```csharp
namespace WindowStream.Core.Capture;

public sealed record WindowInformation(
    WindowHandle handle,
    string title,
    string processName,
    int widthPixels,
    int heightPixels);
```

`CaptureOptions.cs`:
```csharp
namespace WindowStream.Core.Capture;

public sealed record CaptureOptions(
    int targetFramesPerSecond,
    bool includeCursor);
```

`PixelFormat.cs`:
```csharp
namespace WindowStream.Core.Capture;

public enum PixelFormat
{
    Bgra32 = 0,
    Nv12 = 1,
}
```

`CapturedFrame.cs`:
```csharp
using System;

namespace WindowStream.Core.Capture;

public sealed class CapturedFrame
{
    public int widthPixels { get; }
    public int heightPixels { get; }
    public int rowStrideBytes { get; }
    public PixelFormat pixelFormat { get; }
    public long presentationTimestampMicroseconds { get; }
    public ReadOnlyMemory<byte> pixelBuffer { get; }

    public CapturedFrame(
        int widthPixels,
        int heightPixels,
        int rowStrideBytes,
        PixelFormat pixelFormat,
        long presentationTimestampMicroseconds,
        ReadOnlyMemory<byte> pixelBuffer)
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
        this.pixelBuffer = pixelBuffer;
    }
}
```

`WindowCaptureException.cs`:
```csharp
using System;

namespace WindowStream.Core.Capture;

public class WindowCaptureException : Exception
{
    public WindowCaptureException(string message) : base(message) { }
    public WindowCaptureException(string message, Exception innerException) : base(message, innerException) { }
}
```

`WindowGoneException.cs`:
```csharp
using System;

namespace WindowStream.Core.Capture;

public sealed class WindowGoneException : WindowCaptureException
{
    public WindowHandle handle { get; }

    public WindowGoneException(WindowHandle handle)
        : base("Captured window no longer exists: " + handle)
    {
        this.handle = handle;
    }

    public WindowGoneException(WindowHandle handle, Exception innerException)
        : base("Captured window no longer exists: " + handle, innerException)
    {
        this.handle = handle;
    }
}
```

`IWindowCapture.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Threading;

namespace WindowStream.Core.Capture;

public interface IWindowCapture : IAsyncDisposable
{
    IAsyncEnumerable<CapturedFrame> Frames { get; }
    WindowHandle handle { get; }
    CaptureOptions options { get; }
}
```

`IWindowCaptureSource.cs`:
```csharp
using System.Collections.Generic;
using System.Threading;

namespace WindowStream.Core.Capture;

public interface IWindowCaptureSource
{
    IEnumerable<WindowInformation> ListWindows();
    IWindowCapture Start(WindowHandle handle, CaptureOptions options, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Run tests to confirm pass**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~CapturedFrameTests"`
Expected: PASS (6/6).

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Capture tests/WindowStream.Core.Tests/Capture/CapturedFrameTests.cs
git commit -m "feat(core): add capture interface and frame value type"
```

### Task 17: Fake window capture source for deterministic unit tests

**Files:**
- Create: `src/WindowStream.Core/Capture/Testing/FakeWindowCaptureSource.cs`
- Create: `src/WindowStream.Core/Capture/Testing/FakeWindowCapture.cs`
- Test: `tests/WindowStream.Core.Tests/Capture/Testing/FakeWindowCaptureSourceTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Capture.Testing;

public sealed class FakeWindowCaptureSourceTests
{
    [Fact]
    public void ListWindows_ReturnsConfiguredEntries()
    {
        FakeWindowCaptureSource source = new FakeWindowCaptureSource(
            new[]
            {
                new WindowInformation(new WindowHandle(1), "Notepad", "notepad.exe", 640, 480),
                new WindowInformation(new WindowHandle(2), "VS", "devenv.exe", 1920, 1080),
            });

        List<WindowInformation> list = source.ListWindows().ToList();

        Assert.Equal(2, list.Count);
        Assert.Equal("Notepad", list[0].title);
    }

    [Fact]
    public void Start_UnknownHandle_ThrowsWindowGone()
    {
        FakeWindowCaptureSource source = new FakeWindowCaptureSource(System.Array.Empty<WindowInformation>());
        Assert.Throws<WindowGoneException>(() =>
            source.Start(new WindowHandle(99), new CaptureOptions(60, false), CancellationToken.None));
    }

    [Fact]
    public async Task Start_EmitsConfiguredFrames_ThenCompletes()
    {
        WindowInformation window = new WindowInformation(new WindowHandle(1), "W", "p", 4, 2);
        FakeWindowCaptureSource source = new FakeWindowCaptureSource(new[] { window });
        source.EnqueueFrame(window.handle, BuildSolidFrame(4, 2, 0x11));
        source.EnqueueFrame(window.handle, BuildSolidFrame(4, 2, 0x22));
        source.CompleteAfterEnqueued(window.handle);

        await using IWindowCapture capture = source.Start(
            window.handle, new CaptureOptions(60, false), CancellationToken.None);

        List<CapturedFrame> collected = new List<CapturedFrame>();
        await foreach (CapturedFrame frame in capture.Frames.WithCancellation(CancellationToken.None))
        {
            collected.Add(frame);
        }
        Assert.Equal(2, collected.Count);
        Assert.Equal(0x11, collected[0].pixelBuffer.Span[0]);
        Assert.Equal(0x22, collected[1].pixelBuffer.Span[0]);
    }

    [Fact]
    public async Task Start_HonorsCancellation()
    {
        WindowInformation window = new WindowInformation(new WindowHandle(1), "W", "p", 4, 2);
        FakeWindowCaptureSource source = new FakeWindowCaptureSource(new[] { window });
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        await using IWindowCapture capture = source.Start(
            window.handle, new CaptureOptions(60, false), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (CapturedFrame _ in capture.Frames.WithCancellation(cancellation.Token)) { }
        });
    }

    [Fact]
    public async Task Start_WindowGoneMidStream_ThrowsWindowGone()
    {
        WindowInformation window = new WindowInformation(new WindowHandle(1), "W", "p", 4, 2);
        FakeWindowCaptureSource source = new FakeWindowCaptureSource(new[] { window });
        source.EnqueueFrame(window.handle, BuildSolidFrame(4, 2, 0x33));
        source.FaultAfterEnqueued(window.handle, new WindowGoneException(window.handle));

        await using IWindowCapture capture = source.Start(
            window.handle, new CaptureOptions(60, false), CancellationToken.None);

        await Assert.ThrowsAsync<WindowGoneException>(async () =>
        {
            await foreach (CapturedFrame _ in capture.Frames) { }
        });
    }

    private static CapturedFrame BuildSolidFrame(int width, int height, byte value)
    {
        byte[] buffer = new byte[width * 4 * height];
        System.Array.Fill(buffer, value);
        return new CapturedFrame(width, height, width * 4, PixelFormat.Bgra32, 0, buffer);
    }
}
```

- [ ] **Step 2: Run tests to confirm failure**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~FakeWindowCaptureSourceTests"`
Expected: FAIL — types missing.

- [ ] **Step 3: Implement the fake capture**

`FakeWindowCapture.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;

namespace WindowStream.Core.Capture.Testing;

public sealed class FakeWindowCapture : IWindowCapture
{
    internal readonly Channel<object> channel =
        Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public WindowHandle handle { get; }
    public CaptureOptions options { get; }
    public IAsyncEnumerable<CapturedFrame> Frames { get; }

    private readonly CancellationToken cancellationToken;

    public FakeWindowCapture(WindowHandle handle, CaptureOptions options, CancellationToken cancellationToken)
    {
        this.handle = handle;
        this.options = options;
        this.cancellationToken = cancellationToken;
        this.Frames = ReadFramesAsync();
    }

    private async IAsyncEnumerable<CapturedFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken enumeratorCancellation = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, enumeratorCancellation);
        while (await channel.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
        {
            while (channel.Reader.TryRead(out object? next))
            {
                if (next is CapturedFrame frame)
                {
                    yield return frame;
                }
                else if (next is Exception exception)
                {
                    throw exception;
                }
                else
                {
                    yield break;
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
```

`FakeWindowCaptureSource.cs`:
```csharp
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace WindowStream.Core.Capture.Testing;

public sealed class FakeWindowCaptureSource : IWindowCaptureSource
{
    private readonly List<WindowInformation> windows;
    private readonly Dictionary<WindowHandle, FakeWindowCapture> captures = new();

    public FakeWindowCaptureSource(IEnumerable<WindowInformation> windows)
    {
        this.windows = windows?.ToList() ?? new List<WindowInformation>();
    }

    public IEnumerable<WindowInformation> ListWindows() => windows;

    public IWindowCapture Start(WindowHandle handle, CaptureOptions options, CancellationToken cancellationToken)
    {
        if (!windows.Exists(window => window.handle.Equals(handle)))
        {
            throw new WindowGoneException(handle);
        }
        FakeWindowCapture capture = new FakeWindowCapture(handle, options, cancellationToken);
        captures[handle] = capture;
        return capture;
    }

    public void EnqueueFrame(WindowHandle handle, CapturedFrame frame) =>
        captures[handle].channel.Writer.TryWrite(frame);

    public void CompleteAfterEnqueued(WindowHandle handle) =>
        captures[handle].channel.Writer.TryComplete();

    public void FaultAfterEnqueued(WindowHandle handle, System.Exception exception) =>
        captures[handle].channel.Writer.TryComplete(exception);
}
```

- [ ] **Step 4: Run tests to confirm pass**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~FakeWindowCaptureSourceTests"`
Expected: PASS (5/5).

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Capture/Testing tests/WindowStream.Core.Tests/Capture/Testing/FakeWindowCaptureSourceTests.cs
git commit -m "test(core): add FakeWindowCaptureSource for deterministic capture tests"
```

### Task 18: WgcCaptureSource (Windows-only) implementation

**Files:**
- Create: `src/WindowStream.Core/Capture/Windows/WgcCaptureSource.cs`
- Create: `src/WindowStream.Core/Capture/Windows/WgcCapture.cs`
- Create: `src/WindowStream.Core/Capture/Windows/WgcFrameConverter.cs`
- Modify: `src/WindowStream.Core/WindowStream.Core.csproj` (add `<PackageReference Include="Silk.NET.Direct3D11" Version="2.22.0" />` inside `<ItemGroup Condition="'$(TargetFramework)' == 'net8.0-windows10.0.19041.0'">`)
- Test: `tests/WindowStream.Integration.Tests/Capture/WgcCaptureSourceSmokeTests.cs`

This task implements the real Windows capture path; unit coverage for the interface contract is already supplied by the fake (Task 20). The integration smoke test is the coverage for the WGC adapter because `Windows.Graphics.Capture` cannot be exercised headless.

- [ ] **Step 1: Write the failing integration smoke test**

```csharp
#if WINDOWS
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using Xunit;

namespace WindowStream.Integration.Tests.Capture;

[Trait("Category", "Windows")]
public sealed class WgcCaptureSourceSmokeTests
{
    [Fact(Timeout = 15000)]
    public async Task Attaches_To_Notepad_And_Receives_Frame()
    {
        Process notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
            ?? throw new InvalidOperationException("Could not start notepad.exe");
        try
        {
            notepad.WaitForInputIdle(5000);

            WgcCaptureSource source = new WgcCaptureSource();
            WindowInformation? notepadWindow = null;
            for (int attempt = 0; attempt < 20 && notepadWindow is null; attempt++)
            {
                notepadWindow = source.ListWindows().FirstOrDefault(window =>
                    window.processName.Equals("notepad", StringComparison.OrdinalIgnoreCase)
                    && window.widthPixels > 0);
                if (notepadWindow is null)
                {
                    await Task.Delay(200);
                }
            }
            Assert.NotNull(notepadWindow);

            await using IWindowCapture capture = source.Start(
                notepadWindow!.handle,
                new CaptureOptions(30, false),
                CancellationToken.None);

            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (CapturedFrame frame in capture.Frames.WithCancellation(timeout.Token))
            {
                Assert.True(frame.widthPixels > 0);
                Assert.True(frame.heightPixels > 0);
                return;
            }
            Assert.Fail("No frame received before timeout.");
        }
        finally
        {
            try { notepad.CloseMainWindow(); notepad.WaitForExit(2000); if (!notepad.HasExited) { notepad.Kill(); } }
            catch { /* best effort cleanup */ }
        }
    }
}
#endif
```

- [ ] **Step 2: Run to confirm failure**

Run: `dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~WgcCaptureSourceSmokeTests"`
Expected: FAIL — `WgcCaptureSource` undefined.

- [ ] **Step 3: Implement WgcCapture (the per-session capture)**

```csharp
#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace WindowStream.Core.Capture.Windows;

public sealed class WgcCapture : IWindowCapture
{
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private readonly GraphicsCaptureItem item;
    private readonly Direct3D11CaptureFramePool framePool;
    private readonly GraphicsCaptureSession session;
    private readonly Channel<object> frameChannel =
        Channel.CreateBounded<object>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    private readonly CancellationToken cancellationToken;
    private readonly long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    private bool disposed;

    public WindowHandle handle { get; }
    public CaptureOptions options { get; }
    public IAsyncEnumerable<CapturedFrame> Frames { get; }

    public WgcCapture(
        WindowHandle handle,
        CaptureOptions options,
        GraphicsCaptureItem item,
        IDirect3DDevice device,
        CancellationToken cancellationToken)
    {
        this.handle = handle;
        this.options = options;
        this.item = item;
        this.cancellationToken = cancellationToken;

        item.Closed += OnItemClosed;
        framePool = Direct3D11CaptureFramePool.Create(
            device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            size: item.Size);
        framePool.FrameArrived += OnFrameArrived;
        session = framePool.CreateCaptureSession(item);
        session.StartCapture();

        Frames = ReadAsync();
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        frameChannel.Writer.TryComplete(new WindowGoneException(handle));
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object args)
    {
        try
        {
            using Direct3D11CaptureFrame frame = pool.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }
            CapturedFrame converted = WgcFrameConverter.Convert(frame, startTicks);
            frameChannel.Writer.TryWrite(converted);
        }
        catch (Exception exception)
        {
            frameChannel.Writer.TryComplete(new WindowCaptureException("WGC frame conversion failed.", exception));
        }
    }

    private async IAsyncEnumerable<CapturedFrame> ReadAsync(
        [EnumeratorCancellation] CancellationToken enumeratorCancellation = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, enumeratorCancellation);
        while (await frameChannel.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
        {
            while (frameChannel.Reader.TryRead(out object? next))
            {
                if (next is CapturedFrame frame)
                {
                    yield return frame;
                }
                else if (next is Exception exception)
                {
                    throw exception;
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }
        disposed = true;
        try { session.Dispose(); } catch { }
        try { framePool.Dispose(); } catch { }
        frameChannel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
#endif
```

- [ ] **Step 4: Implement WgcFrameConverter (D3D11 surface → managed NV12-or-BGRA buffer)**

```csharp
#if WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace WindowStream.Core.Capture.Windows;

internal static class WgcFrameConverter
{
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    private static readonly Guid iidId3D11Texture2D =
        new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    public static CapturedFrame Convert(Direct3D11CaptureFrame frame, long startTicks)
    {
        IDirect3DDxgiInterfaceAccess access =
            frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid id = iidId3D11Texture2D;
        IntPtr texturePointer = access.GetInterface(ref id);
        try
        {
            unsafe
            {
                ID3D11Texture2D* texture = (ID3D11Texture2D*)texturePointer;
                Texture2DDesc description = default;
                texture->GetDesc(ref description);

                ID3D11Device* device = null;
                texture->GetDevice(&device);
                ID3D11DeviceContext* context = null;
                device->GetImmediateContext(&context);

                Texture2DDesc stagingDescription = description;
                stagingDescription.Usage = Usage.Staging;
                stagingDescription.BindFlags = 0;
                stagingDescription.CPUAccessFlags = (uint)CpuAccessFlag.Read;
                stagingDescription.MiscFlags = 0;

                ID3D11Texture2D* staging = null;
                device->CreateTexture2D(ref stagingDescription, (SubresourceData*)null, ref staging);
                context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)texture);

                MappedSubresource mapped = default;
                context->Map((ID3D11Resource*)staging, 0, Map.Read, 0, ref mapped);

                int width = (int)description.Width;
                int height = (int)description.Height;
                int stride = (int)mapped.RowPitch;
                byte[] managed = new byte[stride * height];
                Marshal.Copy((IntPtr)mapped.PData, managed, 0, managed.Length);
                context->Unmap((ID3D11Resource*)staging, 0);
                staging->Release();
                context->Release();
                device->Release();

                long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
                long timestampMicroseconds = (long)(elapsedTicks * 1_000_000.0 / Stopwatch.Frequency);

                return new CapturedFrame(
                    widthPixels: width,
                    heightPixels: height,
                    rowStrideBytes: stride,
                    pixelFormat: PixelFormat.Bgra32,
                    presentationTimestampMicroseconds: timestampMicroseconds,
                    pixelBuffer: managed);
            }
        }
        finally
        {
            Marshal.Release(texturePointer);
        }
    }
}
#endif
```

- [ ] **Step 5: Implement WgcCaptureSource**

```csharp
#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace WindowStream.Core.Capture.Windows;

public sealed class WgcCaptureSource : IWindowCaptureSource
{
    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, System.Text.StringBuilder builder, int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processIdentifier);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr windowHandle, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    private readonly IWindowEnumerator enumerator;

    public WgcCaptureSource() : this(new WindowEnumerator(new Win32Api())) { }
    public WgcCaptureSource(IWindowEnumerator enumerator)
    {
        this.enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
    }

    public IEnumerable<WindowInformation> ListWindows() => enumerator.EnumerateWindows();

    public IWindowCapture Start(WindowHandle handle, CaptureOptions options, CancellationToken cancellationToken)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new WindowCaptureException("Windows.Graphics.Capture is not supported on this OS build.");
        }

        global::Windows.Graphics.Capture.Interop.IGraphicsCaptureItemInterop interop =
            GraphicsCaptureItem.As<global::Windows.Graphics.Capture.Interop.IGraphicsCaptureItemInterop>();

        GraphicsCaptureItem item;
        try
        {
            item = interop.CreateForWindow(
                new IntPtr(handle.value),
                typeof(GraphicsCaptureItem).GUID);
        }
        catch (Exception exception)
        {
            throw new WindowGoneException(handle, exception);
        }

        IDirect3DDevice device = Direct3D11Helper.CreateDevice();
        return new WgcCapture(handle, options, item, device, cancellationToken);
    }
}
#endif
```

(Add a companion `Direct3D11Helper.cs` in the same folder that wraps `CreateDirect3D11DeviceFromDXGIDevice` and returns an `IDirect3DDevice`; its line count is small enough that it's part of the same commit.)

- [ ] **Step 6: Rerun integration test**

Run: `dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~WgcCaptureSourceSmokeTests" -f net8.0-windows10.0.19041.0`
Expected: PASS (on a Windows host with a desktop session).

- [ ] **Step 7: Commit**

```bash
git add src/WindowStream.Core/Capture/Windows src/WindowStream.Core/WindowStream.Core.csproj tests/WindowStream.Integration.Tests/Capture/WgcCaptureSourceSmokeTests.cs
git commit -m "feat(core): add WGC capture source for Windows target framework"
```

---

## Phase 8: WindowEnumerator

### Task 19: IWin32Api abstraction + enumerator logic with fake

**Files:**
- Create: `src/WindowStream.Core/Capture/Windows/IWin32Api.cs`
- Create: `src/WindowStream.Core/Capture/Windows/Win32Api.cs`
- Create: `src/WindowStream.Core/Capture/Windows/IWindowEnumerator.cs`
- Create: `src/WindowStream.Core/Capture/Windows/WindowEnumerator.cs`
- Create: `src/WindowStream.Core/Capture/Windows/WindowEnumerationFilters.cs`
- Test: `tests/WindowStream.Core.Tests/Capture/Windows/WindowEnumeratorTests.cs`

The abstraction is portable (plain interface) so it compiles on `net8.0` and `net8.0-maccatalyst`, but only `Win32Api` is declared for `net8.0-windows10.0.19041.0`. Tests inject a fake and therefore run everywhere.

- [ ] **Step 1: Write failing tests using a fake `IWin32Api`**

```csharp
using System.Collections.Generic;
using System.Linq;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using Xunit;

namespace WindowStream.Core.Tests.Capture.Windows;

public sealed class WindowEnumeratorTests
{
    private sealed class FakeWin32Api : IWin32Api
    {
        public List<FakeWindow> windows { get; } = new();

        public IEnumerable<System.IntPtr> EnumerateTopLevelWindowHandles()
        {
            foreach (FakeWindow window in windows)
            {
                yield return window.handle;
            }
        }

        public bool IsWindowVisible(System.IntPtr handle) =>
            Find(handle)?.visible ?? false;

        public string GetWindowTitle(System.IntPtr handle) =>
            Find(handle)?.title ?? "";

        public string GetWindowClassName(System.IntPtr handle) =>
            Find(handle)?.className ?? "";

        public (int processIdentifier, string processName) GetWindowProcess(System.IntPtr handle)
        {
            FakeWindow? w = Find(handle);
            return (w?.processIdentifier ?? 0, w?.processName ?? "");
        }

        public (int widthPixels, int heightPixels) GetWindowSize(System.IntPtr handle)
        {
            FakeWindow? w = Find(handle);
            return (w?.widthPixels ?? 0, w?.heightPixels ?? 0);
        }

        private FakeWindow? Find(System.IntPtr handle) => windows.Find(w => w.handle == handle);
    }

    private sealed record FakeWindow(
        System.IntPtr handle, bool visible, string title, string className,
        int processIdentifier, string processName, int widthPixels, int heightPixels);

    [Fact]
    public void Enumerate_YieldsOnlyVisibleTitledNonSystemWindows()
    {
        FakeWin32Api api = new FakeWin32Api();
        api.windows.AddRange(new[]
        {
            new FakeWindow(new(1), true,  "Notepad",  "Notepad", 100, "notepad", 640, 480),
            new FakeWindow(new(2), false, "Hidden",   "AnyClass",101, "app",     100, 100),
            new FakeWindow(new(3), true,  "",         "AnyClass",102, "app",     100, 100),
            new FakeWindow(new(4), true,  "Taskbar",  "Shell_TrayWnd", 103, "explorer", 1920, 40),
            new FakeWindow(new(5), true,  "Desktop",  "Progman",       104, "explorer", 1920, 1080),
            new FakeWindow(new(6), true,  "Visible2","ProperClass",   105, "other",    800, 600),
        });

        WindowEnumerator enumerator = new WindowEnumerator(api);
        List<WindowInformation> list = enumerator.EnumerateWindows().ToList();

        Assert.Equal(2, list.Count);
        Assert.Contains(list, window => window.title == "Notepad");
        Assert.Contains(list, window => window.title == "Visible2");
    }

    [Fact]
    public void Enumerate_ExcludesZeroSizedWindows()
    {
        FakeWin32Api api = new FakeWin32Api();
        api.windows.Add(new FakeWindow(new(1), true, "Title", "Class", 10, "p", 0, 0));
        WindowEnumerator enumerator = new WindowEnumerator(api);
        Assert.Empty(enumerator.EnumerateWindows());
    }

    [Fact]
    public void Enumerate_ReturnsHandleAndDimensions()
    {
        FakeWin32Api api = new FakeWin32Api();
        api.windows.Add(new FakeWindow(new(42), true, "T", "C", 10, "proc", 1024, 768));
        WindowEnumerator enumerator = new WindowEnumerator(api);

        WindowInformation information = enumerator.EnumerateWindows().Single();
        Assert.Equal(42, information.handle.value);
        Assert.Equal(1024, information.widthPixels);
        Assert.Equal(768, information.heightPixels);
        Assert.Equal("proc", information.processName);
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("WorkerW")]
    [InlineData("Windows.UI.Core.CoreWindow")]
    public void ExcludedClasses_AreFiltered(string excludedClass)
    {
        FakeWin32Api api = new FakeWin32Api();
        api.windows.Add(new FakeWindow(new(1), true, "T", excludedClass, 10, "p", 100, 100));
        WindowEnumerator enumerator = new WindowEnumerator(api);
        Assert.Empty(enumerator.EnumerateWindows());
    }

    [Fact]
    public void Constructor_NullApi_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(() => new WindowEnumerator(null!));
    }
}
```

- [ ] **Step 2: Run to confirm failure**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~WindowEnumeratorTests"`
Expected: FAIL — enumerator types undefined.

- [ ] **Step 3: Implement the interface + abstraction**

`IWin32Api.cs`:
```csharp
using System;
using System.Collections.Generic;

namespace WindowStream.Core.Capture.Windows;

public interface IWin32Api
{
    IEnumerable<IntPtr> EnumerateTopLevelWindowHandles();
    bool IsWindowVisible(IntPtr handle);
    string GetWindowTitle(IntPtr handle);
    string GetWindowClassName(IntPtr handle);
    (int processIdentifier, string processName) GetWindowProcess(IntPtr handle);
    (int widthPixels, int heightPixels) GetWindowSize(IntPtr handle);
}
```

`IWindowEnumerator.cs`:
```csharp
using System.Collections.Generic;

namespace WindowStream.Core.Capture.Windows;

public interface IWindowEnumerator
{
    IEnumerable<WindowInformation> EnumerateWindows();
}
```

`WindowEnumerationFilters.cs`:
```csharp
using System.Collections.Generic;

namespace WindowStream.Core.Capture.Windows;

internal static class WindowEnumerationFilters
{
    public static readonly IReadOnlySet<string> ExcludedClassNames =
        new HashSet<string>(System.StringComparer.Ordinal)
        {
            "Progman",
            "Shell_TrayWnd",
            "WorkerW",
            "Windows.UI.Core.CoreWindow",
            "ApplicationFrameWindow",
        };

    public static bool PassesFilters(
        bool isVisible, string title, string className, int widthPixels, int heightPixels)
    {
        if (!isVisible) return false;
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (ExcludedClassNames.Contains(className)) return false;
        if (widthPixels <= 0 || heightPixels <= 0) return false;
        return true;
    }
}
```

`WindowEnumerator.cs`:
```csharp
using System;
using System.Collections.Generic;

namespace WindowStream.Core.Capture.Windows;

public sealed class WindowEnumerator : IWindowEnumerator
{
    private readonly IWin32Api win32Api;

    public WindowEnumerator(IWin32Api win32Api)
    {
        this.win32Api = win32Api ?? throw new ArgumentNullException(nameof(win32Api));
    }

    public IEnumerable<WindowInformation> EnumerateWindows()
    {
        foreach (IntPtr handle in win32Api.EnumerateTopLevelWindowHandles())
        {
            bool visible = win32Api.IsWindowVisible(handle);
            string title = win32Api.GetWindowTitle(handle);
            string className = win32Api.GetWindowClassName(handle);
            (int widthPixels, int heightPixels) size = win32Api.GetWindowSize(handle);

            if (!WindowEnumerationFilters.PassesFilters(
                visible, title, className, size.widthPixels, size.heightPixels))
            {
                continue;
            }
            (int processIdentifier, string processName) process = win32Api.GetWindowProcess(handle);
            yield return new WindowInformation(
                new WindowHandle(handle.ToInt64()),
                title,
                process.processName,
                size.widthPixels,
                size.heightPixels);
        }
    }
}
```

- [ ] **Step 4: Run tests to confirm pass**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~WindowEnumeratorTests"`
Expected: PASS (8/8).

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Capture/Windows/IWin32Api.cs src/WindowStream.Core/Capture/Windows/IWindowEnumerator.cs src/WindowStream.Core/Capture/Windows/WindowEnumerationFilters.cs src/WindowStream.Core/Capture/Windows/WindowEnumerator.cs tests/WindowStream.Core.Tests/Capture/Windows/WindowEnumeratorTests.cs
git commit -m "feat(core): add WindowEnumerator behind injectable IWin32Api"
```

### Task 20: Win32Api real implementation + integration test

**Files:**
- Create: `src/WindowStream.Core/Capture/Windows/Win32Api.cs`
- Test: `tests/WindowStream.Integration.Tests/Capture/Windows/Win32ApiIntegrationTests.cs`

- [ ] **Step 1: Write failing integration test**

```csharp
#if WINDOWS
using System.Linq;
using WindowStream.Core.Capture.Windows;
using Xunit;

namespace WindowStream.Integration.Tests.Capture.Windows;

[Trait("Category", "Windows")]
public sealed class Win32ApiIntegrationTests
{
    [Fact]
    public void EnumerateTopLevelWindowHandles_ReturnsAtLeastOneWindow()
    {
        Win32Api api = new Win32Api();
        Assert.NotEmpty(api.EnumerateTopLevelWindowHandles().Take(1));
    }

    [Fact]
    public void WindowEnumerator_WithRealApi_ReturnsNonZeroVisibleWindows()
    {
        WindowEnumerator enumerator = new WindowEnumerator(new Win32Api());
        Assert.NotEmpty(enumerator.EnumerateWindows().Take(1));
    }
}
#endif
```

- [ ] **Step 2: Run to confirm failure**

Run: `dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~Win32ApiIntegrationTests" -f net8.0-windows10.0.19041.0`
Expected: FAIL — `Win32Api` undefined.

- [ ] **Step 3: Implement Win32Api**

```csharp
#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowStream.Core.Capture.Windows;

public sealed class Win32Api : IWin32Api
{
    private delegate bool EnumWindowsProcedure(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProcedure procedure, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisibleNative(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    private static extern bool IsWindowVisibleExtern(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder builder, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr windowHandle, StringBuilder builder, int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processIdentifier);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int left, top, right, bottom; }

    public IEnumerable<IntPtr> EnumerateTopLevelWindowHandles()
    {
        List<IntPtr> handles = new List<IntPtr>();
        EnumWindows((windowHandle, _) => { handles.Add(windowHandle); return true; }, IntPtr.Zero);
        return handles;
    }

    public bool IsWindowVisible(IntPtr handle) => IsWindowVisibleExtern(handle);

    public string GetWindowTitle(IntPtr handle)
    {
        int length = GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;
        StringBuilder buffer = new StringBuilder(length + 1);
        GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    public string GetWindowClassName(IntPtr handle)
    {
        StringBuilder buffer = new StringBuilder(256);
        GetClassName(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    public (int processIdentifier, string processName) GetWindowProcess(IntPtr handle)
    {
        GetWindowThreadProcessId(handle, out uint processIdentifier);
        try
        {
            using Process process = Process.GetProcessById((int)processIdentifier);
            return ((int)processIdentifier, process.ProcessName);
        }
        catch
        {
            return ((int)processIdentifier, string.Empty);
        }
    }

    public (int widthPixels, int heightPixels) GetWindowSize(IntPtr handle)
    {
        if (!GetWindowRect(handle, out NativeRect rect))
        {
            return (0, 0);
        }
        return (rect.right - rect.left, rect.bottom - rect.top);
    }
}
#endif
```

- [ ] **Step 4: Run the integration test**

Run: `dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~Win32ApiIntegrationTests" -f net8.0-windows10.0.19041.0`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Capture/Windows/Win32Api.cs tests/WindowStream.Integration.Tests/Capture/Windows/Win32ApiIntegrationTests.cs
git commit -m "feat(core): add Win32 P/Invoke implementation of IWin32Api"
```

---

## Phase 9: Video encoder + FFmpeg NVENC

### Task 21: Encoder data types and interface

**Files:**
- Create: `src/WindowStream.Core/Encode/EncoderOptions.cs`
- Create: `src/WindowStream.Core/Encode/EncodedChunk.cs`
- Create: `src/WindowStream.Core/Encode/EncoderException.cs`
- Create: `src/WindowStream.Core/Encode/IVideoEncoder.cs`
- Test: `tests/WindowStream.Core.Tests/Encode/EncoderOptionsTests.cs`
- Test: `tests/WindowStream.Core.Tests/Encode/EncodedChunkTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using System;
using Xunit;
using WindowStream.Core.Encode;

namespace WindowStream.Core.Tests.Encode;

public sealed class EncoderOptionsTests
{
    [Fact]
    public void Constructor_AcceptsValidValues()
    {
        EncoderOptions options = new EncoderOptions(
            widthPixels: 1920,
            heightPixels: 1080,
            framesPerSecond: 60,
            bitrateBitsPerSecond: 8_000_000,
            groupOfPicturesLength: 60,
            safetyKeyframeIntervalSeconds: 2);

        Assert.Equal(1920, options.widthPixels);
        Assert.Equal(60, options.framesPerSecond);
    }

    [Theory]
    [InlineData(0, 1080, 60, 1, 60, 2)]
    [InlineData(1920, 0, 60, 1, 60, 2)]
    [InlineData(1920, 1080, 0, 1, 60, 2)]
    [InlineData(1920, 1080, 60, 0, 60, 2)]
    [InlineData(1920, 1080, 60, 1, 0, 2)]
    [InlineData(1920, 1080, 60, 1, 60, 0)]
    [InlineData(-1, 1080, 60, 1, 60, 2)]
    public void Constructor_RejectsNonPositive(
        int width, int height, int fps, int bitrate, int gop, int safety)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EncoderOptions(width, height, fps, bitrate, gop, safety));
    }
}

public sealed class EncodedChunkTests
{
    [Fact]
    public void Constructor_PopulatesProperties()
    {
        byte[] payload = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67 };
        EncodedChunk chunk = new EncodedChunk(
            payload,
            isKeyframe: true,
            presentationTimestampMicroseconds: 1234);
        Assert.True(chunk.isKeyframe);
        Assert.Equal(5, chunk.payload.Length);
    }

    [Fact]
    public void Constructor_EmptyPayload_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new EncodedChunk(System.Array.Empty<byte>(), false, 0));
    }

    [Fact]
    public void Constructor_NegativeTimestamp_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EncodedChunk(new byte[] { 1 }, false, -1));
    }
}
```

- [ ] **Step 2: Run to confirm failure**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~Encode"`
Expected: FAIL.

- [ ] **Step 3: Add the types**

`EncoderOptions.cs`:
```csharp
using System;

namespace WindowStream.Core.Encode;

public sealed class EncoderOptions
{
    public int widthPixels { get; }
    public int heightPixels { get; }
    public int framesPerSecond { get; }
    public int bitrateBitsPerSecond { get; }
    public int groupOfPicturesLength { get; }
    public int safetyKeyframeIntervalSeconds { get; }

    public EncoderOptions(
        int widthPixels,
        int heightPixels,
        int framesPerSecond,
        int bitrateBitsPerSecond,
        int groupOfPicturesLength,
        int safetyKeyframeIntervalSeconds)
    {
        if (widthPixels <= 0) throw new ArgumentOutOfRangeException(nameof(widthPixels));
        if (heightPixels <= 0) throw new ArgumentOutOfRangeException(nameof(heightPixels));
        if (framesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        if (bitrateBitsPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(bitrateBitsPerSecond));
        if (groupOfPicturesLength <= 0) throw new ArgumentOutOfRangeException(nameof(groupOfPicturesLength));
        if (safetyKeyframeIntervalSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(safetyKeyframeIntervalSeconds));

        this.widthPixels = widthPixels;
        this.heightPixels = heightPixels;
        this.framesPerSecond = framesPerSecond;
        this.bitrateBitsPerSecond = bitrateBitsPerSecond;
        this.groupOfPicturesLength = groupOfPicturesLength;
        this.safetyKeyframeIntervalSeconds = safetyKeyframeIntervalSeconds;
    }
}
```

`EncodedChunk.cs`:
```csharp
using System;

namespace WindowStream.Core.Encode;

public sealed class EncodedChunk
{
    public ReadOnlyMemory<byte> payload { get; }
    public bool isKeyframe { get; }
    public long presentationTimestampMicroseconds { get; }

    public EncodedChunk(
        ReadOnlyMemory<byte> payload,
        bool isKeyframe,
        long presentationTimestampMicroseconds)
    {
        if (payload.Length == 0)
        {
            throw new ArgumentException("payload must not be empty.", nameof(payload));
        }
        if (presentationTimestampMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(presentationTimestampMicroseconds));
        }
        this.payload = payload;
        this.isKeyframe = isKeyframe;
        this.presentationTimestampMicroseconds = presentationTimestampMicroseconds;
    }
}
```

`EncoderException.cs`:
```csharp
using System;

namespace WindowStream.Core.Encode;

public class EncoderException : Exception
{
    public int? ffmpegErrorCode { get; }

    public EncoderException(string message) : base(message) { }
    public EncoderException(string message, int ffmpegErrorCode) : base(message)
    {
        this.ffmpegErrorCode = ffmpegErrorCode;
    }
    public EncoderException(string message, Exception innerException) : base(message, innerException) { }
}
```

`IVideoEncoder.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;

namespace WindowStream.Core.Encode;

public interface IVideoEncoder : IAsyncDisposable
{
    void Configure(EncoderOptions options);
    Task EncodeAsync(CapturedFrame frame, CancellationToken cancellationToken);
    void RequestKeyframe();
    IAsyncEnumerable<EncodedChunk> EncodedChunks { get; }
}
```

- [ ] **Step 4: Run tests to confirm pass**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~Encode"`
Expected: PASS (10/10).

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Encode tests/WindowStream.Core.Tests/Encode
git commit -m "feat(core): add video encoder interface and value types"
```

### Task 22: FakeVideoEncoder for unit tests

**Files:**
- Create: `src/WindowStream.Core/Encode/Testing/FakeVideoEncoder.cs`
- Test: `tests/WindowStream.Core.Tests/Encode/Testing/FakeVideoEncoderTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Encode;
using WindowStream.Core.Encode.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Encode.Testing;

public sealed class FakeVideoEncoderTests
{
    private static CapturedFrame SampleFrame() =>
        new CapturedFrame(2, 2, 8, PixelFormat.Bgra32, 100, new byte[16]);

    [Fact]
    public async Task EncodeAsync_BeforeConfigure_Throws()
    {
        await using FakeVideoEncoder encoder = new FakeVideoEncoder();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            encoder.EncodeAsync(SampleFrame(), CancellationToken.None));
    }

    [Fact]
    public async Task EncodeAsync_EmitsOneChunkPerFrame()
    {
        await using FakeVideoEncoder encoder = new FakeVideoEncoder();
        encoder.Configure(new EncoderOptions(2, 2, 30, 1_000_000, 30, 2));

        await encoder.EncodeAsync(SampleFrame(), CancellationToken.None);
        await encoder.EncodeAsync(SampleFrame(), CancellationToken.None);
        encoder.CompleteEncoding();

        List<EncodedChunk> chunks = new List<EncodedChunk>();
        await foreach (EncodedChunk chunk in encoder.EncodedChunks)
        {
            chunks.Add(chunk);
        }
        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public async Task RequestKeyframe_MarksNextChunkAsKeyframe()
    {
        await using FakeVideoEncoder encoder = new FakeVideoEncoder();
        encoder.Configure(new EncoderOptions(2, 2, 30, 1_000_000, 30, 2));

        encoder.RequestKeyframe();
        await encoder.EncodeAsync(SampleFrame(), CancellationToken.None);
        encoder.CompleteEncoding();

        List<EncodedChunk> chunks = new List<EncodedChunk>();
        await foreach (EncodedChunk chunk in encoder.EncodedChunks)
        {
            chunks.Add(chunk);
        }
        Assert.Single(chunks);
        Assert.True(chunks[0].isKeyframe);
    }

    [Fact]
    public void Configure_Twice_Throws()
    {
        FakeVideoEncoder encoder = new FakeVideoEncoder();
        encoder.Configure(new EncoderOptions(2, 2, 30, 1_000_000, 30, 2));
        Assert.Throws<InvalidOperationException>(() =>
            encoder.Configure(new EncoderOptions(2, 2, 30, 1_000_000, 30, 2)));
    }

    [Fact]
    public async Task EncodeAsync_HonorsCancellation()
    {
        await using FakeVideoEncoder encoder = new FakeVideoEncoder();
        encoder.Configure(new EncoderOptions(2, 2, 30, 1_000_000, 30, 2));
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            encoder.EncodeAsync(SampleFrame(), cancellation.Token));
    }

    [Fact]
    public async Task RequestKeyframe_BeforeConfigure_Throws()
    {
        await using FakeVideoEncoder encoder = new FakeVideoEncoder();
        Assert.Throws<InvalidOperationException>(() => encoder.RequestKeyframe());
        await Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Run to confirm failure**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~FakeVideoEncoderTests"`
Expected: FAIL.

- [ ] **Step 3: Implement the fake encoder**

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WindowStream.Core.Capture;

namespace WindowStream.Core.Encode.Testing;

public sealed class FakeVideoEncoder : IVideoEncoder
{
    private readonly Channel<EncodedChunk> channel =
        Channel.CreateUnbounded<EncodedChunk>(new UnboundedChannelOptions { SingleReader = true });
    private EncoderOptions? options;
    private bool nextKeyframe;
    private int nextIndex;
    private bool disposed;

    public IAsyncEnumerable<EncodedChunk> EncodedChunks { get; }

    public FakeVideoEncoder()
    {
        EncodedChunks = ReadAsync();
    }

    public void Configure(EncoderOptions options)
    {
        if (this.options is not null)
        {
            throw new InvalidOperationException("FakeVideoEncoder is already configured.");
        }
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task EncodeAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        if (options is null)
        {
            throw new InvalidOperationException("Configure must be called before EncodeAsync.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        bool keyframe = nextKeyframe || nextIndex == 0;
        nextKeyframe = false;
        byte[] bytes = new byte[] { (byte)nextIndex };
        nextIndex++;
        channel.Writer.TryWrite(new EncodedChunk(bytes, keyframe, frame.presentationTimestampMicroseconds));
        return Task.CompletedTask;
    }

    public void RequestKeyframe()
    {
        if (options is null)
        {
            throw new InvalidOperationException("Configure must be called before RequestKeyframe.");
        }
        nextKeyframe = true;
    }

    public void CompleteEncoding() => channel.Writer.TryComplete();

    private async IAsyncEnumerable<EncodedChunk> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (channel.Reader.TryRead(out EncodedChunk? chunk))
            {
                yield return chunk;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 4: Run tests to confirm pass**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~FakeVideoEncoderTests"`
Expected: PASS (6/6).

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Encode/Testing tests/WindowStream.Core.Tests/Encode/Testing/FakeVideoEncoderTests.cs
git commit -m "test(core): add FakeVideoEncoder for encoder interface coverage"
```

### Task 23: FFmpegNvencEncoder — construction + Configure path

**Files:**
- Modify: `src/WindowStream.Core/WindowStream.Core.csproj`
  - Add: `<PackageReference Include="FFmpeg.AutoGen" Version="6.1.0.2" />`
  - Add: `<PackageReference Include="FFmpeg.AutoGen.ReadOnly.Runtime.Windows" Version="6.1.0.2" />`
- Create: `src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs`
- Create: `src/WindowStream.Core/Encode/FFmpegNativeLoader.cs`
- Test: `tests/WindowStream.Core.Tests/Encode/FFmpegNvencEncoderConstructionTests.cs`

The portable tests only prove the non-native parts: null checks, double-configure rejection, disposing before configure. Real NVENC exercise happens in the part-C integration phase (Phase 12), as the brief directs.

- [ ] **Step 1: Write failing construction-tier tests**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Encode;
using Xunit;

namespace WindowStream.Core.Tests.Encode;

public sealed class FFmpegNvencEncoderConstructionTests
{
    [Fact]
    public async Task DisposeAsync_BeforeConfigure_IsNoThrow()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        await encoder.DisposeAsync();
    }

    [Fact]
    public void Configure_Null_Throws()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        Assert.Throws<ArgumentNullException>(() => encoder.Configure(null!));
    }

    [Fact]
    public async Task EncodeAsync_BeforeConfigure_Throws()
    {
        await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        CapturedFrame frame = new CapturedFrame(2, 2, 8, PixelFormat.Bgra32, 0, new byte[16]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            encoder.EncodeAsync(frame, CancellationToken.None));
    }

    [Fact]
    public void RequestKeyframe_BeforeConfigure_Throws()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        Assert.Throws<InvalidOperationException>(() => encoder.RequestKeyframe());
    }

    [Fact]
    public void Configure_WhenCodecMissing_ThrowsEncoderException()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new FailingLoader());
        Assert.Throws<EncoderException>(() =>
            encoder.Configure(new EncoderOptions(640, 480, 30, 1_000_000, 60, 2)));
    }

    private sealed class DummyLoader : IFFmpegNativeLoader
    {
        public void EnsureLoaded() { /* no-op — no native work in these tests */ }
    }
    private sealed class FailingLoader : IFFmpegNativeLoader
    {
        public void EnsureLoaded() => throw new EncoderException("FFmpeg natives missing.");
    }
}
```

- [ ] **Step 2: Run to confirm failure**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~FFmpegNvencEncoderConstructionTests"`
Expected: FAIL — `FFmpegNvencEncoder` and `IFFmpegNativeLoader` undefined.

- [ ] **Step 3: Add the loader interface + default implementation**

`FFmpegNativeLoader.cs`:
```csharp
using System;
using System.IO;
using FFmpeg.AutoGen;

namespace WindowStream.Core.Encode;

public interface IFFmpegNativeLoader
{
    void EnsureLoaded();
}

public sealed class FFmpegNativeLoader : IFFmpegNativeLoader
{
    private static readonly object synchronizationLock = new object();
    private static bool initialized;

    public void EnsureLoaded()
    {
        lock (synchronizationLock)
        {
            if (initialized)
            {
                return;
            }
            string binaryDirectory = Path.GetDirectoryName(typeof(FFmpegNativeLoader).Assembly.Location)
                ?? AppContext.BaseDirectory;
            ffmpeg.RootPath = binaryDirectory;
            try
            {
                // Probe a known function to force the native load
                _ = ffmpeg.av_version_info();
            }
            catch (Exception exception)
            {
                throw new EncoderException("Failed to load FFmpeg native libraries.", exception);
            }
            initialized = true;
        }
    }
}
```

- [ ] **Step 4: Add the encoder skeleton (Configure + Request + Dispose)**

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using WindowStream.Core.Capture;

namespace WindowStream.Core.Encode;

public sealed unsafe class FFmpegNvencEncoder : IVideoEncoder
{
    private readonly IFFmpegNativeLoader nativeLoader;
    private readonly Channel<EncodedChunk> chunkChannel =
        Channel.CreateUnbounded<EncodedChunk>(new UnboundedChannelOptions { SingleReader = true });
    private AVCodecContext* codecContext;
    private AVFrame* stagingFrame;
    private AVPacket* reusablePacket;
    private SwsContext* softwareScaleContext;
    private EncoderOptions? options;
    private long frameIndex;
    private bool forceNextKeyframe;
    private bool disposed;

    public IAsyncEnumerable<EncodedChunk> EncodedChunks { get; }

    public FFmpegNvencEncoder() : this(new FFmpegNativeLoader()) { }
    public FFmpegNvencEncoder(IFFmpegNativeLoader nativeLoader)
    {
        this.nativeLoader = nativeLoader ?? throw new ArgumentNullException(nameof(nativeLoader));
        EncodedChunks = ReadAsync();
    }

    public void Configure(EncoderOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (this.options is not null) throw new InvalidOperationException("Configure already called.");
        nativeLoader.EnsureLoaded();

        AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc");
        if (codec == null)
        {
            throw new EncoderException("h264_nvenc codec not available in the loaded FFmpeg build.");
        }

        AVCodecContext* context = ffmpeg.avcodec_alloc_context3(codec);
        if (context == null)
        {
            throw new EncoderException("avcodec_alloc_context3 returned null.");
        }

        context->width = options.widthPixels;
        context->height = options.heightPixels;
        context->time_base = new AVRational { num = 1, den = options.framesPerSecond };
        context->framerate = new AVRational { num = options.framesPerSecond, den = 1 };
        context->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
        context->bit_rate = options.bitrateBitsPerSecond;
        context->gop_size = options.groupOfPicturesLength;
        context->max_b_frames = 0;

        ffmpeg.av_opt_set(context->priv_data, "preset", "p1", 0);       // lowest-latency NVENC preset
        ffmpeg.av_opt_set(context->priv_data, "tune", "ll", 0);          // low-latency tune
        ffmpeg.av_opt_set(context->priv_data, "zerolatency", "1", 0);
        ffmpeg.av_opt_set(context->priv_data, "rc", "cbr", 0);

        int openResult = ffmpeg.avcodec_open2(context, codec, null);
        if (openResult < 0)
        {
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("avcodec_open2 failed.", openResult);
        }

        AVFrame* frame = ffmpeg.av_frame_alloc();
        frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
        frame->width = options.widthPixels;
        frame->height = options.heightPixels;
        int allocateResult = ffmpeg.av_frame_get_buffer(frame, 32);
        if (allocateResult < 0)
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("av_frame_get_buffer failed.", allocateResult);
        }

        AVPacket* packet = ffmpeg.av_packet_alloc();
        if (packet == null)
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&context);
            throw new EncoderException("av_packet_alloc returned null.");
        }

        codecContext = context;
        stagingFrame = frame;
        reusablePacket = packet;
        softwareScaleContext = ffmpeg.sws_getContext(
            options.widthPixels, options.heightPixels, AVPixelFormat.AV_PIX_FMT_BGRA,
            options.widthPixels, options.heightPixels, AVPixelFormat.AV_PIX_FMT_NV12,
            ffmpeg.SWS_BILINEAR, null, null, null);
        this.options = options;
    }

    public void RequestKeyframe()
    {
        if (options is null) throw new InvalidOperationException("Configure must be called first.");
        forceNextKeyframe = true;
    }

    public async Task EncodeAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        if (options is null) throw new InvalidOperationException("Configure must be called before EncodeAsync.");
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() => EncodeOnThread(frame), cancellationToken).ConfigureAwait(false);
    }

    private void EncodeOnThread(CapturedFrame frame)
    {
        int scaleResult;
        fixed (byte* sourcePointer = frame.pixelBuffer.Span)
        {
            byte*[] sourceData = new byte*[4] { sourcePointer, null, null, null };
            int[] sourceStride = new int[4] { frame.rowStrideBytes, 0, 0, 0 };
            scaleResult = ffmpeg.sws_scale(
                softwareScaleContext,
                sourceData, sourceStride, 0, frame.heightPixels,
                stagingFrame->data, stagingFrame->linesize);
        }
        if (scaleResult < 0)
        {
            throw new EncoderException("sws_scale failed.", scaleResult);
        }

        stagingFrame->pts = frameIndex++;
        if (forceNextKeyframe)
        {
            stagingFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
            stagingFrame->key_frame = 1;
            forceNextKeyframe = false;
        }
        else
        {
            stagingFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
            stagingFrame->key_frame = 0;
        }

        int sendResult = ffmpeg.avcodec_send_frame(codecContext, stagingFrame);
        if (sendResult < 0)
        {
            throw new EncoderException("avcodec_send_frame failed.", sendResult);
        }

        while (true)
        {
            int receiveResult = ffmpeg.avcodec_receive_packet(codecContext, reusablePacket);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
            {
                break;
            }
            if (receiveResult < 0)
            {
                throw new EncoderException("avcodec_receive_packet failed.", receiveResult);
            }

            byte[] managed = new byte[reusablePacket->size];
            System.Runtime.InteropServices.Marshal.Copy(
                (IntPtr)reusablePacket->data, managed, 0, reusablePacket->size);
            bool isKeyframe = (reusablePacket->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
            long timestampMicroseconds = 1_000_000L * reusablePacket->pts
                * codecContext->time_base.num / codecContext->time_base.den;
            chunkChannel.Writer.TryWrite(new EncodedChunk(managed, isKeyframe, timestampMicroseconds));
            ffmpeg.av_packet_unref(reusablePacket);
        }
    }

    private async IAsyncEnumerable<EncodedChunk> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await chunkChannel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (chunkChannel.Reader.TryRead(out EncodedChunk? chunk))
            {
                yield return chunk;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        chunkChannel.Writer.TryComplete();

        if (reusablePacket != null)
        {
            AVPacket* packet = reusablePacket;
            ffmpeg.av_packet_free(&packet);
            reusablePacket = null;
        }
        if (stagingFrame != null)
        {
            AVFrame* frame = stagingFrame;
            ffmpeg.av_frame_free(&frame);
            stagingFrame = null;
        }
        if (codecContext != null)
        {
            AVCodecContext* context = codecContext;
            ffmpeg.avcodec_free_context(&context);
            codecContext = null;
        }
        if (softwareScaleContext != null)
        {
            ffmpeg.sws_freeContext(softwareScaleContext);
            softwareScaleContext = null;
        }
        return ValueTask.CompletedTask;
    }
}
```

(The `WindowStream.Core.csproj` must enable `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`; confirm it is set, and add it here if part A did not.)

- [ ] **Step 5: Run tests to confirm pass**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter "FullyQualifiedName~FFmpegNvencEncoderConstructionTests"`
Expected: PASS (5/5).

- [ ] **Step 6: Commit**

```bash
git add src/WindowStream.Core/Encode/FFmpegNativeLoader.cs src/WindowStream.Core/Encode/FFmpegNvencEncoder.cs src/WindowStream.Core/WindowStream.Core.csproj tests/WindowStream.Core.Tests/Encode/FFmpegNvencEncoderConstructionTests.cs
git commit -m "feat(core): add FFmpeg h264_nvenc encoder with pluggable native loader"
```

### Task 24: Coverage gate for phases 6-9

**Files:**
- Modify: `tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj` (ensure coverlet collector is referenced if part A has not done so):
  `<PackageReference Include="coverlet.collector" Version="6.0.0" PrivateAssets="all" />`
- Create: `tests/WindowStream.Core.Tests/coverlet.runsettings` (if not already added by part A — this task is idempotent if the file exists):

```xml
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <DataCollectionRunSettings>
    <DataCollectors>
      <DataCollector friendlyName="XPlat code coverage">
        <Configuration>
          <Format>cobertura</Format>
          <Exclude>[xunit.*]*,[*.Tests]*</Exclude>
          <ExcludeByAttribute>GeneratedCode</ExcludeByAttribute>
          <SingleHit>false</SingleHit>
          <UseSourceLink>false</UseSourceLink>
        </Configuration>
      </DataCollector>
    </DataCollectors>
  </DataCollectionRunSettings>
</RunSettings>
```

- [ ] **Step 1: Run coverage-enabled test run**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --collect:"XPlat Code Coverage" --settings tests/WindowStream.Core.Tests/coverlet.runsettings`
Expected: PASS. A `coverage.cobertura.xml` is produced under `tests/WindowStream.Core.Tests/TestResults/<guid>/`.

- [ ] **Step 2: Inspect the report for the types added in phases 6-9**

Confirm 100% line and 100% branch on each of:
- `WindowStream.Core.Discovery.ServiceTextRecords`
- `WindowStream.Core.Discovery.AdvertisementOptions`
- `WindowStream.Core.Discovery.ServerAdvertiser`
- `WindowStream.Core.Capture.CapturedFrame`
- `WindowStream.Core.Capture.Testing.FakeWindowCapture`
- `WindowStream.Core.Capture.Testing.FakeWindowCaptureSource`
- `WindowStream.Core.Capture.Windows.WindowEnumerator`
- `WindowStream.Core.Capture.Windows.WindowEnumerationFilters`
- `WindowStream.Core.Encode.EncoderOptions`
- `WindowStream.Core.Encode.EncodedChunk`
- `WindowStream.Core.Encode.Testing.FakeVideoEncoder`
- `WindowStream.Core.Encode.FFmpegNvencEncoder` — the non-native paths only; native-path statements are attributed to the integration suite in Phase 12.

Platform-gated types (`WgcCaptureSource`, `WgcCapture`, `WgcFrameConverter`, `Win32Api`) are excluded from the portable gate because they live behind `#if WINDOWS`; coverage for them comes from the Windows-only integration suite (Task 18, Task 21, Task 23) and is gated separately in part C.

- [ ] **Step 3: Commit runsettings if newly added**

```bash
git add tests/WindowStream.Core.Tests/coverlet.runsettings tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj
git commit -m "test(core): enable XPlat coverage collection for phase 6-9 types"
```

## Phase 10: Session wire-up

### Task 25: SessionHost — connect Session to capture, encode, and transport

**Files:**
- Create: `src/WindowStream.Core/Session/SessionHost.cs`
- Create: `src/WindowStream.Core/Session/SessionHostOptions.cs`
- Test: `tests/WindowStream.Core.Tests/Session/SessionHostTests.cs`

`SessionHost` is the composition root. It owns the TCP control listener, the UDP video socket, one `IWindowCaptureSource`, one `IVideoEncoder`, and one `Session`. Second viewer connections receive `ERROR{VIEWER_BUSY}` and are disconnected. HELLO → SERVER_HELLO handshake drives a `REQUEST_KEYFRAME` that is forwarded to the encoder. Heartbeats fire every 2 seconds; 6 seconds of TCP silence tears the viewer connection down (capture keeps running). Teardown cancels pumps in order: control listener, UDP socket, encoder, capture.

- [ ] **Step 1: Write the failing test for single-viewer acceptance and handshake**

```csharp
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Protocol;
using WindowStream.Core.Session;
using Xunit;

namespace WindowStream.Core.Tests.Session;

public sealed class SessionHostTests
{
    [Fact]
    public async Task Accepts_First_Viewer_And_Completes_Handshake()
    {
        using var cancellation = new CancellationTokenSource(TestTimeouts.Default);
        using var harness = await SessionHostTestHarness.StartAsync(cancellation.Token);

        await using var firstViewer = await harness.ConnectViewerAsync(cancellation.Token);
        await firstViewer.SendAsync(new HelloMessage(viewerVersion: 1, displayCapabilities: DisplayCapabilities.Default), cancellation.Token);

        var serverHello = await firstViewer.ReceiveAsync<ServerHelloMessage>(cancellation.Token);

        Assert.Equal(1, serverHello.ServerVersion);
        Assert.NotNull(serverHello.ActiveStream);
        Assert.Equal(harness.UdpPort, serverHello.ActiveStream!.UdpPort);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/WindowStream.Core.Tests --filter "FullyQualifiedName~SessionHostTests.Accepts_First_Viewer_And_Completes_Handshake"`
Expected: FAIL — `SessionHost` and `SessionHostTestHarness` do not exist yet.

- [ ] **Step 3: Write `SessionHostOptions.cs`**

```csharp
namespace WindowStream.Core.Session;

public sealed record SessionHostOptions(
    int TcpListenPort,
    int UdpListenPort,
    int HeartbeatIntervalMilliseconds = 2000,
    int HeartbeatTimeoutMilliseconds = 6000,
    int ServerVersion = 1);
```

- [ ] **Step 4: Write `SessionHost.cs` (minimal implementation)**

```csharp
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Encode;
using WindowStream.Core.Protocol;
using WindowStream.Core.Transport;

namespace WindowStream.Core.Session;

public sealed class SessionHost : IAsyncDisposable
{
    private readonly SessionHostOptions options;
    private readonly IWindowCaptureSource captureSource;
    private readonly IVideoEncoder videoEncoder;
    private readonly IControlChannelFactory controlChannelFactory;
    private readonly IUdpVideoSender udpVideoSender;
    private readonly TimeProvider timeProvider;

    private TcpListener? tcpListener;
    private Session? currentSession;
    private IControlChannel? activeChannel;
    private CancellationTokenSource? lifecycleCancellation;

    public SessionHost(
        SessionHostOptions options,
        IWindowCaptureSource captureSource,
        IVideoEncoder videoEncoder,
        IControlChannelFactory controlChannelFactory,
        IUdpVideoSender udpVideoSender,
        TimeProvider timeProvider)
    {
        this.options = options;
        this.captureSource = captureSource;
        this.videoEncoder = videoEncoder;
        this.controlChannelFactory = controlChannelFactory;
        this.udpVideoSender = udpVideoSender;
        this.timeProvider = timeProvider;
    }

    public int UdpPort => udpVideoSender.LocalPort;
    public int TcpPort => ((IPEndPoint)(tcpListener?.LocalEndpoint ?? throw new InvalidOperationException("not started"))).Port;

    public async Task StartAsync(WindowHandle targetWindow, CaptureOptions captureOptions, EncoderOptions encoderOptions, CancellationToken cancellationToken)
    {
        lifecycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await udpVideoSender.BindAsync(options.UdpListenPort, lifecycleCancellation.Token).ConfigureAwait(false);
        tcpListener = new TcpListener(IPAddress.Any, options.TcpListenPort);
        tcpListener.Start();

        currentSession = new Session(captureSource, videoEncoder, udpVideoSender, timeProvider);
        await currentSession.StartAsync(targetWindow, captureOptions, encoderOptions, lifecycleCancellation.Token).ConfigureAwait(false);
        _ = AcceptLoopAsync(lifecycleCancellation.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await tcpListener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var channel = controlChannelFactory.CreateServerChannel(client);
            if (activeChannel is not null)
            {
                await channel.SendAsync(new ErrorMessage(ErrorCodes.ViewerBusy, "server already has a connected viewer"), cancellationToken).ConfigureAwait(false);
                await channel.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            activeChannel = channel;
            _ = ServeViewerAsync(channel, cancellationToken);
        }
    }

    private async Task ServeViewerAsync(IControlChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            var hello = await channel.ReceiveAsync<HelloMessage>(cancellationToken).ConfigureAwait(false);
            _ = hello;
            var descriptor = currentSession!.ActiveStreamDescriptor;
            await channel.SendAsync(new ServerHelloMessage(options.ServerVersion, descriptor), cancellationToken).ConfigureAwait(false);

            using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var heartbeatTask = RunHeartbeatAsync(channel, heartbeatCancellation.Token);

            await foreach (var message in channel.IncomingMessages.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                switch (message)
                {
                    case RequestKeyframeMessage:
                        await videoEncoder.ForceKeyframeAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case HeartbeatMessage:
                        channel.NotifyHeartbeatReceived();
                        break;
                }
            }

            heartbeatCancellation.Cancel();
            await heartbeatTask.ConfigureAwait(false);
        }
        finally
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            activeChannel = null;
        }
    }

    private async Task RunHeartbeatAsync(IControlChannel channel, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(options.HeartbeatIntervalMilliseconds);
        var timeout = TimeSpan.FromMilliseconds(options.HeartbeatTimeoutMilliseconds);
        using var timer = new PeriodicTimer(interval, timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await channel.SendAsync(HeartbeatMessage.Instance, cancellationToken).ConfigureAwait(false);
            if (timeProvider.GetUtcNow() - channel.LastHeartbeatReceived > timeout)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifecycleCancellation?.Cancel();
        tcpListener?.Stop();
        if (activeChannel is not null)
        {
            await activeChannel.DisposeAsync().ConfigureAwait(false);
        }
        if (currentSession is not null)
        {
            await currentSession.StopAsync().ConfigureAwait(false);
        }
        await udpVideoSender.DisposeAsync().ConfigureAwait(false);
        lifecycleCancellation?.Dispose();
    }
}
```

- [ ] **Step 5: Write `SessionHostTestHarness.cs`**

```csharp
using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture.Fakes;
using WindowStream.Core.Encode.Fakes;
using WindowStream.Core.Protocol;
using WindowStream.Core.Session;
using WindowStream.Core.Transport;
using WindowStream.Core.Transport.Fakes;

namespace WindowStream.Core.Tests.Session;

internal sealed class SessionHostTestHarness : IAsyncDisposable
{
    public required SessionHost Host { get; init; }
    public required int TcpPort { get; init; }
    public required int UdpPort { get; init; }

    public static async Task<SessionHostTestHarness> StartAsync(CancellationToken cancellationToken)
    {
        var captureSource = new FakeWindowCaptureSource();
        var encoder = new FakeVideoEncoder();
        var udpSender = new FakeUdpVideoSender();
        var channelFactory = new JsonFramedControlChannelFactory();
        var options = new SessionHostOptions(TcpListenPort: 0, UdpListenPort: 0);
        var host = new SessionHost(options, captureSource, encoder, channelFactory, udpSender, TimeProvider.System);
        await host.StartAsync(WindowHandle.FromInt64(1), CaptureOptions.Default, EncoderOptions.Default, cancellationToken).ConfigureAwait(false);
        return new SessionHostTestHarness { Host = host, TcpPort = host.TcpPort, UdpPort = host.UdpPort };
    }

    public async Task<ViewerFake> ConnectViewerAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, TcpPort, cancellationToken).ConfigureAwait(false);
        return new ViewerFake(client);
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/WindowStream.Core.Tests --filter "FullyQualifiedName~SessionHostTests.Accepts_First_Viewer_And_Completes_Handshake"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/WindowStream.Core/Session/SessionHost.cs src/WindowStream.Core/Session/SessionHostOptions.cs tests/WindowStream.Core.Tests/Session/SessionHostTests.cs tests/WindowStream.Core.Tests/Session/SessionHostTestHarness.cs
git commit -m "feat(core): SessionHost accepts first viewer and completes HELLO handshake"
```

### Task 26: Reject second viewer with VIEWER_BUSY

**Files:**
- Modify: `tests/WindowStream.Core.Tests/Session/SessionHostTests.cs`
- Modify: `src/WindowStream.Core/Session/SessionHost.cs` (already handled in Task 30 minimal impl; this task pins behavior)

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Rejects_Second_Viewer_With_Viewer_Busy()
{
    using var cancellation = new CancellationTokenSource(TestTimeouts.Default);
    using var harness = await SessionHostTestHarness.StartAsync(cancellation.Token);

    await using var firstViewer = await harness.ConnectViewerAsync(cancellation.Token);
    await firstViewer.SendAsync(new HelloMessage(1, DisplayCapabilities.Default), cancellation.Token);
    _ = await firstViewer.ReceiveAsync<ServerHelloMessage>(cancellation.Token);

    await using var secondViewer = await harness.ConnectViewerAsync(cancellation.Token);
    var error = await secondViewer.ReceiveAsync<ErrorMessage>(cancellation.Token);

    Assert.Equal(ErrorCodes.ViewerBusy, error.Code);
}
```

- [ ] **Step 2: Run test to verify it fails or passes depending on Task 30 state**

Run: `dotnet test tests/WindowStream.Core.Tests --filter "FullyQualifiedName~SessionHostTests.Rejects_Second_Viewer_With_Viewer_Busy"`
Expected: PASS if Task 30 minimal impl covered it. If FAIL, adjust `AcceptLoopAsync` to send `ERROR{VIEWER_BUSY}` then `DisposeAsync()` before the next accept.

- [ ] **Step 3: Commit**

```bash
git add tests/WindowStream.Core.Tests/Session/SessionHostTests.cs src/WindowStream.Core/Session/SessionHost.cs
git commit -m "test(core): pin SessionHost VIEWER_BUSY rejection"
```

### Task 27: REQUEST_KEYFRAME forwarded to encoder

**Files:**
- Modify: `tests/WindowStream.Core.Tests/Session/SessionHostTests.cs`
- Modify: `src/WindowStream.Core/Encode/Fakes/FakeVideoEncoder.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Request_Keyframe_Forwards_To_Encoder()
{
    using var cancellation = new CancellationTokenSource(TestTimeouts.Default);
    using var harness = await SessionHostTestHarness.StartAsync(cancellation.Token);
    var fakeEncoder = harness.Encoder;

    await using var viewer = await harness.ConnectViewerAsync(cancellation.Token);
    await viewer.SendAsync(new HelloMessage(1, DisplayCapabilities.Default), cancellation.Token);
    _ = await viewer.ReceiveAsync<ServerHelloMessage>(cancellation.Token);

    await viewer.SendAsync(new RequestKeyframeMessage(streamId: 1), cancellation.Token);

    await TestPolling.UntilAsync(() => fakeEncoder.ForceKeyframeCount > 0, cancellation.Token);
    Assert.True(fakeEncoder.ForceKeyframeCount > 0);
}
```

- [ ] **Step 2: Run test to verify it fails**

Expected: FAIL if `FakeVideoEncoder.ForceKeyframeCount` not exposed.

- [ ] **Step 3: Add `ForceKeyframeCount` to `FakeVideoEncoder`**

```csharp
public int ForceKeyframeCount { get; private set; }

public Task ForceKeyframeAsync(CancellationToken cancellationToken)
{
    ForceKeyframeCount++;
    return Task.CompletedTask;
}
```

- [ ] **Step 4: Expose encoder on harness, run test**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/WindowStream.Core.Tests/Session/SessionHostTests.cs tests/WindowStream.Core.Tests/Session/SessionHostTestHarness.cs src/WindowStream.Core/Encode/Fakes/FakeVideoEncoder.cs
git commit -m "test(core): SessionHost forwards REQUEST_KEYFRAME to encoder"
```

### Task 28: Heartbeat send + timeout teardown

**Files:**
- Modify: `tests/WindowStream.Core.Tests/Session/SessionHostTests.cs`
- Modify: `src/WindowStream.Core/Session/SessionHost.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Sends_Heartbeat_At_Configured_Interval()
{
    using var cancellation = new CancellationTokenSource(TestTimeouts.Default);
    using var harness = await SessionHostTestHarness.StartAsync(cancellation.Token, heartbeatIntervalMilliseconds: 100, heartbeatTimeoutMilliseconds: 10_000);

    await using var viewer = await harness.ConnectViewerAsync(cancellation.Token);
    await viewer.SendAsync(new HelloMessage(1, DisplayCapabilities.Default), cancellation.Token);
    _ = await viewer.ReceiveAsync<ServerHelloMessage>(cancellation.Token);

    var firstHeartbeat = await viewer.ReceiveAsync<HeartbeatMessage>(cancellation.Token);
    var secondHeartbeat = await viewer.ReceiveAsync<HeartbeatMessage>(cancellation.Token);

    Assert.NotNull(firstHeartbeat);
    Assert.NotNull(secondHeartbeat);
}

[Fact]
public async Task Tears_Down_Viewer_On_Heartbeat_Timeout()
{
    using var cancellation = new CancellationTokenSource(TestTimeouts.Default);
    using var harness = await SessionHostTestHarness.StartAsync(cancellation.Token, heartbeatIntervalMilliseconds: 50, heartbeatTimeoutMilliseconds: 150);

    await using var viewer = await harness.ConnectViewerAsync(cancellation.Token);
    await viewer.SendAsync(new HelloMessage(1, DisplayCapabilities.Default), cancellation.Token);
    _ = await viewer.ReceiveAsync<ServerHelloMessage>(cancellation.Token);

    await TestPolling.UntilAsync(() => viewer.RemoteClosed, cancellation.Token);
    Assert.True(viewer.RemoteClosed);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: FAIL — heartbeat logic may not yet trigger in minimal impl.

- [ ] **Step 3: Ensure heartbeat loop sends and enforces timeout (already drafted in Task 30 — verify and fix)**

Check `RunHeartbeatAsync` uses `channel.LastHeartbeatReceived` (initialized to `timeProvider.GetUtcNow()` on construction) and disposes the channel when the gap exceeds `HeartbeatTimeoutMilliseconds`.

- [ ] **Step 4: Run tests**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/WindowStream.Core.Tests/Session/SessionHostTests.cs src/WindowStream.Core/Session/SessionHost.cs
git commit -m "feat(core): heartbeat send + 6s silence teardown in SessionHost"
```

### Task 29: Encoded chunks flow to UDP sender

**Files:**
- Modify: `tests/WindowStream.Core.Tests/Session/SessionHostTests.cs`
- Modify: `src/WindowStream.Core/Session/Session.cs` (glue pipeline)

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Encoded_Chunks_Are_Forwarded_To_Udp_Sender()
{
    using var cancellation = new CancellationTokenSource(TestTimeouts.Default);
    using var harness = await SessionHostTestHarness.StartAsync(cancellation.Token);

    harness.CaptureSource.EmitFrame(new CapturedFrame(width: 320, height: 240, presentationTimestampMicroseconds: 0L, pixelBuffer: TestFixtures.SolidRed(320, 240)));
    harness.Encoder.EmitChunk(new EncodedChunk(streamId: 1, sequence: 0, presentationTimestampMicroseconds: 0L, isKeyframe: true, nalUnitBytes: TestFixtures.MinimalIdrNalUnit()));

    await TestPolling.UntilAsync(() => harness.UdpSender.SentPacketCount > 0, cancellation.Token);
    Assert.True(harness.UdpSender.SentPacketCount > 0);
}
```

- [ ] **Step 2: Run test to verify it fails**

Expected: FAIL — `Session` pump may not yet wire encoder output to UDP sender.

- [ ] **Step 3: Wire the pump in `Session.StartAsync`**

```csharp
_ = Task.Run(async () =>
{
    await foreach (var frame in captureSource.Frames.WithCancellation(sessionCancellation.Token).ConfigureAwait(false))
    {
        await videoEncoder.EncodeAsync(frame, sessionCancellation.Token).ConfigureAwait(false);
    }
}, sessionCancellation.Token);

_ = Task.Run(async () =>
{
    await foreach (var chunk in videoEncoder.EncodedChunks.WithCancellation(sessionCancellation.Token).ConfigureAwait(false))
    {
        await udpVideoSender.SendEncodedChunkAsync(chunk, sessionCancellation.Token).ConfigureAwait(false);
    }
}, sessionCancellation.Token);
```

- [ ] **Step 4: Run test**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Session/Session.cs tests/WindowStream.Core.Tests/Session/SessionHostTests.cs
git commit -m "feat(core): Session pipes capture to encoder to UDP sender"
```

### Task 30: Teardown cancels all pumps cleanly

**Files:**
- Modify: `tests/WindowStream.Core.Tests/Session/SessionHostTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Disposing_Host_Stops_Capture_Encoder_And_Closes_Sockets()
{
    using var cancellation = new CancellationTokenSource(TestTimeouts.Default);
    var harness = await SessionHostTestHarness.StartAsync(cancellation.Token);

    await harness.DisposeAsync();

    Assert.True(harness.CaptureSource.Stopped);
    Assert.True(harness.Encoder.Stopped);
    Assert.True(harness.UdpSender.Disposed);
}
```

- [ ] **Step 2: Run test**

Expected: PASS if teardown order is correct; FAIL if any pump is still observed alive.

- [ ] **Step 3: Fix ordering in `SessionHost.DisposeAsync` if needed, run test**

Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/WindowStream.Core.Tests/Session/SessionHostTests.cs src/WindowStream.Core/Session/SessionHost.cs
git commit -m "test(core): SessionHost teardown stops all pumps"
```

## Phase 11: CLI (`WindowStream.Cli`)

### Task 31: CLI project scaffolding and `list` command

**Files:**
- Create: `src/WindowStream.Cli/WindowStream.Cli.csproj`
- Create: `src/WindowStream.Cli/Program.cs`
- Create: `src/WindowStream.Cli/Commands/ListWindowsCommand.cs`
- Create: `src/WindowStream.Cli/Commands/ListWindowsCommandHandler.cs`
- Test: `tests/WindowStream.Core.Tests/Cli/ListWindowsCommandTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.CommandLine;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Cli.Commands;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Fakes;
using Xunit;

namespace WindowStream.Core.Tests.Cli;

public sealed class ListWindowsCommandTests
{
    [Fact]
    public async Task Prints_Table_With_Handle_Title_And_Process_Name()
    {
        var captureSource = new FakeWindowCaptureSource();
        captureSource.SeedWindow(new WindowInformation(WindowHandle.FromInt64(42), "Notepad - Untitled", "notepad"));
        captureSource.SeedWindow(new WindowInformation(WindowHandle.FromInt64(43), "WindowStream CLI", "dotnet"));

        using var writer = new StringWriter();
        var handler = new ListWindowsCommandHandler(captureSource, writer);

        var exitCode = await handler.ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        var output = writer.ToString();
        Assert.Contains("42", output);
        Assert.Contains("Notepad - Untitled", output);
        Assert.Contains("notepad", output);
        Assert.Contains("43", output);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/WindowStream.Core.Tests --filter "FullyQualifiedName~ListWindowsCommandTests"`
Expected: FAIL — `ListWindowsCommandHandler` does not exist.

- [ ] **Step 3: Create `WindowStream.Cli.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <AssemblyName>windowstream</AssemblyName>
    <RootNamespace>WindowStream.Cli</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.CommandLine" Version="2.0.0-beta4.22272.1" />
    <ProjectReference Include="..\WindowStream.Core\WindowStream.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Implement `ListWindowsCommandHandler.cs`**

```csharp
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;

namespace WindowStream.Cli.Commands;

public sealed class ListWindowsCommandHandler
{
    private readonly IWindowCaptureSource captureSource;
    private readonly TextWriter writer;

    public ListWindowsCommandHandler(IWindowCaptureSource captureSource, TextWriter writer)
    {
        this.captureSource = captureSource;
        this.writer = writer;
    }

    public Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        writer.WriteLine($"{"HANDLE",-12} {"PROCESS",-20} TITLE");
        foreach (var window in captureSource.ListWindows())
        {
            writer.WriteLine($"{window.Handle.ToInt64(),-12} {window.ProcessName,-20} {window.Title}");
        }
        return Task.FromResult(0);
    }
}
```

- [ ] **Step 5: Run test**

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/WindowStream.Cli tests/WindowStream.Core.Tests/Cli/ListWindowsCommandTests.cs
git commit -m "feat(cli): list command prints handle, title, and process name"
```

### Task 32: `serve --hwnd` and `serve --title-matches` command wiring

**Files:**
- Create: `src/WindowStream.Cli/Commands/ServeCommand.cs`
- Create: `src/WindowStream.Cli/Commands/ServeCommandHandler.cs`
- Modify: `src/WindowStream.Cli/Program.cs`
- Test: `tests/WindowStream.Core.Tests/Cli/ServeCommandTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Cli.Commands;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Fakes;
using WindowStream.Core.Session.Fakes;
using Xunit;

namespace WindowStream.Core.Tests.Cli;

public sealed class ServeCommandTests
{
    [Fact]
    public async Task Serve_With_Hwnd_Starts_Session_On_Specified_Handle()
    {
        var captureSource = new FakeWindowCaptureSource();
        captureSource.SeedWindow(new WindowInformation(WindowHandle.FromInt64(1001), "Target", "notepad"));
        var hostLauncher = new FakeSessionHostLauncher();
        var handler = new ServeCommandHandler(captureSource, hostLauncher);

        using var cancellation = new CancellationTokenSource();
        _ = Task.Run(async () => { await Task.Delay(50); cancellation.Cancel(); });

        var exitCode = await handler.ExecuteAsync(new ServeArguments(Handle: WindowHandle.FromInt64(1001), TitlePattern: null), cancellation.Token);

        Assert.Equal(0, exitCode);
        Assert.Equal(WindowHandle.FromInt64(1001), hostLauncher.LaunchedHandle);
    }

    [Fact]
    public async Task Serve_With_Title_Matches_Finds_First_Visible_Window()
    {
        var captureSource = new FakeWindowCaptureSource();
        captureSource.SeedWindow(new WindowInformation(WindowHandle.FromInt64(7), "Chrome", "chrome"));
        captureSource.SeedWindow(new WindowInformation(WindowHandle.FromInt64(8), "Notepad - README", "notepad"));
        var hostLauncher = new FakeSessionHostLauncher();
        var handler = new ServeCommandHandler(captureSource, hostLauncher);

        using var cancellation = new CancellationTokenSource();
        _ = Task.Run(async () => { await Task.Delay(50); cancellation.Cancel(); });

        var exitCode = await handler.ExecuteAsync(new ServeArguments(Handle: null, TitlePattern: "Notepad.*"), cancellation.Token);

        Assert.Equal(0, exitCode);
        Assert.Equal(WindowHandle.FromInt64(8), hostLauncher.LaunchedHandle);
    }

    [Fact]
    public async Task Serve_With_Title_Matches_Returns_Error_When_No_Match()
    {
        var captureSource = new FakeWindowCaptureSource();
        captureSource.SeedWindow(new WindowInformation(WindowHandle.FromInt64(7), "Chrome", "chrome"));
        var hostLauncher = new FakeSessionHostLauncher();
        var handler = new ServeCommandHandler(captureSource, hostLauncher);

        var exitCode = await handler.ExecuteAsync(new ServeArguments(Handle: null, TitlePattern: "Nope"), CancellationToken.None);

        Assert.Equal(2, exitCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: FAIL — `ServeCommandHandler` does not exist.

- [ ] **Step 3: Implement `ServeCommandHandler`**

```csharp
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Session;

namespace WindowStream.Cli.Commands;

public sealed record ServeArguments(WindowHandle? Handle, string? TitlePattern);

public sealed class ServeCommandHandler
{
    private readonly IWindowCaptureSource captureSource;
    private readonly ISessionHostLauncher hostLauncher;

    public ServeCommandHandler(IWindowCaptureSource captureSource, ISessionHostLauncher hostLauncher)
    {
        this.captureSource = captureSource;
        this.hostLauncher = hostLauncher;
    }

    public async Task<int> ExecuteAsync(ServeArguments arguments, CancellationToken cancellationToken)
    {
        WindowHandle? resolved = arguments.Handle;
        if (resolved is null && arguments.TitlePattern is not null)
        {
            var pattern = new Regex(arguments.TitlePattern, RegexOptions.CultureInvariant);
            var firstMatch = captureSource.ListWindows().FirstOrDefault(window => pattern.IsMatch(window.Title));
            if (firstMatch is null)
            {
                return 2;
            }
            resolved = firstMatch.Handle;
        }

        if (resolved is null)
        {
            return 2;
        }

        await hostLauncher.LaunchAsync(resolved.Value, cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
```

- [ ] **Step 4: Run tests**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Cli tests/WindowStream.Core.Tests/Cli/ServeCommandTests.cs
git commit -m "feat(cli): serve command resolves hwnd or title pattern to session"
```

### Task 33: Root command parsing and Ctrl-C cancellation

**Files:**
- Modify: `src/WindowStream.Cli/Program.cs`
- Create: `src/WindowStream.Cli/RootCommandBuilder.cs`
- Test: `tests/WindowStream.Core.Tests/Cli/RootCommandBuilderTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using WindowStream.Cli;
using Xunit;

namespace WindowStream.Core.Tests.Cli;

public sealed class RootCommandBuilderTests
{
    [Fact]
    public void Builds_Root_Command_With_List_And_Serve_Subcommands()
    {
        var root = RootCommandBuilder.Build(new FakeCliServices());

        Assert.Contains(root.Children, child => child.Name == "list");
        Assert.Contains(root.Children, child => child.Name == "serve");
    }

    [Fact]
    public void Serve_Parses_Hwnd_Option_As_Long()
    {
        var root = RootCommandBuilder.Build(new FakeCliServices());
        var result = root.Parse(new[] { "serve", "--hwnd", "1234" });
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Serve_Parses_Title_Matches_Option_As_String()
    {
        var root = RootCommandBuilder.Build(new FakeCliServices());
        var result = root.Parse(new[] { "serve", "--title-matches", "Notepad.*" });
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Serve_Rejects_When_Neither_Hwnd_Nor_Title_Matches_Provided()
    {
        var root = RootCommandBuilder.Build(new FakeCliServices());
        var result = root.Parse(new[] { "serve" });
        Assert.NotEmpty(result.Errors);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Expected: FAIL — `RootCommandBuilder` does not exist.

- [ ] **Step 3: Implement `RootCommandBuilder`**

```csharp
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading;
using WindowStream.Cli.Commands;
using WindowStream.Core.Capture;

namespace WindowStream.Cli;

public static class RootCommandBuilder
{
    public static RootCommand Build(ICliServices services)
    {
        var root = new RootCommand("WindowStream CLI");

        var listCommand = new Command("list", "Enumerate streamable windows");
        listCommand.SetHandler(async invocationContext =>
        {
            var handler = new ListWindowsCommandHandler(services.CaptureSource, services.Output);
            invocationContext.ExitCode = await handler.ExecuteAsync(invocationContext.GetCancellationToken());
        });
        root.AddCommand(listCommand);

        var handleOption = new Option<long?>("--hwnd", "HWND of window to stream");
        var titleOption = new Option<string?>("--title-matches", "Regex matched against window titles");
        var serveCommand = new Command("serve", "Start a streaming session")
        {
            handleOption,
            titleOption
        };
        serveCommand.AddValidator(result =>
        {
            if (result.GetValueForOption(handleOption) is null && string.IsNullOrEmpty(result.GetValueForOption(titleOption)))
            {
                result.ErrorMessage = "Provide --hwnd or --title-matches";
            }
        });
        serveCommand.SetHandler(async invocationContext =>
        {
            var rawHandle = invocationContext.ParseResult.GetValueForOption(handleOption);
            var titlePattern = invocationContext.ParseResult.GetValueForOption(titleOption);
            var arguments = new ServeArguments(
                Handle: rawHandle is null ? null : WindowHandle.FromInt64(rawHandle.Value),
                TitlePattern: titlePattern);
            var handler = new ServeCommandHandler(services.CaptureSource, services.HostLauncher);
            invocationContext.ExitCode = await handler.ExecuteAsync(arguments, invocationContext.GetCancellationToken());
        });
        root.AddCommand(serveCommand);

        return root;
    }
}
```

- [ ] **Step 4: Implement `Program.cs` with Ctrl-C wiring**

```csharp
using System;
using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Cli;

var services = CliServices.CreateDefault();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArguments) =>
{
    eventArguments.Cancel = true;
    cancellation.Cancel();
};
var root = RootCommandBuilder.Build(services);
return await root.InvokeAsync(args);
```

- [ ] **Step 5: Run tests**

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/WindowStream.Cli tests/WindowStream.Core.Tests/Cli/RootCommandBuilderTests.cs
git commit -m "feat(cli): root command with list/serve subcommands and Ctrl-C cancellation"
```

## Phase 12: Integration tests (`WindowStream.Integration.Tests`)

### Task 34: Integration test project scaffolding

**Files:**
- Create: `tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj`
- Create: `tests/WindowStream.Integration.Tests/Infrastructure/DesktopSessionFacts.cs`
- Create: `tests/WindowStream.Integration.Tests/Infrastructure/NvidiaDriverFacts.cs`

- [ ] **Step 1: Create `.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net8.0-windows10.0.19041.0</TargetFrameworks>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="FFmpeg.AutoGen" Version="7.0.0" />
    <ProjectReference Include="..\..\src\WindowStream.Core\WindowStream.Core.csproj" />
    <ProjectReference Include="..\..\src\WindowStream.Cli\WindowStream.Cli.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Implement environment gate helpers**

```csharp
using System;
using Xunit;

namespace WindowStream.Integration.Tests.Infrastructure;

public sealed class DesktopSessionFactAttribute : FactAttribute
{
    public DesktopSessionFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WINDOWSTREAM_SKIP_DESKTOP") == "1" || Environment.UserInteractive == false)
        {
            Skip = "Requires an interactive desktop session";
        }
    }
}

public sealed class NvidiaDriverFactAttribute : FactAttribute
{
    public NvidiaDriverFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WINDOWSTREAM_SKIP_NVENC") == "1")
        {
            Skip = "Requires NVIDIA driver with NVENC";
        }
    }
}
```

- [ ] **Step 3: Commit**

```bash
git add tests/WindowStream.Integration.Tests
git commit -m "feat(integration): scaffold integration test project with environment gates"
```

### Task 35: WGC can-attach smoke test

**Files:**
- Create: `tests/WindowStream.Integration.Tests/Capture/WgcCanAttachSmokeTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
#if WINDOWS
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using WindowStream.Integration.Tests.Infrastructure;
using Xunit;

namespace WindowStream.Integration.Tests.Capture;

public sealed class WgcCanAttachSmokeTests
{
    [DesktopSessionFact]
    public async Task Attaches_To_Notepad_And_Delivers_A_Frame()
    {
        using var notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })!;
        try
        {
            notepad.WaitForInputIdle(TimeSpan.FromSeconds(5).Milliseconds);
            var enumerator = new WindowEnumerator();
            var target = await WaitForWindowAsync(enumerator, process => process.Title.Contains("Notepad", StringComparison.OrdinalIgnoreCase));

            using var source = new WgcCaptureSource();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await using var capture = source.Start(target.Handle, CaptureOptions.Default);
            var firstFrame = await capture.Frames.GetAsyncEnumerator(cancellation.Token).MoveNextAsync();
            Assert.True(firstFrame);
        }
        finally
        {
            try { notepad.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }

    private static async Task<WindowInformation> WaitForWindowAsync(WindowEnumerator enumerator, Func<WindowInformation, bool> predicate)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var match = enumerator.EnumerateWindows().FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }
            await Task.Delay(100);
        }
        throw new InvalidOperationException("target window never appeared");
    }
}
#endif
```

- [ ] **Step 2: Run test**

Run: `dotnet test tests/WindowStream.Integration.Tests -f net8.0-windows10.0.19041.0 --filter "FullyQualifiedName~WgcCanAttachSmokeTests"`
Expected: FAIL first run if `WgcCaptureSource` or `WindowEnumerator` lack expected surface; fix until PASS on a machine with a desktop.

- [ ] **Step 3: Commit**

```bash
git add tests/WindowStream.Integration.Tests/Capture/WgcCanAttachSmokeTests.cs
git commit -m "test(integration): WGC attaches to Notepad and delivers a frame"
```

### Task 36: NVENC init smoke test

**Files:**
- Create: `tests/WindowStream.Integration.Tests/Encode/NvencInitSmokeTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
#if WINDOWS
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Encode;
using WindowStream.Integration.Tests.Infrastructure;
using Xunit;

namespace WindowStream.Integration.Tests.Encode;

public sealed class NvencInitSmokeTests
{
    [NvidiaDriverFact]
    public async Task Configures_And_Encodes_A_Single_Solid_Color_Frame()
    {
        using var encoder = new FFmpegNvencEncoder();
        var options = new EncoderOptions(Width: 640, Height: 360, FramesPerSecond: 30, BitrateKilobitsPerSecond: 4000);
        encoder.Configure(options);

        var frame = TestFrameFactory.CreateSolidColor(640, 360, red: 32, green: 128, blue: 64);
        using var cancellation = new CancellationTokenSource(System.TimeSpan.FromSeconds(3));

        await encoder.EncodeAsync(frame, cancellation.Token);
        await encoder.ForceKeyframeAsync(cancellation.Token);

        EncodedChunk? firstChunk = null;
        await foreach (var chunk in encoder.EncodedChunks.WithCancellation(cancellation.Token))
        {
            firstChunk = chunk;
            break;
        }

        Assert.NotNull(firstChunk);
        Assert.True(firstChunk!.NalUnitBytes.Length > 0);
    }
}
#endif
```

- [ ] **Step 2: Run test**

Run: `dotnet test tests/WindowStream.Integration.Tests -f net8.0-windows10.0.19041.0 --filter "FullyQualifiedName~NvencInitSmokeTests"`
Expected: PASS on a machine with NVIDIA driver; otherwise the `[NvidiaDriverFact]` skip keeps it green.

- [ ] **Step 3: Commit**

```bash
git add tests/WindowStream.Integration.Tests/Encode/NvencInitSmokeTests.cs
git commit -m "test(integration): NVENC configures and emits an encoded chunk"
```

### Task 37: CLI + loopback end-to-end smoke test

**Files:**
- Create: `tests/WindowStream.Integration.Tests/Loopback/CliLoopbackHarness.cs`
- Create: `tests/WindowStream.Integration.Tests/Loopback/SoftwareDecoder.cs`
- Create: `tests/WindowStream.Integration.Tests/Loopback/CliLoopbackEndToEndTests.cs`

`CliLoopbackHarness` spawns `windowstream.exe serve --title-matches Notepad` on `127.0.0.1`, accepts the TCP control connection, issues `HELLO` + `REQUEST_KEYFRAME`, reassembles fragmented UDP packets, decodes NAL units with FFmpeg.AutoGen's software H.264 decoder, and exposes decoded frames for assertions.

- [ ] **Step 1: Write the failing test**

```csharp
#if WINDOWS
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Integration.Tests.Infrastructure;
using Xunit;

namespace WindowStream.Integration.Tests.Loopback;

public sealed class CliLoopbackEndToEndTests
{
    [DesktopSessionFact]
    public async Task Cli_Serve_Produces_Decodable_Idr_Frames_Over_Loopback()
    {
        using var notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })!;
        try
        {
            notepad.WaitForInputIdle(2000);
            await using var harness = await CliLoopbackHarness.LaunchAsync("--title-matches", "Notepad", TimeSpan.FromSeconds(10));

            var idrFrameCount = 0;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await foreach (var decoded in harness.DecodedFrames.WithCancellation(cancellation.Token))
            {
                if (decoded.IsKeyframe)
                {
                    idrFrameCount++;
                    Assert.Equal(harness.StreamDescriptor.Width, decoded.Width);
                    Assert.Equal(harness.StreamDescriptor.Height, decoded.Height);
                }
                if (idrFrameCount >= 2)
                {
                    break;
                }
            }
            Assert.True(idrFrameCount >= 2);
        }
        finally
        {
            try { notepad.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }
}
#endif
```

- [ ] **Step 2: Implement `CliLoopbackHarness.cs`**

```csharp
#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WindowStream.Core.Protocol;

namespace WindowStream.Integration.Tests.Loopback;

public sealed class CliLoopbackHarness : IAsyncDisposable
{
    private readonly Process cliProcess;
    private readonly TcpListener tcpListener;
    private readonly UdpClient udpReceiver;
    private readonly SoftwareDecoder decoder;
    private readonly Channel<DecodedFrame> decodedChannel = Channel.CreateUnbounded<DecodedFrame>();

    public ActiveStreamDescriptor StreamDescriptor { get; private set; } = null!;
    public IAsyncEnumerable<DecodedFrame> DecodedFrames => decodedChannel.Reader.ReadAllAsync();

    private CliLoopbackHarness(Process cliProcess, TcpListener tcpListener, UdpClient udpReceiver, SoftwareDecoder decoder)
    {
        this.cliProcess = cliProcess;
        this.tcpListener = tcpListener;
        this.udpReceiver = udpReceiver;
        this.decoder = decoder;
    }

    public static async Task<CliLoopbackHarness> LaunchAsync(string flag, string value, TimeSpan timeout)
    {
        var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var udpReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var controlPort = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        var udpPort = ((IPEndPoint)udpReceiver.Client.LocalEndPoint!).Port;

        var startInfo = new ProcessStartInfo
        {
            FileName = CliBinaryLocator.Find(),
            ArgumentList = { "serve", flag, value, "--bind", "127.0.0.1", "--tcp-port", controlPort.ToString(), "--udp-target-port", udpPort.ToString() },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        var process = Process.Start(startInfo)!;
        var decoder = new SoftwareDecoder();
        var harness = new CliLoopbackHarness(process, tcpListener, udpReceiver, decoder);
        await harness.RunControlAndVideoPumpsAsync(timeout).ConfigureAwait(false);
        return harness;
    }

    private async Task RunControlAndVideoPumpsAsync(TimeSpan timeout)
    {
        using var acceptCancellation = new CancellationTokenSource(timeout);
        var tcpClient = await tcpListener.AcceptTcpClientAsync(acceptCancellation.Token).ConfigureAwait(false);
        var stream = tcpClient.GetStream();

        await LengthPrefixedJson.WriteAsync(stream, new HelloMessage(1, DisplayCapabilities.Default), acceptCancellation.Token).ConfigureAwait(false);
        var serverHello = await LengthPrefixedJson.ReadAsync<ServerHelloMessage>(stream, acceptCancellation.Token).ConfigureAwait(false);
        StreamDescriptor = serverHello.ActiveStream ?? throw new InvalidOperationException("no active stream");
        await LengthPrefixedJson.WriteAsync(stream, new RequestKeyframeMessage(StreamDescriptor.StreamId), acceptCancellation.Token).ConfigureAwait(false);

        _ = Task.Run(async () =>
        {
            var reassembler = new UdpFragmentReassembler();
            while (!acceptCancellation.IsCancellationRequested)
            {
                var receive = await udpReceiver.ReceiveAsync().ConfigureAwait(false);
                if (reassembler.TryAccept(receive.Buffer, out var nalUnit, out var isKeyframe))
                {
                    if (decoder.TryDecode(nalUnit, out var frame))
                    {
                        await decodedChannel.Writer.WriteAsync(new DecodedFrame(frame.Width, frame.Height, isKeyframe)).ConfigureAwait(false);
                    }
                }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!cliProcess.HasExited) cliProcess.Kill(entireProcessTree: true); } catch { /* ignore */ }
        tcpListener.Stop();
        udpReceiver.Dispose();
        decoder.Dispose();
        decodedChannel.Writer.TryComplete();
        await Task.CompletedTask;
    }
}

public sealed record DecodedFrame(int Width, int Height, bool IsKeyframe);
#endif
```

- [ ] **Step 3: Implement `SoftwareDecoder.cs` (FFmpeg.AutoGen H.264 decode)**

```csharp
#if WINDOWS
using System;
using FFmpeg.AutoGen;

namespace WindowStream.Integration.Tests.Loopback;

public sealed class SoftwareDecoder : IDisposable
{
    private unsafe AVCodecContext* context;
    private unsafe AVPacket* packet;
    private unsafe AVFrame* frame;

    public SoftwareDecoder()
    {
        unsafe
        {
            var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
            context = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_open2(context, codec, null).ThrowIfError();
            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
        }
    }

    public bool TryDecode(ReadOnlySpan<byte> nalUnit, out (int Width, int Height) frameSize)
    {
        unsafe
        {
            fixed (byte* data = nalUnit)
            {
                packet->data = data;
                packet->size = nalUnit.Length;
                if (ffmpeg.avcodec_send_packet(context, packet) < 0)
                {
                    frameSize = default;
                    return false;
                }
                if (ffmpeg.avcodec_receive_frame(context, frame) < 0)
                {
                    frameSize = default;
                    return false;
                }
                frameSize = (frame->width, frame->height);
                return true;
            }
        }
    }

    public void Dispose()
    {
        unsafe
        {
            if (packet != null) { var localPacket = packet; ffmpeg.av_packet_free(&localPacket); packet = null; }
            if (frame != null) { var localFrame = frame; ffmpeg.av_frame_free(&localFrame); frame = null; }
            if (context != null) { var localContext = context; ffmpeg.avcodec_free_context(&localContext); context = null; }
        }
    }
}
#endif
```

- [ ] **Step 4: Run end-to-end test**

Run: `dotnet test tests/WindowStream.Integration.Tests -f net8.0-windows10.0.19041.0 --filter "FullyQualifiedName~CliLoopbackEndToEndTests"`
Expected: PASS on a development machine with NVIDIA driver + desktop session.

- [ ] **Step 5: Commit**

```bash
git add tests/WindowStream.Integration.Tests/Loopback
git commit -m "test(integration): CLI serve + loopback decodes IDR frames end-to-end"
```

## Phase 13: MAUI picker UI (`WindowStreamServer`)

### Task 38: MAUI project scaffolding

**Files:**
- Create: `src/WindowStreamServer/WindowStreamServer.csproj`
- Create: `src/WindowStreamServer/MauiProgram.cs`
- Create: `src/WindowStreamServer/App.xaml`
- Create: `src/WindowStreamServer/App.xaml.cs`
- Create: `src/WindowStreamServer/AppShell.xaml`
- Create: `src/WindowStreamServer/AppShell.xaml.cs`

- [ ] **Step 1: Create `WindowStreamServer.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0-windows10.0.19041.0</TargetFrameworks>
    <OutputType>Exe</OutputType>
    <UseMaui>true</UseMaui>
    <SingleProject>true</SingleProject>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <RootNamespace>WindowStream.Server</RootNamespace>
    <ApplicationId>com.mtschoen.windowstream.server</ApplicationId>
    <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
    <ApplicationVersion>1</ApplicationVersion>
    <WindowsPackageType>None</WindowsPackageType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\WindowStream.Core\WindowStream.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write `MauiProgram.cs`**

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;
using WindowStream.Server.ViewModels;

namespace WindowStream.Server;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddSingleton<WindowPickerViewModel>();
        builder.Services.AddSingleton<SessionViewModel>();
        builder.Logging.AddDebug();
        return builder.Build();
    }
}
```

- [ ] **Step 3: Commit**

```bash
git add src/WindowStreamServer
git commit -m "feat(server): MAUI application skeleton"
```

### Task 39: `WindowPickerViewModel`

**Files:**
- Create: `src/WindowStreamServer/ViewModels/WindowPickerViewModel.cs`
- Test: `tests/WindowStream.Core.Tests/ViewModels/WindowPickerViewModelTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Fakes;
using WindowStream.Core.Session.Fakes;
using WindowStream.Server.ViewModels;
using Xunit;

namespace WindowStream.Core.Tests.ViewModels;

public sealed class WindowPickerViewModelTests
{
    [Fact]
    public void Refresh_Populates_Windows_From_Capture_Source()
    {
        var captureSource = new FakeWindowCaptureSource();
        captureSource.SeedWindow(new WindowInformation(WindowHandle.FromInt64(1), "Alpha", "alpha"));
        captureSource.SeedWindow(new WindowInformation(WindowHandle.FromInt64(2), "Beta", "beta"));
        var launcher = new FakeSessionHostLauncher();
        var viewModel = new WindowPickerViewModel(captureSource, launcher);

        viewModel.Refresh();

        Assert.Equal(2, viewModel.Windows.Count);
    }

    [Fact]
    public async Task Start_Stream_Invokes_Launcher_With_Selected_Window()
    {
        var captureSource = new FakeWindowCaptureSource();
        var information = new WindowInformation(WindowHandle.FromInt64(99), "Rider", "rider");
        captureSource.SeedWindow(information);
        var launcher = new FakeSessionHostLauncher();
        var viewModel = new WindowPickerViewModel(captureSource, launcher);
        viewModel.Refresh();

        await viewModel.StartStreamAsync(information, CancellationToken.None);

        Assert.Equal(information.Handle, launcher.LaunchedHandle);
    }
}
```

- [ ] **Step 2: Run tests**

Expected: FAIL — `WindowPickerViewModel` does not exist.

- [ ] **Step 3: Implement `WindowPickerViewModel.cs`**

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Session;

namespace WindowStream.Server.ViewModels;

public sealed class WindowPickerViewModel : INotifyPropertyChanged
{
    private readonly IWindowCaptureSource captureSource;
    private readonly ISessionHostLauncher hostLauncher;

    public ObservableCollection<WindowInformation> Windows { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public WindowPickerViewModel(IWindowCaptureSource captureSource, ISessionHostLauncher hostLauncher)
    {
        this.captureSource = captureSource;
        this.hostLauncher = hostLauncher;
    }

    public void Refresh()
    {
        Windows.Clear();
        foreach (var window in captureSource.ListWindows())
        {
            Windows.Add(window);
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Windows)));
    }

    public Task StartStreamAsync(WindowInformation window, CancellationToken cancellationToken)
    {
        return hostLauncher.LaunchAsync(window.Handle, cancellationToken);
    }
}
```

- [ ] **Step 4: Run tests**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WindowStreamServer/ViewModels/WindowPickerViewModel.cs tests/WindowStream.Core.Tests/ViewModels/WindowPickerViewModelTests.cs
git commit -m "feat(server): WindowPickerViewModel populates and launches streams"
```

### Task 40: `SessionViewModel`

**Files:**
- Create: `src/WindowStreamServer/ViewModels/SessionViewModel.cs`
- Test: `tests/WindowStream.Core.Tests/ViewModels/SessionViewModelTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.ComponentModel;
using WindowStream.Core.Session;
using WindowStream.Server.ViewModels;
using Xunit;

namespace WindowStream.Core.Tests.ViewModels;

public sealed class SessionViewModelTests
{
    [Fact]
    public void Initial_State_Is_Idle()
    {
        var viewModel = new SessionViewModel();
        Assert.Equal(SessionStatus.Idle, viewModel.Status);
    }

    [Fact]
    public void Observed_Metrics_Update_Property_Changed()
    {
        var viewModel = new SessionViewModel();
        string? lastChanged = null;
        ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, eventArguments) => lastChanged = eventArguments.PropertyName;

        viewModel.ReportMetrics(new SessionMetrics(FramesPerSecond: 59.9, BitrateKilobitsPerSecond: 6500, ConnectedViewerEndpoint: "192.168.1.44:51001"));

        Assert.Equal(59.9, viewModel.FramesPerSecond);
        Assert.Equal(6500, viewModel.BitrateKilobitsPerSecond);
        Assert.Equal("192.168.1.44:51001", viewModel.ConnectedViewerEndpoint);
        Assert.Equal(nameof(SessionViewModel.ConnectedViewerEndpoint), lastChanged);
    }

    [Fact]
    public void Stop_Transitions_To_Idle()
    {
        var viewModel = new SessionViewModel();
        viewModel.ReportStatus(SessionStatus.Streaming);
        viewModel.ReportStatus(SessionStatus.Idle);
        Assert.Equal(SessionStatus.Idle, viewModel.Status);
    }
}
```

- [ ] **Step 2: Run tests**

Expected: FAIL — `SessionViewModel` / `SessionMetrics` / `SessionStatus` may not yet exist.

- [ ] **Step 3: Implement `SessionViewModel.cs`**

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WindowStream.Core.Session;

namespace WindowStream.Server.ViewModels;

public enum SessionStatus
{
    Idle,
    Streaming,
    Error
}

public sealed record SessionMetrics(double FramesPerSecond, int BitrateKilobitsPerSecond, string? ConnectedViewerEndpoint);

public sealed class SessionViewModel : INotifyPropertyChanged
{
    private SessionStatus status = SessionStatus.Idle;
    private double framesPerSecond;
    private int bitrateKilobitsPerSecond;
    private string? connectedViewerEndpoint;

    public SessionStatus Status
    {
        get => status;
        private set => SetField(ref status, value);
    }

    public double FramesPerSecond
    {
        get => framesPerSecond;
        private set => SetField(ref framesPerSecond, value);
    }

    public int BitrateKilobitsPerSecond
    {
        get => bitrateKilobitsPerSecond;
        private set => SetField(ref bitrateKilobitsPerSecond, value);
    }

    public string? ConnectedViewerEndpoint
    {
        get => connectedViewerEndpoint;
        private set => SetField(ref connectedViewerEndpoint, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ReportStatus(SessionStatus newStatus) => Status = newStatus;

    public void ReportMetrics(SessionMetrics metrics)
    {
        FramesPerSecond = metrics.FramesPerSecond;
        BitrateKilobitsPerSecond = metrics.BitrateKilobitsPerSecond;
        ConnectedViewerEndpoint = metrics.ConnectedViewerEndpoint;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
```

- [ ] **Step 4: Run tests**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WindowStreamServer/ViewModels/SessionViewModel.cs tests/WindowStream.Core.Tests/ViewModels/SessionViewModelTests.cs
git commit -m "feat(server): SessionViewModel exposes status + metrics with PropertyChanged"
```

### Task 41: Main page — list → tap → status pane with Stop

**Files:**
- Create: `src/WindowStreamServer/Pages/MainPage.xaml`
- Create: `src/WindowStreamServer/Pages/MainPage.xaml.cs`
- Create: `src/WindowStreamServer/AppShell.xaml` (route registration)

- [ ] **Step 1: Implement `MainPage.xaml` (layout binds to ViewModels)**

```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:viewModels="clr-namespace:WindowStream.Server.ViewModels"
             x:Class="WindowStream.Server.Pages.MainPage"
             x:DataType="viewModels:WindowPickerViewModel">
    <Grid ColumnDefinitions="*,2*" Padding="12" RowDefinitions="Auto,*">
        <Label Grid.ColumnSpan="2" Text="WindowStream" FontSize="28" />
        <CollectionView Grid.Row="1" Grid.Column="0" ItemsSource="{Binding Windows}" SelectionMode="Single" SelectionChangedCommand="{Binding StartStreamCommand}">
            <CollectionView.ItemTemplate>
                <DataTemplate>
                    <Grid Padding="8" ColumnDefinitions="Auto,*">
                        <Label Grid.Column="0" Text="{Binding ProcessName}" WidthRequest="120" />
                        <Label Grid.Column="1" Text="{Binding Title}" />
                    </Grid>
                </DataTemplate>
            </CollectionView.ItemTemplate>
        </CollectionView>
        <VerticalStackLayout Grid.Row="1" Grid.Column="1" Padding="12" Spacing="8" x:Name="SessionPane" BindingContext="{Binding Source={RelativeSource AncestorType={x:Type ContentPage}}, Path=SessionViewModel}">
            <Label Text="{Binding Status}" FontSize="20" />
            <Label Text="{Binding FramesPerSecond, StringFormat='FPS: {0:F1}'}" />
            <Label Text="{Binding BitrateKilobitsPerSecond, StringFormat='Bitrate: {0} kbps'}" />
            <Label Text="{Binding ConnectedViewerEndpoint, StringFormat='Viewer: {0}'}" />
            <Button Text="Stop" Clicked="OnStopClicked" />
        </VerticalStackLayout>
    </Grid>
</ContentPage>
```

- [ ] **Step 2: Implement `MainPage.xaml.cs`**

```csharp
using Microsoft.Maui.Controls;
using WindowStream.Server.ViewModels;

namespace WindowStream.Server.Pages;

public partial class MainPage : ContentPage
{
    public SessionViewModel SessionViewModel { get; }

    public MainPage(WindowPickerViewModel pickerViewModel, SessionViewModel sessionViewModel)
    {
        InitializeComponent();
        BindingContext = pickerViewModel;
        SessionViewModel = sessionViewModel;
    }

    private void OnStopClicked(object? sender, System.EventArgs eventArguments)
    {
        SessionViewModel.ReportStatus(SessionStatus.Idle);
    }
}
```

- [ ] **Step 3: Commit**

```bash
git add src/WindowStreamServer/Pages src/WindowStreamServer/AppShell.xaml src/WindowStreamServer/AppShell.xaml.cs
git commit -m "feat(server): main page picker + session status pane + Stop button"
```

## Phase 14: Coverage gate

### Task 42: `Directory.Build.props` enforces 100% coverage

**Files:**
- Create: `Directory.Build.props`
- Modify: every test `.csproj` to add `coverlet.msbuild` package reference
- Create: `test.cmd`
- Create: `test.sh`
- Modify: `CLAUDE.md`

- [ ] **Step 1: Write `Directory.Build.props` at repo root**

```xml
<Project>
  <PropertyGroup>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <PropertyGroup Condition="'$(IsTestProject)' == 'true'">
    <CollectCoverage>true</CollectCoverage>
    <CoverletOutputFormat>cobertura</CoverletOutputFormat>
    <CoverletOutput>$(MSBuildThisFileDirectory)artifacts/coverage/$(MSBuildProjectName)/</CoverletOutput>
    <Threshold>100</Threshold>
    <ThresholdType>line,branch</ThresholdType>
    <ThresholdStat>total</ThresholdStat>
    <ExcludeByFile>**/Generated/**/*.cs</ExcludeByFile>
  </PropertyGroup>
  <ItemGroup Condition="'$(IsTestProject)' == 'true'">
    <PackageReference Include="coverlet.msbuild" Version="6.0.2">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write `test.cmd`**

```bat
@echo off
setlocal
rmdir /s /q artifacts\coverage 2>nul
dotnet test WindowStream.sln -c Release
if errorlevel 1 exit /b %errorlevel%
dotnet tool restore
dotnet reportgenerator -reports:"artifacts\coverage\*\coverage.cobertura.xml" -targetdir:"artifacts\coverage\report" -reporttypes:Html
echo Coverage report: artifacts\coverage\report\index.html
endlocal
```

- [ ] **Step 3: Write `test.sh`**

```bash
#!/usr/bin/env bash
set -euo pipefail
rm -rf artifacts/coverage
dotnet test WindowStream.sln -c Release
dotnet tool restore
dotnet reportgenerator \
  -reports:"artifacts/coverage/*/coverage.cobertura.xml" \
  -targetdir:"artifacts/coverage/report" \
  -reporttypes:Html
echo "Coverage report: artifacts/coverage/report/index.html"
```

- [ ] **Step 4: Add `.config/dotnet-tools.json` for ReportGenerator**

```json
{
  "version": 1,
  "isRoot": true,
  "tools": {
    "dotnet-reportgenerator-globaltool": {
      "version": "5.3.11",
      "commands": [
        "reportgenerator"
      ]
    }
  }
}
```

- [ ] **Step 5: Document in `CLAUDE.md`**

Append:

```markdown
## Tests and coverage

- Run everything + generate HTML coverage report:
  - Windows: `test.cmd`
  - Unix: `./test.sh`
- Coverage gate is 100% line and 100% branch (see `Directory.Build.props`).
  Failing to meet that threshold breaks the build.
- Regenerate the HTML report by hand with:
  ```bash
  dotnet tool restore
  dotnet reportgenerator \
    -reports:"artifacts/coverage/*/coverage.cobertura.xml" \
    -targetdir:"artifacts/coverage/report" \
    -reporttypes:Html
  ```
  Open `artifacts/coverage/report/index.html` in a browser.
- Integration tests live in `tests/WindowStream.Integration.Tests`. Skip-gates:
  - `[DesktopSessionFact]` — set `WINDOWSTREAM_SKIP_DESKTOP=1` on headless CI.
  - `[NvidiaDriverFact]` — set `WINDOWSTREAM_SKIP_NVENC=1` on non-NVIDIA hosts.
```

- [ ] **Step 6: Verify gate fails when coverage drops**

Run: `dotnet test WindowStream.sln`
Expected: PASS if existing tests cover 100%. Temporarily add an uncovered method to confirm the build fails; revert before committing.

- [ ] **Step 7: Commit**

```bash
git add Directory.Build.props test.cmd test.sh .config/dotnet-tools.json CLAUDE.md
git commit -m "chore: enforce 100% line and branch coverage via Directory.Build.props"
```
