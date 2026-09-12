#pragma warning disable CS1591

using System.Buffers.Binary;
using System.Text;

namespace Codec.Speex;

/// <summary>
/// Parsed Speex identification header (the first Ogg packet of a Speex stream, also
/// usable as raw extradata). Layout mirrors <c>parse_speex_extradata</c> in FFmpeg's
/// <c>speexdec.c</c>: the eight-byte magic <c>"Speex   "</c>, a 20-byte version
/// string, then little-endian 32-bit fields.
/// </summary>
public sealed record SpeexHeader(
  int VersionId,
  int Rate,
  int Mode,
  int BitstreamVersion,
  int NbChannels,
  int Bitrate,
  int FrameSize,
  int Vbr,
  int FramesPerPacket,
  int ExtraHeaders) {

  /// <summary>The eight-byte identification magic.</summary>
  public static ReadOnlySpan<byte> Magic => "Speex   "u8;

  internal const int NbFrameSize = 160;

  /// <summary>
  /// Parses a Speex header from <paramref name="packet"/>. The magic may sit at the
  /// start (the usual Ogg case) or be located within the buffer (FFmpeg's
  /// <c>av_strnstr</c> behaviour for extradata).
  /// </summary>
  /// <exception cref="InvalidDataException">Magic missing, fields out of range, or
  /// the bitstream version is not the supported value (4).</exception>
  public static SpeexHeader Parse(ReadOnlySpan<byte> packet) {
    var off = IndexOf(packet, Magic);
    if (off < 0)
      throw new InvalidDataException("Speex header magic 'Speex   ' not found.");

    // Skip the 8-byte magic + 20-byte version string (28 bytes total).
    var p = off + 28;
    if (p + 40 > packet.Length)
      throw new InvalidDataException("Speex header truncated.");

    var versionId = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(p, 4)); p += 4;
    p += 4;                     // header_size — unused, exactly as the reference skips it
    var rate = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(p, 4)); p += 4;
    if (rate <= 0)
      throw new InvalidDataException("Speex header: non-positive sample rate.");
    var mode = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(p, 4)); p += 4;
    if (mode < 0 || mode >= 3)
      throw new InvalidDataException($"Speex header: unsupported mode {mode}.");
    var bitstreamVersion = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(p, 4)); p += 4;
    if (bitstreamVersion != 4)
      throw new InvalidDataException($"Speex header: unsupported bitstream version {bitstreamVersion}.");
    var nbChannels = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(p, 4)); p += 4;
    if (nbChannels is <= 0 or > 2)
      throw new InvalidDataException($"Speex header: unsupported channel count {nbChannels}.");
    var bitrate = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(p, 4)); p += 4;
    var frameSizeRaw = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(p, 4)); p += 4;
    // Mirror the reference clamp: frame_size << (mode>1) then min with 160<<mode.
    var shift = mode > 1 ? 1 : 0;
    if (frameSizeRaw < NbFrameSize << shift || frameSizeRaw > int.MaxValue >> shift)
      throw new InvalidDataException("Speex header: invalid frame size.");
    var frameSize = Math.Min(frameSizeRaw << shift, NbFrameSize << mode);
    var vbr = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(p, 4)); p += 4;
    var framesPerPacket = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(p, 4)); p += 4;
    if (framesPerPacket <= 0 || framesPerPacket > 64)
      throw new InvalidDataException($"Speex header: invalid frames-per-packet {framesPerPacket}.");
    var extraHeaders = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(p, 4)); p += 4;

    return new SpeexHeader(versionId, rate, mode, bitstreamVersion, nbChannels,
      bitrate, frameSize, vbr, framesPerPacket, extraHeaders);
  }

  /// <summary>The version string carried in bytes 8..28 (best-effort ASCII).</summary>
  public static string ReadVersionString(ReadOnlySpan<byte> packet) {
    var off = IndexOf(packet, Magic);
    if (off < 0 || off + 28 > packet.Length)
      return string.Empty;
    var slice = packet.Slice(off + 8, 20);
    var nul = slice.IndexOf((byte)0);
    if (nul >= 0) slice = slice[..nul];
    return Encoding.ASCII.GetString(slice).Trim();
  }

  private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) {
    for (var i = 0; i + needle.Length <= haystack.Length; ++i)
      if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
        return i;
    return -1;
  }
}
