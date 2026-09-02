#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Caf;

/// <summary>
/// Apple Core Audio Format (<c>.caf</c>) parser. All multi-byte integers and the
/// IEEE-754 sample rate are big-endian. Layout:
/// <list type="bullet">
///   <item>File header: ASCII <c>caff</c> | uint16 version (=1) | uint16 flags (=0).</item>
///   <item>A sequence of chunks: 4-char ASCII type | int64 size | body[size].
///         A <c>data</c> chunk may carry size = -1 meaning "to EOF".</item>
///   <item><c>desc</c> chunk (32-byte body): float64 sample rate | 4-char format id
///         (=<c>lpcm</c>) | uint32 format flags | uint32 bytes-per-packet |
///         uint32 frames-per-packet | uint32 channels-per-frame | uint32 bits-per-channel.</item>
///   <item><c>data</c> chunk: uint32 edit-count then interleaved PCM bytes.</item>
/// </list>
/// Format flags: bit 0 (0x1) = IEEE float; bit 1 (0x2) = little-endian samples.
/// For integer PCM, default (flags = 0) means big-endian samples; this reader converts
/// such samples to little-endian so downstream callers (and <c>PcmCodec</c>) see a
/// canonical little-endian buffer.
/// <para>The G.711 companded formats <c>ulaw</c> and <c>alaw</c> are decoded to 16-bit
/// little-endian PCM (one source byte per channel sample, channels interleaved bytewise
/// exactly like LPCM) via <c>Codec.MuLaw</c>/<c>Codec.ALaw</c>; the result reports
/// <see cref="ParsedCaf.FormatId"/> = <c>lpcm</c> and <see cref="ParsedCaf.BitsPerSample"/>
/// = 16 so the per-channel split path applies. Other compressed formats (<c>ima4</c>,
/// <c>aac </c>, …) pass through undecoded and are surfaced as <c>FULL.caf</c> only.</para>
/// Any chunk other than <c>desc</c>/<c>data</c> is kept addressable through
/// <see cref="ParsedCaf.OtherChunks"/>.
/// </summary>
public sealed class CafReader {
    /// <summary>
  /// Represents a parsed caf.
  /// </summary>
public sealed record ParsedCaf(
    int NumChannels,
    int SampleRate,
    int BitsPerSample,
    uint FormatFlags,
    bool IsFloat,
    string FormatId,
    byte[] InterleavedPcm,
    IReadOnlyList<(string Type, byte[] Data)> OtherChunks,
    uint? ChannelMask = null);

  private const uint FlagIsFloat = 0x1;
  private const uint FlagIsLittleEndian = 0x2;

    /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
public ParsedCaf Read(ReadOnlySpan<byte> data) {
    if (data.Length < 8)
      throw new InvalidDataException("CAF too short for file header.");
    if (data[0] != 'c' || data[1] != 'a' || data[2] != 'f' || data[3] != 'f')
      throw new InvalidDataException("Missing 'caff' magic.");

    var pos = 8;
    uint? channelMask = null;
    var descParsed = false;
    int channels = 0, sampleRate = 0, bitsPerChannel = 0;
    uint formatFlags = 0;
    var formatId = "";
    byte[]? rawData = null;
    var other = new List<(string, byte[])>();

    while (pos + 12 <= data.Length) {
      var type = Encoding.ASCII.GetString(data.Slice(pos, 4));
      var size = BinaryPrimitives.ReadInt64BigEndian(data[(pos + 4)..]);
      var bodyStart = pos + 12;

      long effective;
      if (size < 0) {
        // "to EOF" — only valid for the audio data chunk.
        effective = data.Length - bodyStart;
      } else {
        effective = size;
        if (bodyStart + effective > data.Length)
          throw new InvalidDataException($"CAF chunk '{type}' truncated.");
      }

      var body = data.Slice(bodyStart, (int)effective);

      switch (type) {
        case "desc":
          if (body.Length < 32)
            throw new InvalidDataException("CAF 'desc' chunk shorter than 32 bytes.");
          sampleRate = (int)BinaryPrimitives.ReadDoubleBigEndian(body);
          formatId = Encoding.ASCII.GetString(body.Slice(8, 4));
          formatFlags = BinaryPrimitives.ReadUInt32BigEndian(body[12..]);
          channels = (int)BinaryPrimitives.ReadUInt32BigEndian(body[24..]);
          bitsPerChannel = (int)BinaryPrimitives.ReadUInt32BigEndian(body[28..]);
          descParsed = true;
          break;
        case "data":
          // First 4 bytes are mEditCount; the rest is the audio payload.
          rawData = body.Length >= 4 ? body[4..].ToArray() : [];
          break;
        case "chan":
          // AudioChannelLayout: mChannelLayoutTag | mChannelBitmap | descriptions.
          // Only the UseChannelBitmap tag (0x10000) carries a WAVE-order speaker
          // mask we can name channels from; other tags stay raw metadata.
          if (body.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(body) == 0x10000)
            channelMask = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
          other.Add((type, body.ToArray()));
          break;
        default:
          other.Add((type, body.ToArray()));
          break;
      }

      pos = bodyStart + (int)effective;
    }

    if (!descParsed) throw new InvalidDataException("CAF missing 'desc' chunk.");

    var isFloat = (formatFlags & FlagIsFloat) != 0;
    var littleEndian = (formatFlags & FlagIsLittleEndian) != 0;
    var payload = rawData ?? [];

    // G.711 companded formats: decode each byte to a 16-bit linear sample. Bytes are
    // interleaved by channel exactly like LPCM, so the existing channel-split path
    // applies once we expose the decoded 16-bit LE PCM as canonical lpcm.
    switch (formatId) {
      case "ulaw":
        return new ParsedCaf(channels, sampleRate, BitsPerSample: 16, formatFlags, IsFloat: false,
          FormatId: "lpcm", ShortsToLePcm(Codec.MuLaw.MuLawCodec.Decode(payload)), other, channelMask);
      case "alaw":
        return new ParsedCaf(channels, sampleRate, BitsPerSample: 16, formatFlags, IsFloat: false,
          FormatId: "lpcm", ShortsToLePcm(Codec.ALaw.ALawCodec.Decode(payload)), other, channelMask);
    }

    // Convert big-endian integer samples to little-endian so PcmCodec sees canonical PCM.
    var canonical = payload;
    if (!isFloat && !littleEndian && bitsPerChannel > 8)
      canonical = ConvertBeToLe(payload, bitsPerChannel / 8);

    return new ParsedCaf(channels, sampleRate, bitsPerChannel, formatFlags, isFloat, formatId, canonical, other, channelMask);
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }

  private static byte[] ConvertBeToLe(byte[] be, int bytesPerSample) {
    if (bytesPerSample <= 1) return (byte[])be.Clone();
    var le = new byte[be.Length];
    for (var i = 0; i + bytesPerSample <= be.Length; i += bytesPerSample)
      for (var j = 0; j < bytesPerSample; ++j)
        le[i + j] = be[i + bytesPerSample - 1 - j];
    return le;
  }
}
