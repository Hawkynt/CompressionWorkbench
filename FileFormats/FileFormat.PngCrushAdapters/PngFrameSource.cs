#pragma warning disable CS1591
using FileFormat.Core;
using FileFormat.Png;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// <see cref="IFrameSource"/> for plain PNG files. Snapshots source bytes once
/// (so a later pixel decode doesn't need a rewindable stream) and parses the
/// IHDR chunk inline — `GetMetadata` never invokes the full PNG reader.
/// </summary>
/// <remarks>
/// PNG signature is 8 bytes; IHDR is the next chunk and is fixed-layout:
/// <c>4 length | 4 "IHDR" | 13 data | 4 CRC</c>. The 13-byte data block is
/// width(BE32) + height(BE32) + bit_depth + color_type + compression + filter +
/// interlace. We read width/height/bit_depth/color_type and skip the rest, so a
/// 200 MB PNG lists in microseconds.
/// </remarks>
public sealed class PngFrameSource : IFrameSource {

  private readonly byte[] _bytes;
  private RawImage? _cachedFrame;

  public PngFrameSource(Stream stream) {
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
    // Need at least 8 (signature) + 4 (length) + 4 (type) + 13 (IHDR data) = 29 bytes.
    if (_bytes.Length < 29) return new FrameMetadata(0, 0, 24, false);
    if (_bytes[12] != (byte)'I' || _bytes[13] != (byte)'H' || _bytes[14] != (byte)'D' || _bytes[15] != (byte)'R')
      return new FrameMetadata(0, 0, 24, false);
    var width = (_bytes[16] << 24) | (_bytes[17] << 16) | (_bytes[18] << 8) | _bytes[19];
    var height = (_bytes[20] << 24) | (_bytes[21] << 16) | (_bytes[22] << 8) | _bytes[23];
    var bitDepth = _bytes[24];
    var colorType = _bytes[25];
    // Color type bits: 1 = palette, 2 = color, 4 = alpha. Standard combos: 0,2,3,4,6.
    var hasAlpha = colorType == 4 || colorType == 6;
    var bpp = colorType switch {
      0 => (int)bitDepth,
      2 => bitDepth * 3,
      3 => (int)bitDepth,
      4 => bitDepth * 2,
      6 => bitDepth * 4,
      _ => 24,
    };
    return new FrameMetadata(width, height, bpp, hasAlpha);
  }

  public RawImage GetFrame(int frameIndex) {
    if (frameIndex != 0)
      throw new ArgumentOutOfRangeException(nameof(frameIndex), "PNG has exactly one frame.");
    if (_cachedFrame is { } cached) return cached;
    var png = PngReader.FromBytes(_bytes);
    _cachedFrame = PngFile.ToRawImage(png);
    return _cachedFrame;
  }
}
