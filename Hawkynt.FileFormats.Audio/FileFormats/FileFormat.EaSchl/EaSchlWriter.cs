#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.EaXa;

namespace FileFormat.EaSchl;

/// <summary>
/// Writes a minimal SCHl / SCDl / SCEl stream that round-trips through
/// <see cref="EaSchlReader"/>. The output is deliberately simplified relative to real EA
/// files:
/// <list type="bullet">
///   <item>the SCHl header carries a compact PT (patch table) with just the channels,
///         sample-rate, total-sample and compression fields this reader understands;</item>
///   <item>all audio is emitted in a single SCDl block (real EA files chunk audio into many
///         interleaved-but-bounded blocks);</item>
///   <item>only the EA-XA compression type is produced.</item>
/// </list>
/// </summary>
public static class EaSchlWriter {

  private const byte PtMarker = 0xFD;
  private const byte PtEnd = 0x8A;

  /// <summary>Builds a SCHl stream from interleaved 16-bit PCM, encoding the audio with EA-XA.</summary>
  public static byte[] Write(ReadOnlySpan<short> interleaved, int channels, int sampleRate) {
    if (channels < 1)
      throw new ArgumentException("EA SCHl needs at least one channel.", nameof(channels));
    if (interleaved.Length % channels != 0)
      throw new ArgumentException("Interleaved sample count must be a multiple of the channel count.", nameof(interleaved));

    var coded = EaXaCodec.Encode(interleaved, channels);
    var totalSamples = interleaved.Length / channels;

    using var ms = new MemoryStream();

    // ── SCHl header block with an embedded PT ──
    var pt = BuildPatchTable(channels, sampleRate, totalSamples, EaSchlReader.CompressionEaXa);
    WriteBlock(ms, "SCHl"u8, pt);

    // ── SCDl data block: u32 LE sample count + coded audio ──
    var data = new byte[4 + coded.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)totalSamples);
    coded.CopyTo(data, 4);
    WriteBlock(ms, "SCDl"u8, data);

    // ── SCEl end block (empty body) ──
    WriteBlock(ms, "SCEl"u8, ReadOnlySpan<byte>.Empty);

    return ms.ToArray();
  }

  private static byte[] BuildPatchTable(int channels, int sampleRate, long totalSamples, int compression) {
    using var ms = new MemoryStream();
    ms.WriteByte(PtMarker);
    WriteTlv(ms, 0x82, channels);
    WriteTlv(ms, 0x84, sampleRate);
    WriteTlv(ms, 0x85, totalSamples);
    WriteTlv(ms, 0xA0, compression);
    ms.WriteByte(PtEnd);
    return ms.ToArray();
  }

  private static void WriteTlv(Stream s, byte code, long value) {
    Span<byte> be = stackalloc byte[8];
    BinaryPrimitives.WriteInt64BigEndian(be, value);
    // Trim leading zero bytes but keep at least one byte.
    var first = 0;
    while (first < 7 && be[first] == 0) ++first;
    var len = 8 - first;
    s.WriteByte(code);
    s.WriteByte((byte)len);
    s.Write(be[first..]);
  }

  private static void WriteBlock(Stream s, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> body) {
    Span<byte> header = stackalloc byte[8];
    tag.CopyTo(header);
    BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(8 + body.Length));
    s.Write(header);
    s.Write(body);
  }
}
