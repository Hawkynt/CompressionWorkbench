#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Wave64;

/// <summary>
/// Sony Wave64 (.w64) header + per-channel PCM extraction. Wave64 is structurally a
/// RIFF/WAVE file but uses 16-byte GUID chunk identifiers and 64-bit little-endian
/// sizes (so it can exceed the 4 GiB RIFF ceiling).
/// <para>The on-disk layout is
/// <c>&lt;riff-guid&gt; &lt;int64 fileSize&gt; &lt;wave-guid&gt;</c> followed by chunks,
/// each <c>&lt;chunk-guid&gt; &lt;int64 chunkSize&gt; &lt;body&gt; &lt;pad to 8&gt;</c>.
/// The <c>chunkSize</c> field counts the 16-byte guid and the 8-byte size field itself,
/// so the body length is <c>chunkSize - 24</c>. Padding zero-fills the chunk up to an
/// 8-byte boundary and is not counted in <c>chunkSize</c>.</para>
/// <para>The <c>fmt</c> body is a standard <c>WAVEFORMATEX</c> (identical to WAV), and
/// the <c>data</c> body is interleaved little-endian PCM. Reads only the <c>fmt</c> and
/// <c>data</c> chunks; other chunks remain addressable via
/// <see cref="ParsedWave64.OtherChunks"/> keyed by their 16-byte GUID.</para>
/// </summary>
public sealed class Wave64Reader {

  /// <summary>The standard Wave64 GUIDs. The first four bytes are the ASCII 4CC in
  /// little-endian; the trailing 12 bytes are the fixed Wave64 tail (the riff guid uses
  /// a distinct tail).</summary>
  public static readonly byte[] RiffGuid = [0x72, 0x69, 0x66, 0x66, 0x2E, 0x91, 0xCF, 0x11, 0xA5, 0xD6, 0x28, 0xDB, 0x04, 0xC1, 0x00, 0x00];
  /// <summary>
  /// Provides the wave guid value.
  /// </summary>
public static readonly byte[] WaveGuid = [0x77, 0x61, 0x76, 0x65, 0xF3, 0xAC, 0xD3, 0x11, 0x8C, 0xD1, 0x00, 0xC0, 0x4F, 0x8E, 0xDB, 0x8A];
  /// <summary>
  /// Provides the fmt guid value.
  /// </summary>
public static readonly byte[] FmtGuid = [0x66, 0x6D, 0x74, 0x20, 0xF3, 0xAC, 0xD3, 0x11, 0x8C, 0xD1, 0x00, 0xC0, 0x4F, 0x8E, 0xDB, 0x8A];
  /// <summary>
  /// Provides the data guid value.
  /// </summary>
public static readonly byte[] DataGuid = [0x64, 0x61, 0x74, 0x61, 0xF3, 0xAC, 0xD3, 0x11, 0x8C, 0xD1, 0x00, 0xC0, 0x4F, 0x8E, 0xDB, 0x8A];

  /// <summary>
  /// Represents a parsed wave 64.
  /// </summary>
public sealed record ParsedWave64(
    int NumChannels,
    int SampleRate,
    int BitsPerSample,
    int FormatCode,
    byte[] InterleavedPcm,
    IReadOnlyList<(byte[] Guid, byte[] Data)> OtherChunks,
    uint? ChannelMask = null);

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
public ParsedWave64 Read(ReadOnlySpan<byte> data) {
    // riff guid (16) + fileSize (8) + wave guid (16) = 40-byte preamble.
    if (data.Length < 40)
      throw new InvalidDataException("Wave64 too short for riff/wave header.");
    if (!data[..16].SequenceEqual(RiffGuid))
      throw new InvalidDataException("Missing Wave64 riff GUID.");
    if (!data.Slice(24, 16).SequenceEqual(WaveGuid))
      throw new InvalidDataException("Wave64 payload is not WAVE.");

    var pos = 40;
    int formatCode = 0, numChannels = 0, sampleRate = 0, bitsPerSample = 0;
    uint? channelMask = null;
    var fmtParsed = false;
    byte[]? rawData = null;
    var others = new List<(byte[], byte[])>();

    while (pos + 24 <= data.Length) {
      var guid = data.Slice(pos, 16).ToArray();
      var chunkSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[(pos + 16)..]);
      if (chunkSize < 24)
        throw new InvalidDataException("Wave64 chunk size smaller than its header.");
      var bodyStart = pos + 24;
      var bodyLen = (int)(chunkSize - 24);
      if (bodyStart + bodyLen > data.Length)
        throw new InvalidDataException("Wave64 chunk truncated.");

      var body = data.Slice(bodyStart, bodyLen);
      if (guid.AsSpan().SequenceEqual(FmtGuid)) {
        if (bodyLen < 16)
          throw new InvalidDataException("Wave64 'fmt ' chunk too small.");
        formatCode = BinaryPrimitives.ReadUInt16LittleEndian(body);
        numChannels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
        sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
        // WAVE_FORMAT_EXTENSIBLE: dwChannelMask at +20, real code 24 bytes in.
        if (formatCode == 0xFFFE && bodyLen >= 40) {
          channelMask = BinaryPrimitives.ReadUInt32LittleEndian(body[20..]);
          formatCode = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]);
        }
        fmtParsed = true;
      } else if (guid.AsSpan().SequenceEqual(DataGuid)) {
        rawData = body.ToArray();
      } else {
        others.Add((guid, body.ToArray()));
      }

      // Whole chunk (guid + size + body) is padded up to an 8-byte boundary.
      var padded = (chunkSize + 7) & ~7L;
      pos += (int)padded;
    }

    if (!fmtParsed) throw new InvalidDataException("Wave64 missing 'fmt ' chunk.");
    if (rawData == null) throw new InvalidDataException("Wave64 missing 'data' chunk.");

    return new ParsedWave64(numChannels, sampleRate, bitsPerSample, formatCode, rawData, others, channelMask);
  }
}
