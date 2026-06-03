#if WINDOWS
using System.IO.Pipes;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Encode;
using WindowStream.Core.Hosting;

namespace WindowStream.Cli.Commands;

public static class WorkerCommandHandler
{
    public static async Task<int> ExecuteAsync(WorkerArguments arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                serverName: ".",
                pipeName: arguments.PipeName,
                direction: PipeDirection.InOut,
                options: PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);

            using var lifecycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pauseLock = new object();
            var paused = false;

            using var deviceManager = new Direct3D11DeviceManager();
            await using var encoder = new FFmpegNvencEncoder();
            encoder.Configure(arguments.EncoderOptions, deviceManager);

            var captureSource = new WgcCaptureSource();

            // lifecycle/encoder/pipe are deliberately shared across these Task.Run loops
            // and the main flow; both tasks are awaited (the WhenAll drain below) before the
            // enclosing using/await-using disposes them. The analyzer cannot prove the
            // bounded-shutdown drain completes first; per feedback_inspections_refactor_over_suppress.
            // ReSharper disable AccessToDisposedClosure
            var commandReaderTask = Task.Run(async () =>
            {
                try
                {
                    while (!lifecycle.Token.IsCancellationRequested)
                    {
                        var command = await WorkerChunkPipe.ReadCommandAsync(pipe, lifecycle.Token).ConfigureAwait(false);
                        switch (command.Tag)
                        {
                            case WorkerCommandTag.Pause:
                                lock (pauseLock) paused = true;
                                break;
                            case WorkerCommandTag.Resume:
                                lock (pauseLock) paused = false;
                                encoder.RequestKeyframe();
                                break;
                            case WorkerCommandTag.RequestKeyframe:
                                encoder.RequestKeyframe();
                                break;
                            case WorkerCommandTag.Shutdown:
                                await lifecycle.CancelAsync().ConfigureAwait(false);
                                return;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (EndOfStreamException) { await lifecycle.CancelAsync().ConfigureAwait(false); }
            }, lifecycle.Token);

            var encodeOutputTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var chunk in encoder.EncodedChunks.WithCancellation(lifecycle.Token).ConfigureAwait(false))
                    {
                        var frame = new WorkerChunkFrame(
                            PresentationTimestampMicroseconds: (ulong)chunk.PresentationTimestampMicroseconds,
                            IsKeyframe: chunk.IsKeyframe,
                            Payload: chunk.Payload.ToArray());
                        await WorkerChunkPipe.WriteChunkAsync(pipe, frame, lifecycle.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
            }, lifecycle.Token);
            // ReSharper restore AccessToDisposedClosure

            await using var capture = captureSource.Start(
                arguments.Hwnd,
                new CaptureOptions(TargetFramesPerSecond: arguments.EncoderOptions.FramesPerSecond, IncludeCursor: false),
                sharedDeviceManager: deviceManager,
                sharedFrameTexturePool: encoder,
                lifecycle.Token);

            try
            {
                await foreach (var captured in capture.Frames.WithCancellation(lifecycle.Token).ConfigureAwait(false))
                {
                    bool currentlyPaused;
                    lock (pauseLock) currentlyPaused = paused;
                    if (currentlyPaused)
                    {
                        // Return the acquired pool frame so the encoder doesn't leak
                        // its AVFrame across the pause window. Without this the
                        // pool slot stays held until the worker exits and a
                        // subsequent resume's EncodeAsync would fail to find a
                        // matching dictionary entry (Gitea #6).
                        encoder.ReleaseFrameTexture(captured.NativeTexturePointer, captured.TextureArrayIndex);
                        continue;
                    }
                    await encoder.EncodeAsync(captured, lifecycle.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
#pragma warning disable CA1031 // worker capture-failure boundary: logs the exception then shuts down the worker process
            catch (Exception captureException)
            {
                await Console.Error.WriteLineAsync($"[worker] capture failed: {captureException}").ConfigureAwait(false);
                await lifecycle.CancelAsync().ConfigureAwait(false);
                // CancellationToken.None intentional: token is already cancelled; we want the 2-second wall-clock timeout regardless
                try { await Task.WhenAll(commandReaderTask, encodeOutputTask).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false); } catch { /* graceful-shutdown drain; tasks already cancelled */ }
                return 2;
            }
#pragma warning restore CA1031

            await lifecycle.CancelAsync().ConfigureAwait(false);
            // CancellationToken.None intentional: token is already cancelled; we want the 2-second wall-clock timeout regardless
#pragma warning disable CA1031, RCS1075 // graceful-shutdown drain; tasks already cancelled, any residual exception is ignorable
            try { await Task.WhenAll(commandReaderTask, encodeOutputTask).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false); } catch { /* graceful-shutdown drain; tasks already cancelled */ }
#pragma warning restore CA1031, RCS1075
            return 0;
        }
#pragma warning disable CA1031 // top-level worker guard: logs unexpected fault and returns non-zero exit code
        catch (Exception unexpected)
        {
            await Console.Error.WriteLineAsync($"[worker] unexpected: {unexpected}").ConfigureAwait(false);
            return 3;
        }
#pragma warning restore CA1031
    }
}
#endif
