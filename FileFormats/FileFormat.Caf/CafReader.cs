#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Caf;

/// <summary>
/// Apple Core Audio Format (<c>.caf</c>) parser. All container integers are big-endian;
/// LPCM sample endianness is controlled by Core Audio's standard ASBD flags.
/// G.711 is decoded to canonical PCM16; other compressed payloads remain available
/// through <see cref="ParsedCaf.InterleavedPcm"/> with their original format ID.
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
  private const uint FlagIsBigEndian = 0x2;

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  public ParsedCaf Read(ReadOnlySpan<byte> data) {
    if (data.Length < 8)
      throw new InvalidDataException("CAF too short for file header.");
    if (!data[..4].SequenceEqual("caff"u8))
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
        if (type != "data")
          throw new InvalidDataException($"CAF chunk '{type}' uses an indefinite size outside the data chunk.");
        effective = data.Length - bodyStart;
      } else {
        effective = size;
        if (effective > int.MaxValue || bodyStart + effective > data.Length)
          throw new InvalidDataException($"CAF chunk '{type}' truncated or too large.");
      }

      var body = data.Slice(bodyStart, checked((int)effective));
      switch (type) {
        case "desc":
          if (body.Length < 32)
            throw new InvalidDataException("CAF 'desc' chunk shorter than 32 bytes.");
          sampleRate = checked((int)BinaryPrimitives.ReadDoubleBigEndian(body));
          formatId = Encoding.ASCII.GetString(body.Slice(8, 4));
          formatFlags = BinaryPrimitives.ReadUInt32BigEndian(body[12..]);
          channels = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[24..]));
          bitsPerChannel = checked((int)BinaryPrimitives.ReadUInt32BigEndian(body[28..]));
          descParsed = true;
          break;
        case "data":
          rawData = body.Length >= 4 ? body[4..].ToArray() : [];
          break;
        case "chan":
          if (body.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(body) == 0x10000)
            channelMask = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
          other.Add((type, body.ToArray()));
          break;
        default:
          other.Add((type, body.ToArray()));
          break;
      }

      pos = checked(bodyStart + (int)effective);
    }

    if (!descParsed) throw new InvalidDataException("CAF missing 'desc' chunk.");
    if (channels < 1) throw new InvalidDataException("CAF channel count must be positive.");
    if (sampleRate < 1) throw new InvalidDataException("CAF sample rate must be positive.");

    var isFloat = (formatFlags & FlagIsFloat) != 0;
    var bigEndian = (formatFlags & FlagIsBigEndian) != 0;
    var payload = rawData ?? [];

    switch (formatId) {
      case "ulaw":
        return new ParsedCaf(channels, sampleRate, 16, formatFlags, false, "lpcm",
          ShortsToLePcm(Codec.MuLaw.MuLawCodec.Decode(payload)), other, channelMask);
      case "alaw":
        return new ParsedCaf(channels, sampleRate, 16, formatFlags, false, "lpcm",
          ShortsToLePcm(Codec.ALaw.ALawCodec.Decode(payload)), other, channelMask);
    }

    var canonical = payload;
    if (formatId == "lpcm" && bigEndian && bitsPerChannel > 8)
      canonical = ConvertBeToLe(payload, bitsPerChannel / 8);

    return new ParsedCaf(channels, sampleRate, bitsPerChannel, formatFlags, isFloat, formatId, canonical, other, channelMask);
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), samples[i]);
    return pcm;
  }

  private static byte[] ConvertBeToLe(ReadOnlySpan<byte> bigEndian, int bytesPerSample) {
    if (bytesPerSample <= 1) return bigEndian.ToArray();
    if (bigEndian.Length % bytesPerSample != 0)
      throw new InvalidDataException("CAF LPCM payload is not aligned to its sample width.");
    var littleEndian = new byte[bigEndian.Length];
    for (var offset = 0; offset < bigEndian.Length; offset += bytesPerSample)
      for (var i = 0; i < bytesPerSample; ++i)
        littleEndian[offset + i] = bigEndian[offset + bytesPerSample - 1 - i];
    return littleEndian;
  }
}
