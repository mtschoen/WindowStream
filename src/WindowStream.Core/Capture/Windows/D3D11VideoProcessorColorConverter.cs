#if WINDOWS
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Silk.NET.Core;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace WindowStream.Core.Capture.Windows;

/// <summary>
/// Wraps the D3D11 video-processor pipeline for fixed-function BGRA → NV12
/// colour conversion entirely on the GPU using <c>VideoProcessorBlt</c>.
/// The processor, enumerator, video device, and video context are created once
/// in the constructor and reused across frames. Per-call
/// <c>ID3D11VideoProcessorInputView</c> and
/// <c>ID3D11VideoProcessorOutputView</c> are created and released inside
/// <see cref="Convert"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Convert"/> is intended to be called synchronously inside the WGC
/// <c>FrameArrived</c> callback so the source BGRA texture is consumed before
/// the WGC frame is disposed. Do not call <see cref="Convert"/> from a
/// different thread without coordinating frame lifetime externally.
/// </para>
/// </remarks>
[ExcludeFromCodeCoverage(Justification = "Native COM video-processor pipeline — covered by integration tests.")]
public sealed unsafe class D3D11VideoProcessorColorConverter : IDisposable
{
    // IID for ID3D11VideoDevice: {10EC4D5B-975A-4689-B9E4-D0AAC30FE333}
    private static readonly Guid iidId3D11VideoDevice =
        new Guid("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");

    // IID for ID3D11VideoContext: {C7262BC3-91BA-4D11-81F7-6A37AB8B3AB1}
    private static readonly Guid iidId3D11VideoContext =
        new Guid("C7262BC3-91BA-4D11-81F7-6A37AB8B3AB1");

    private bool disposed;

    private readonly int sourceWidth;
    private readonly int sourceHeight;

    // Acquired in constructor; released in Dispose in reverse-acquisition order.
    private ID3D11VideoDevice* videoDevice;
    private ID3D11VideoContext* videoContext;
    private ID3D11VideoProcessorEnumerator* videoProcessorEnumerator;
    private ID3D11VideoProcessor* videoProcessor;

    /// <summary>
    /// Initialises the D3D11 video-processor pipeline for BGRA → NV12 conversion
    /// at the supplied source dimensions.
    /// </summary>
    /// <param name="deviceManager">
    /// The shared D3D11 device. Must have been created with
    /// <c>D3D11_CREATE_DEVICE_VIDEO_SUPPORT</c>.
    /// </param>
    /// <param name="width">Source (and destination) width in pixels.</param>
    /// <param name="height">Source (and destination) height in pixels.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="deviceManager"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="width"/> or <paramref name="height"/> is less than 1.
    /// </exception>
    /// <exception cref="WindowCaptureException">
    /// A D3D11 API call returned a failing HRESULT.
    /// </exception>
    public D3D11VideoProcessorColorConverter(
        Direct3D11DeviceManager deviceManager,
        int width,
        int height)
    {
        if (deviceManager is null)
        {
            throw new ArgumentNullException(nameof(deviceManager));
        }
        if (width < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width,
                "Width must be at least 1.");
        }
        if (height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height,
                "Height must be at least 1.");
        }

        sourceWidth = width;
        sourceHeight = height;

        // QueryInterface ID3D11Device → ID3D11VideoDevice
        {
            ID3D11Device* device = (ID3D11Device*)deviceManager.NativeDevicePointer;
            ID3D11VideoDevice* acquiredVideoDevice = null;
            Guid iid = iidId3D11VideoDevice;
            int result = device->QueryInterface(ref iid, (void**)&acquiredVideoDevice);
            if (result < 0)
            {
                throw new WindowCaptureException(
                    "QueryInterface(ID3D11VideoDevice) failed. HRESULT: 0x"
                    + ((uint)result).ToString("X8", CultureInfo.InvariantCulture));
            }
            videoDevice = acquiredVideoDevice;
        }

        // QueryInterface ID3D11DeviceContext → ID3D11VideoContext
        try
        {
            ID3D11DeviceContext* context = (ID3D11DeviceContext*)deviceManager.NativeContextPointer;
            ID3D11VideoContext* acquiredVideoContext = null;
            Guid iid = iidId3D11VideoContext;
            int result = context->QueryInterface(ref iid, (void**)&acquiredVideoContext);
            if (result < 0)
            {
                throw new WindowCaptureException(
                    "QueryInterface(ID3D11VideoContext) failed. HRESULT: 0x"
                    + ((uint)result).ToString("X8", CultureInfo.InvariantCulture));
            }
            videoContext = acquiredVideoContext;
        }
        catch
        {
            videoDevice->Release();
            videoDevice = null;
            throw;
        }

        // CreateVideoProcessorEnumerator
        try
        {
            VideoProcessorContentDesc contentDescription = new VideoProcessorContentDesc
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational { Numerator = 60, Denominator = 1 },
                InputWidth = (uint)width,
                InputHeight = (uint)height,
                OutputFrameRate = new Rational { Numerator = 60, Denominator = 1 },
                OutputWidth = (uint)width,
                OutputHeight = (uint)height,
                Usage = VideoUsage.PlaybackNormal,
            };

            ID3D11VideoProcessorEnumerator* acquiredEnumerator = null;
            int result = videoDevice->CreateVideoProcessorEnumerator(
                ref contentDescription,
                ref acquiredEnumerator);
            if (result < 0)
            {
                throw new WindowCaptureException(
                    "CreateVideoProcessorEnumerator failed. HRESULT: 0x"
                    + ((uint)result).ToString("X8", CultureInfo.InvariantCulture));
            }
            videoProcessorEnumerator = acquiredEnumerator;
        }
        catch
        {
            videoContext->Release();
            videoContext = null;
            videoDevice->Release();
            videoDevice = null;
            throw;
        }

        // CreateVideoProcessor (rate-conversion index 0 = identity / progressive)
        try
        {
            ID3D11VideoProcessor* acquiredProcessor = null;
            int result = videoDevice->CreateVideoProcessor(
                videoProcessorEnumerator,
                RateConversionIndex: 0,
                ref acquiredProcessor);
            if (result < 0)
            {
                throw new WindowCaptureException(
                    "CreateVideoProcessor failed. HRESULT: 0x"
                    + ((uint)result).ToString("X8", CultureInfo.InvariantCulture));
            }
            videoProcessor = acquiredProcessor;
        }
        catch
        {
            videoProcessorEnumerator->Release();
            videoProcessorEnumerator = null;
            videoContext->Release();
            videoContext = null;
            videoDevice->Release();
            videoDevice = null;
            throw;
        }
    }

    /// <summary>
    /// Converts a BGRA source texture to an NV12 destination texture on the GPU
    /// using <c>VideoProcessorBlt</c>.
    /// </summary>
    /// <param name="sourceBgraPointer">
    /// Raw COM pointer to an <c>ID3D11Texture2D</c> with format
    /// <c>DXGI_FORMAT_B8G8R8A8_UNORM</c>. Must be non-zero.
    /// </param>
    /// <param name="destinationNv12Pointer">
    /// Raw COM pointer to an <c>ID3D11Texture2D</c> with format
    /// <c>DXGI_FORMAT_NV12</c>. Must be non-zero.
    /// </param>
    /// <param name="arrayIndex">
    /// Array slice index within the destination texture array to write into.
    /// Must be non-negative.
    /// </param>
    /// <exception cref="ObjectDisposedException">
    /// This instance has been disposed.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sourceBgraPointer"/> or
    /// <paramref name="destinationNv12Pointer"/> is zero, or
    /// <paramref name="arrayIndex"/> is negative.
    /// </exception>
    /// <exception cref="WindowCaptureException">
    /// A D3D11 API call returned a failing HRESULT.
    /// </exception>
    public void Convert(nint sourceBgraPointer, nint destinationNv12Pointer, int arrayIndex)
    {
        ThrowIfDisposed();

        if (sourceBgraPointer == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceBgraPointer),
                "Source BGRA texture pointer must be non-zero.");
        }
        if (destinationNv12Pointer == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationNv12Pointer),
                "Destination NV12 texture pointer must be non-zero.");
        }
        if (arrayIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(arrayIndex), arrayIndex,
                "Array index must be non-negative.");
        }

        ID3D11Texture2D* sourceTexture = (ID3D11Texture2D*)sourceBgraPointer;
        ID3D11Texture2D* destinationTexture = (ID3D11Texture2D*)destinationNv12Pointer;

        ID3D11VideoProcessorInputView* inputView = null;
        ID3D11VideoProcessorOutputView* outputView = null;

        try
        {
            // Create input view for the BGRA source (FourCC = 0 → use format from texture)
            VideoProcessorInputViewDesc inputViewDescription = new VideoProcessorInputViewDesc
            {
                FourCC = 0,
                ViewDimension = VpivDimension.Texture2D,
                Anonymous = new VideoProcessorInputViewDescUnion
                {
                    Texture2D = new Tex2DVpiv { MipSlice = 0, ArraySlice = 0 },
                },
            };

            {
                ID3D11VideoProcessorInputView* acquiredInputView = null;
                int result = videoDevice->CreateVideoProcessorInputView(
                    (ID3D11Resource*)sourceTexture,
                    videoProcessorEnumerator,
                    ref inputViewDescription,
                    ref acquiredInputView);
                if (result < 0)
                {
                    throw new WindowCaptureException(
                        "CreateVideoProcessorInputView failed. HRESULT: 0x"
                        + ((uint)result).ToString("X8", CultureInfo.InvariantCulture));
                }
                inputView = acquiredInputView;
            }

            // Create output view for the NV12 destination (array slice = arrayIndex)
            VideoProcessorOutputViewDesc outputViewDescription = new VideoProcessorOutputViewDesc
            {
                ViewDimension = VpovDimension.Texture2Darray,
                Anonymous = new VideoProcessorOutputViewDescUnion
                {
                    Texture2DArray = new Tex2DArrayVpov
                    {
                        MipSlice = 0,
                        FirstArraySlice = (uint)arrayIndex,
                        ArraySize = 1,
                    },
                },
            };

            {
                ID3D11VideoProcessorOutputView* acquiredOutputView = null;
                int result = videoDevice->CreateVideoProcessorOutputView(
                    (ID3D11Resource*)destinationTexture,
                    videoProcessorEnumerator,
                    ref outputViewDescription,
                    ref acquiredOutputView);
                if (result < 0)
                {
                    throw new WindowCaptureException(
                        "CreateVideoProcessorOutputView failed. HRESULT: 0x"
                        + ((uint)result).ToString("X8", CultureInfo.InvariantCulture));
                }
                outputView = acquiredOutputView;
            }

            // Configure the stream: progressive, single frame, no past/future frames
            VideoProcessorStream stream = new VideoProcessorStream
            {
                Enable = new Silk.NET.Core.Bool32(true),
                OutputIndex = 0,
                InputFrameOrField = 0,
                PastFrames = 0,
                FutureFrames = 0,
                PpPastSurfaces = null,
                PInputSurface = inputView,
                PpFutureSurfaces = null,
                PpPastSurfacesRight = null,
                PInputSurfaceRight = null,
                PpFutureSurfacesRight = null,
            };

            int bltResult = videoContext->VideoProcessorBlt(
                videoProcessor,
                outputView,
                OutputFrame: 0,
                StreamCount: 1,
                &stream);

            if (bltResult < 0)
            {
                throw new WindowCaptureException(
                    "VideoProcessorBlt failed. HRESULT: 0x"
                    + ((uint)bltResult).ToString("X8", CultureInfo.InvariantCulture));
            }

            Console.Error.WriteLine($"[FRAMECOUNT] stage=convert");
        }
        finally
        {
            if (outputView != null)
            {
                outputView->Release();
            }
            if (inputView != null)
            {
                inputView->Release();
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        // Release in reverse-acquisition order: processor → enumerator → videoContext → videoDevice
        if (videoProcessor != null)
        {
            videoProcessor->Release();
            videoProcessor = null;
        }
        if (videoProcessorEnumerator != null)
        {
            videoProcessorEnumerator->Release();
            videoProcessorEnumerator = null;
        }
        if (videoContext != null)
        {
            videoContext->Release();
            videoContext = null;
        }
        if (videoDevice != null)
        {
            videoDevice->Release();
            videoDevice = null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(D3D11VideoProcessorColorConverter));
        }
    }
}
#endif
