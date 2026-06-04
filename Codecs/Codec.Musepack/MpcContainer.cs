#pragma warning disable CS1591

namespace Codec.Musepack;

/// <summary>
/// Parser for the Musepack SV8 (<c>MPCK</c>) container: a byte-aligned stream of
/// chunks, each a 2-character ASCII tag followed by a base-128 varint
/// <em>total</em> size (tag + varint + payload), then the payload. Ported from
/// FFmpeg's <c>libavformat/mpc8.c</c> (<c>ffio_read_varlen</c> +
/// <c>mpc8_get_chunk_header</c>). Recognised tags: <c>SH</c> stream header,
/// <c>RG</c> replay gain, <c>EI</c> encoder info, <c>SO</c>/<c>ST</c> seek
/// offset/table, <c>AP</c> audio packet, <c>SE</c> stream end.
/// </summary>
internal static class MpcContainer {

  public static readonly byte[] MagicSv8 = "MPCK"u8.ToArray();
  public static readonly byte[] MagicSv7 = "MP+"u8.ToArray();

  /// <summary>A parsed chunk: its two-character tag plus the payload span [<see cref="PayloadStart"/>, +<see cref="PayloadLength"/>).</summary>
  public readonly record struct Chunk(string Tag, int PayloadStart, int PayloadLength);

  /// <summary>Stream-header fields decoded from the SH chunk.</summary>
  public sealed record StreamHeader(
    uint Crc, int Version, long SampleCount, long BeginningSilence,
    int SampleRate, int MaxBand, int Channels, bool MidSideUsed, int FramesPerPacket);

  /// <summary>Reads a base-128 big-endian varint (MSB = continuation). Advances <paramref name="pos"/>.</summary>
  public static long ReadVarint(byte[] data, ref int pos) {
    long value = 0;
    int b;
    do {
      if (pos >= data.Length)
        throw new InvalidDataException("Musepack: varint runs past end of stream.");
      b = data[pos++];
      value = (value << 7) | (uint)(b & 0x7F);
    } while ((b & 0x80) != 0);
    return value;
  }

  /// <summary>
  /// Reads one chunk header at <paramref name="pos"/>, returning the tag and the
  /// payload range. <paramref name="pos"/> is advanced to the start of the payload.
  /// The varint encodes the total chunk size (including the 2-byte tag and the
  /// varint bytes themselves); the payload length is that minus the header bytes.
  /// </summary>
  public static Chunk ReadChunkHeader(byte[] data, ref int pos) {
    if (pos + 2 > data.Length)
      throw new InvalidDataException("Musepack: truncated chunk tag.");
    var tag = $"{(char)data[pos]}{(char)data[pos + 1]}";
    var headerStart = pos;
    pos += 2;
    var totalSize = ReadVarint(data, ref pos);
    var headerBytes = pos - headerStart;
    var payloadLength = totalSize - headerBytes;
    if (payloadLength < 0 || pos + payloadLength > data.Length)
      throw new InvalidDataException($"Musepack: chunk '{tag}' size {totalSize} overruns the stream.");
    return new Chunk(tag, pos, (int)payloadLength);
  }

  /// <summary>
  /// Parses the SH chunk payload: 4-byte CRC, 1-byte version (must be 8), varint
  /// sample count, varint beginning silence, then two extradata bytes packing the
  /// sample-rate index (3 bits), max band (5 bits), channel count (4 bits),
  /// mid-side flag (1 bit) and frames-per-packet exponent (3 bits).
  /// </summary>
  public static StreamHeader ParseStreamHeader(byte[] data, Chunk sh) {
    var pos = sh.PayloadStart;
    var end = sh.PayloadStart + sh.PayloadLength;
    if (pos + 5 > end)
      throw new InvalidDataException("Musepack: SH chunk too small.");

    var crc = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
    pos += 4;
    var version = data[pos++];
    if (version != 8)
      throw new NotSupportedException($"Musepack: unsupported SV8 stream-header version {version}.");

    var sampleCount = ReadVarint(data, ref pos);
    var beginningSilence = ReadVarint(data, ref pos);
    if (pos + 2 > end)
      throw new InvalidDataException("Musepack: SH chunk missing audio parameters.");

    var b0 = data[pos];
    var b1 = data[pos + 1];
    var sampleRateIdx = b0 >> 5;
    var maxBand = (b0 & 0x1F) + 1;
    var channels = (b1 >> 4) + 1;
    var midSide = (b1 & 0x08) != 0;
    var framesExp = b1 & 0x07;

    if (sampleRateIdx >= MpcTables.SampleRates.Length)
      throw new InvalidDataException($"Musepack: invalid sample-rate index {sampleRateIdx}.");

    return new StreamHeader(
      crc, version, sampleCount, beginningSilence,
      MpcTables.SampleRates[sampleRateIdx], maxBand, channels, midSide,
      1 << (framesExp * 2));
  }
}
