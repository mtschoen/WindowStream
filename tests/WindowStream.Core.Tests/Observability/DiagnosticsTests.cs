using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Moq;
using WindowStream.Core.Observability;
using Xunit;

namespace WindowStream.Core.Tests.Observability;

public class DiagnosticsTests
{
    [Fact]
    public void Report_Translates_Listening_To_Info_Log_With_EventType_Property()
    {
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(logger => logger.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        Diagnostics diagnostics = new(loggerMock.Object);
        diagnostics.Report(new PipelineEvent.Listening(TcpPort: 53234, UdpPort: 53235));

        // Moq verification lambda — state.ToString() is the test assertion, not a hot log path.
#pragma warning disable CA1873 // not a production log call; argument evaluation is intentional in test
        loggerMock.Verify(logger => logger.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("Listening")),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
#pragma warning restore CA1873
    }

    [Fact]
    public void Report_Translates_WorkerSpawnFailed_To_Error_Log_With_Exception()
    {
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(logger => logger.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        InvalidOperationException boom = new("boom");

        Diagnostics diagnostics = new(loggerMock.Object);
        diagnostics.Report(new PipelineEvent.WorkerSpawnFailed(StreamId: 7, Exception: boom));

        loggerMock.Verify(logger => logger.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            boom,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Report_Translates_StreamRefused_To_Warning_Log()
    {
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(logger => logger.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        Diagnostics diagnostics = new(loggerMock.Object);
        diagnostics.Report(new PipelineEvent.StreamRefused(StreamId: 1, ErrorCode: "WGC_FAIL", Message: "boom"));

        loggerMock.Verify(logger => logger.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Report_Null_Event_Throws_ArgumentNullException()
    {
        Diagnostics diagnostics = new(Mock.Of<ILogger>());
        Assert.Throws<ArgumentNullException>(() => diagnostics.Report(null!));
    }

    [Fact]
    public void Constructor_Null_Logger_Throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new Diagnostics(null!));
    }

    [Fact]
    public void Subscribed_Handler_Receives_Event_After_Report()
    {
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(logger => logger.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        Diagnostics diagnostics = new(loggerMock.Object);

        PipelineEvent? received = null;
        diagnostics.Subscribe(evt => received = evt);

        PipelineEvent.Listening expected = new(53234, 53235);
        diagnostics.Report(expected);

        Assert.Same(expected, received);
    }

    [Fact]
    public void Report_Translates_CaptureFailed_To_Error_Log_With_Exception()
    {
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(logger => logger.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        InvalidOperationException boom = new("capture fail");

        Diagnostics diagnostics = new(loggerMock.Object);
        diagnostics.Report(new PipelineEvent.CaptureFailed(StreamId: 2, Exception: boom));

        loggerMock.Verify(logger => logger.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            boom,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Report_Translates_EncodeFailed_To_Error_Log_With_Exception()
    {
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(logger => logger.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        InvalidOperationException boom = new("encode fail");

        Diagnostics diagnostics = new(loggerMock.Object);
        diagnostics.Report(new PipelineEvent.EncodeFailed(StreamId: 3, Exception: boom));

        loggerMock.Verify(logger => logger.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            boom,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Report_Translates_ProbeFailed_To_Error_Log_With_Exception()
    {
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(logger => logger.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        InvalidOperationException boom = new("probe fail");

        Diagnostics diagnostics = new(loggerMock.Object);
        diagnostics.Report(new PipelineEvent.ProbeFailed(WindowId: 1UL, Hwnd: 12345L, Exception: boom));

        loggerMock.Verify(logger => logger.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            boom,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Report_Translates_EnumerationFailed_To_Warning_Log_With_Exception()
    {
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(logger => logger.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        InvalidOperationException boom = new("enum fail");

        Diagnostics diagnostics = new(loggerMock.Object);
        diagnostics.Report(new PipelineEvent.EnumerationFailed(Exception: boom));

        loggerMock.Verify(logger => logger.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            boom,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
