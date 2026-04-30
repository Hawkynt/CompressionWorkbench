#pragma warning disable CS1591
using FileFormat.Core;
using FileFormat.Jpeg;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// <see cref="IFrameSource"/> for plain JPEG files. Caches the source bytes
/// once on construction (so a subsequent pixel decode doesn't have to re-read
/// the stream — it might already be at EOF) but does NOT decode pixels until
/// <see cref="GetFrame"/> is called.
/// </summary>
/// <remarks>
/// <para>
/// <b>List path</b> — <c>MultiImageArchiveHelper.List</c> calls only
/// <see cref="FrameCount"/> and <see cref="GetMetadata"/>. The metadata call
/// runs <see cref="JpegMetadataScanner.Scan"/> against an in-memory copy of
/// the source bytes, which reads at most <see cref="JpegMetadataScanner.ScanLimit"/>
/// bytes and never invokes the libjpeg decoder. A 10 MB JPEG lists in &lt;10 ms.
/// </para>
/// <para>
/// <b>Extract path</b> — <see cref="GetFrame"/> calls
/// <see cref="JpegReader.FromBytes"/> exactly once, caches the result, and
/// reuses it across multiple component requests inside the same
/// <c>Extract()</c> call. Distinct <c>Extract()</c> invocations are
/// independent (the source instance is short-lived inside the helper).
/// </para>
/// <para>
/// We snapshot bytes up-front because the underlying
/// <see cref="JpegReader.FromStream"/> consumes the stream destructively
/// when seekable (<c>stream.ReadExactly(...)</c>), and the helper passes a
/// caller-owned stream we cannot rewind.
/// </para>
/// </remarks>
public sealed class JpegFrameSource : IFrameSource {

  private readonly byte[] _bytes;
  private RawImage? _cachedFrame;

  public JpegFrameSource(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) {
      var len = checked((int)(stream.Length - stream.Position));
      _bytes = new byte[len];
      stream.ReadExactly(_bytes);
    } else {
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      _bytes = ms.ToArray();
    }
  }

  public int FrameCount => 1;

  public FrameMetadata GetMetadata(int frameIndex) {
    if (frameIndex != 0) return new FrameMetadata(0, 0, 24, false);
    return JpegMetadataScanner.ScanBytes(_bytes);
  }

  public RawImage GetFrame(int frameIndex) {
    if (frameIndex != 0)
      throw new ArgumentOutOfRangeException(nameof(frameIndex), "JPEG has exactly one frame.");
    if (_cachedFrame is { } cached) return cached;
    var jpeg = JpegReader.FromBytes(_bytes);
    _cachedFrame = JpegFile.ToRawImage(jpeg);
    return _cachedFrame;
  }
}
