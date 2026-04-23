#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// Stream-level information about an AAC bitstream extracted from its first
/// frame header (ADTS) or AudioSpecificConfig (raw / MP4).
/// </summary>
/// <param name="SampleRate">Output sample rate in Hz.</param>
/// <param name="Channels">Number of decoded PCM channels (1 or 2 for AAC-LC mono/stereo).</param>
/// <param name="Profile">AAC profile / object type as the integer value of <see cref="AacObjectType"/>.</param>
/// <param name="DurationSamples">Estimated total decoded samples per channel (-1 if unknown).</param>
public sealed record AacStreamInfo(int SampleRate, int Channels, int Profile, long DurationSamples);

/// <summary>
/// Top-level AAC-LC decoder. Wraps an <see cref="AacAdtsReader"/> for framing
/// and an <see cref="AacDecoder"/> for raw_data_block decoding. Output is
/// interleaved little-endian signed 16-bit PCM.
/// <para>
/// <b>Supported:</b> AAC-LC (object type 2) in ADTS framing, mono &amp; stereo,
/// standard sample rates 8 kHz – 48 kHz.
/// </para>
/// <para>
/// <b>Not supported (raises <see cref="NotSupportedException"/> with a clear message):</b>
/// HE-AAC (SBR), HE-AAC v2 (PS), Main/SSR/LTP profiles, ER profiles, MPEG-2 raw,
/// xHE-AAC / USAC, &gt;2 channels, ADIF / LATM / LOAS framing.
/// </para>
/// </summary>
public static class AacCodec {

  /// <summary>
  /// Decompresses an AAC ADTS stream into interleaved little-endian signed
  /// 16-bit PCM on <paramref name="output"/>.
  /// </summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var data = ReadAll(input);
    if (data.Length < AacAdtsReader.ShortHeaderLength)
      throw new InvalidDataException("AAC: input too short to contain an ADTS header.");

    var pos = FindAdtsSync(data, 0);
    if (pos < 0)
      throw new InvalidDataException("AAC: no ADTS sync word found. ADIF/LATM/LOAS framing not supported.");

    var first = AacAdtsReader.ParseHeader(data, pos);
    AssertProfileSupported(first.ObjectType);

    var decoder = new AacDecoder(first.ObjectType, first.SampleRateIndex, first.ChannelConfiguration);

    while (pos + AacAdtsReader.ShortHeaderLength <= data.Length) {
      var header = AacAdtsReader.ParseHeader(data, pos);
      AssertProfileSupported(header.ObjectType);

      var payloadOffset = pos + header.HeaderLengthBytes;
      var payloadLength = header.FrameLength - header.HeaderLengthBytes;
      if (payloadLength <= 0 || payloadOffset + payloadLength > data.Length)
        throw new InvalidDataException("AAC: ADTS frame length overruns input.");

      // Decode each raw_data_block declared in the ADTS header.
      var reader = new AacBitReader(data, payloadOffset, payloadLength);
      var blocks = header.NumberOfRawDataBlocks + 1;
      for (var i = 0; i < blocks; ++i) {
        var samples = decoder.DecodeRawDataBlock(reader);
        WritePcm(output, samples);
      }

      pos += header.FrameLength;
      // Optional: re-sync to next 0xFFF if the decoder wandered.
      if (pos + 2 <= data.Length && (data[pos] != 0xFF || (data[pos + 1] & 0xF0) != 0xF0)) {
        var resync = FindAdtsSync(data, pos);
        if (resync < 0) break;
        pos = resync;
      }
    }
  }

  /// <summary>
  /// Reads the first ADTS header to surface stream-level metadata without
  /// decoding any audio. Useful for archive-descriptor probes.
  /// </summary>
  public static AacStreamInfo ReadStreamInfo(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var data = ReadAll(input);
    var pos = FindAdtsSync(data, 0);
    if (pos < 0)
      throw new InvalidDataException("AAC: no ADTS sync word found.");

    var header = AacAdtsReader.ParseHeader(data, pos);
    long approxFrames = 0;
    var p = pos;
    while (p + AacAdtsReader.ShortHeaderLength <= data.Length) {
      if (data[p] != 0xFF || (data[p + 1] & 0xF0) != 0xF0) {
        var resync = FindAdtsSync(data, p + 1);
        if (resync < 0) break;
        p = resync;
        continue;
      }
      var h = AacAdtsReader.ParseHeader(data, p);
      approxFrames += h.NumberOfRawDataBlocks + 1;
      if (h.FrameLength <= 0) break;
      p += h.FrameLength;
    }

    var samplesPerChannel = approxFrames * AacFilterBank.LongFrameSize;
    var channels = header.ChannelConfiguration is >= 1 and <= 2
      ? header.ChannelConfiguration
      : 2;
    return new AacStreamInfo(
      SampleRate: header.SampleRate,
      Channels: channels,
      Profile: (int)header.ObjectType,
      DurationSamples: samplesPerChannel);
  }

  /// <summary>
  /// Validates that <paramref name="objectType"/> is AAC-LC (the only supported
  /// profile). Throws <see cref="NotSupportedException"/> with a message
  /// distinguishing HE-AAC / Main / SSR / LTP / ER / USAC.
  /// </summary>
  internal static void AssertProfileSupported(AacObjectType objectType) {
    switch (objectType) {
      case AacObjectType.AacLc:
        return;
      case AacObjectType.AacMain:
        throw new NotSupportedException("AAC Main profile (object type 1) is not supported. AAC-LC only.");
      case AacObjectType.AacSsr:
        throw new NotSupportedException("AAC SSR profile (object type 3) is not supported. AAC-LC only.");
      case AacObjectType.AacLtp:
        throw new NotSupportedException("AAC LTP profile (object type 4) is not supported. AAC-LC only.");
      case AacObjectType.Sbr:
        throw new NotSupportedException("HE-AAC (SBR, object type 5) is not supported. AAC-LC only.");
      case AacObjectType.Ps:
        throw new NotSupportedException("HE-AAC v2 (Parametric Stereo, object type 29) is not supported. AAC-LC only.");
      case AacObjectType.Er_AacLc:
      case AacObjectType.Er_AacLtp:
      case AacObjectType.Er_AacScalable:
        throw new NotSupportedException($"AAC Error-Resilient profile {objectType} is not supported. AAC-LC only.");
      default:
        throw new NotSupportedException(
          $"AAC object type {objectType} ({(int)objectType}) is not supported. " +
          "Only AAC-LC (object type 2) is implemented.");
    }
  }

  /// <summary>
  /// Inspects the AudioSpecificConfig (used by MP4-in-ISOBMFF and LATM) and
  /// rejects HE-AAC / PS / non-LC explicitly. Returns the parsed object type
  /// and sample rate index for callers that need them.
  /// </summary>
  public static (AacObjectType ObjectType, int SampleRateIndex, int ChannelConfiguration)
    ParseAudioSpecificConfig(ReadOnlySpan<byte> asc) {
    if (asc.Length < 2)
      throw new InvalidDataException("AudioSpecificConfig too short.");

    var buf = new byte[asc.Length];
    asc.CopyTo(buf);
    var reader = new AacBitReader(buf);

    var ot = ReadObjectType(reader);
    var srIdx = (int)reader.ReadBits(4);
    if (srIdx == 15) reader.SkipBits(24); // explicit sample rate
    var channelConfig = (int)reader.ReadBits(4);

    if (ot == AacObjectType.Sbr || ot == AacObjectType.Ps) {
      throw new NotSupportedException(
        $"AudioSpecificConfig signals HE-AAC ({ot}); only AAC-LC is supported.");
    }

    AssertProfileSupported(ot);
    return (ot, srIdx, channelConfig);
  }

  // ---------------- helpers ----------------

  private static AacObjectType ReadObjectType(AacBitReader reader) {
    var ot = (int)reader.ReadBits(5);
    if (ot == 31)
      ot = 32 + (int)reader.ReadBits(6);
    return (AacObjectType)ot;
  }

  private static int FindAdtsSync(byte[] data, int start) {
    for (var i = start; i + 1 < data.Length; ++i)
      if (data[i] == 0xFF && (data[i + 1] & 0xF0) == 0xF0)
        return i;
    return -1;
  }

  private static byte[] ReadAll(Stream input) {
    if (input is MemoryStream ms && ms.TryGetBuffer(out var seg) && seg.Offset == 0 && seg.Count == seg.Array!.Length)
      return seg.Array;
    using var tmp = new MemoryStream();
    input.CopyTo(tmp);
    return tmp.ToArray();
  }

  private static void WritePcm(Stream output, short[] samples) {
    if (samples.Length == 0) return;
    var bytes = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i) {
      bytes[i * 2] = (byte)(samples[i] & 0xFF);
      bytes[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
    }
    output.Write(bytes, 0, bytes.Length);
  }
}
