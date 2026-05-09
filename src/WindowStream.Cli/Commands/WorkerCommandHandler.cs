#if WINDOWS
using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Encode;
using WindowStream.Core.Hosting;

namespace WindowStream.Cli.Commands;

public sealed class WorkerCommandHandler
{
    public async Task<int> ExecuteAsync(WorkerArguments arguments, CancellationToken cancellationToken)
    {
        try
        {
            using NamedPipeClientStream pipe = new NamedPipeClientStream(
                serverName: ".",
                pipeName: arguments.PipeName,
                direction: PipeDirection.InOut,
                options: PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);

            using CancellationTokenSource lifecycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            object pauseLock = new object();
            bool paused = false;

            using Direct3D11DeviceManager deviceManager = new Direct3D11DeviceManager();
            await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder();
            encoder.Configure(arguments.EncoderOptions, deviceManager);

            WgcCaptureSource captureSource = new WgcCaptureSource();

            Task commandReaderTask = Task.Run(async () =>
            {
                try
                {
                    while (!lifecycle.Token.IsCancellationRequested)
                    {
                        WorkerCommandFrame command = await WorkerChunkPipe.ReadCommandAsync(pipe, lifecycle.Token).ConfigureAwait(false);
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
                                lifecycle.Cancel();
                                return;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (EndOfStreamException) { lifecycle.Cancel(); }
            }, lifecycle.Token);

            Task encodeOutputTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (EncodedChunk chunk in encoder.EncodedChunks.WithCancellation(lifecycle.Token).ConfigureAwait(false))
                    {
                        WorkerChunkFrame frame = new WorkerChunkFrame(
                            PresentationTimestampMicroseconds: (ulong)chunk.presentationTimestampMicroseconds,
                            IsKeyframe: chunk.isKeyframe,
                            Payload: chunk.payload.ToArray());
                        await WorkerChunkPipe.WriteChunkAsync(pipe, frame, lifecycle.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
            }, lifecycle.Token);

            await using IWindowCapture capture = captureSource.Start(
                arguments.Hwnd,
                new CaptureOptions(targetFramesPerSecond: arguments.EncoderOptions.framesPerSecond, includeCursor: false),
                sharedDeviceManager: deviceManager,
                sharedFrameTexturePool: encoder,
                lifecycle.Token);

            try
            {
                await foreach (CapturedFrame captured in capture.Frames.WithCancellation(lifecycle.Token).ConfigureAwait(false))
                {
                    bool currentlyPaused;
                    lock (pauseLock) currentlyPaused = paused;
                    if (currentlyPaused) continue;
                    await encoder.EncodeAsync(captured, lifecycle.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception captureException)
            {
                Console.Error.WriteLine($"[worker] capture failed: {captureException}");
                lifecycle.Cancel();
                try { await Task.WhenAll(commandReaderTask, encodeOutputTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                return 2;
            }

            lifecycle.Cancel();
            try { await Task.WhenAll(commandReaderTask, encodeOutputTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            return 0;
        }
        catch (Exception unexpected)
        {
            Console.Error.WriteLine($"[worker] unexpected: {unexpected}");
            return 3;
        }
    }
}
#endif
