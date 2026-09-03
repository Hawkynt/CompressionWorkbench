#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Wav;

/// <summary>
/// RIFF/WAVE header + per-channel PCM extraction. Supports linear PCM, IEEE float,
/// G.711, IMA/MS/OKI/G.72x ADPCM, G.722, MPEG audio, TrueSpeech and Microsoft GSM 06.10 WAV49.
/// Block/bit-packed codecs are decoded to canonical little-endian PCM and trimmed to
/// the optional <c>fact</c> sample count so padding never leaks into transcoding.
/// <para>G.711 is the exception: A-law/µ-law bytes carry identically into AU, AIFC and CAF, so
/// <see cref="Read"/> surfaces them verbatim under their own format code and container remuxing
/// can hand them on without a lossy decode/re-encode cycle. Callers that want samples rather than
/// packets use <see cref="ReadCanonicalPcm"/>, which decodes them like every other codec.</para>
/// </summary>
public sealed class WavReader {
  /// <summary>Represents a parsed WAVE stream.</summary>
  public sealed record ParsedWav(
    int NumChannels,
    int SampleRate,
    int BitsPerSample,
    int FormatCode,
    byte[] InterleavedPcm,
    IReadOnlyList<(string Id, byte[] Data)> MetadataChunks,
    uint? ChannelMask = null);

  /// <summary>Reads the supplied WAVE payload.</summary>
  public ParsedWav Read(ReadOnlySpan<byte> data) {
    if (data.Length < 44)
      throw new InvalidDataException("WAV too short for RIFF header + fmt/data chunks.");
    if (!data[..4].SequenceEqual("RIFF"u8))
      throw new InvalidDataException("Missing RIFF magic.");
    if (!data.Slice(8, 4).SequenceEqual("WAVE"u8))
      throw new InvalidDataException("RIFF payload is not WAVE.");

    var pos = 12;
    int formatCode = 0, numChannels = 0, sampleRate = 0, bitsPerSample = 0, blockAlign = 0;
    uint? channelMask = null;
    uint? factSampleFrames = null;
    byte[] fmtExtra = [];
    var fmtParsed = false;
    byte[]? rawData = null;
    var metadata = new List<(string, byte[])>();

    while (pos + 8 <= data.Length) {
      var id = System.Text.Encoding.ASCII.GetString(data.Slice(pos, 4));
      var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]));
      var bodyStart = pos + 8;
      if (size < 0 || bodyStart + (long)size > data.Length)
        throw new InvalidDataException($"Chunk '{id}' truncated.");

      switch (id) {
        case "fmt ": {
          if (size < 16) throw new InvalidDataException("WAV 'fmt ' chunk is shorter than 16 bytes.");
          formatCode = BinaryPrimitives.ReadUInt16LittleEndian(data[bodyStart..]);
          numChannels = BinaryPrimitives.ReadUInt16LittleEndian(data[(bodyStart + 2)..]);
          sampleRate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[(bodyStart + 4)..]));
          blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(data[(bodyStart + 12)..]);
          bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data[(bodyStart + 14)..]);
          fmtExtra = size > 16 ? data.Slice(bodyStart + 16, size - 16).ToArray() : [];
          if (formatCode == 0xFFFE && size >= 40) {
            channelMask = BinaryPrimitives.ReadUInt32LittleEndian(data[(bodyStart + 20)..]);
            formatCode = BinaryPrimitives.ReadUInt16LittleEndian(data[(bodyStart + 24)..]);
          }
          fmtParsed = true;
          break;
        }
        case "fact":
          if (size >= 4)
            factSampleFrames = BinaryPrimitives.ReadUInt32LittleEndian(data[bodyStart..]);
          metadata.Add((id, data.Slice(bodyStart, size).ToArray()));
          break;
        case "data":
          rawData = data.Slice(bodyStart, size).ToArray();
          break;
        default:
          metadata.Add((id, data.Slice(bodyStart, size).ToArray()));
          break;
      }
      pos = bodyStart + size + (size & 1);
    }

    if (!fmtParsed) throw new InvalidDataException("WAV missing 'fmt ' chunk.");
    if (rawData is null) throw new InvalidDataException("WAV missing 'data' chunk.");
    if (numChannels < 1) throw new InvalidDataException("WAV channel count must be positive.");
    if (sampleRate < 1) throw new InvalidDataException("WAV sample rate must be positive.");

    switch (formatCode) {
      case 0x0011: {
        if (blockAlign <= 0) throw new InvalidDataException("IMA ADPCM needs blockAlign.");
        var perChannel = Codec.ImaAdpcm.ImaAdpcmCodec.Decode(rawData, blockAlign, numChannels);
        return Pcm16(numChannels, sampleRate, InterleaveChannels(perChannel, factSampleFrames), metadata, channelMask, null);
      }
      case 0x0002: {
        if (blockAlign <= 0) throw new InvalidDataException("MS ADPCM needs blockAlign.");
        var perChannel = Codec.MsAdpcm.MsAdpcmCodec.Decode(rawData, blockAlign, numChannels);
        return Pcm16(numChannels, sampleRate, InterleaveChannels(perChannel, factSampleFrames), metadata, channelMask, null);
      }
      case 0x0010 or 0x0017: {
        RequireMono(numChannels, "OKI ADPCM");
        if (bitsPerSample is not (0 or 4))
          throw new InvalidDataException($"OKI ADPCM expects 4 coded bits/sample, got {bitsPerSample}.");
        return Pcm16(1, sampleRate, Codec.OkiAdpcm.OkiAdpcmCodec.Decode(rawData), metadata, channelMask, factSampleFrames);
      }
      case 0x0022: {
        var shorts = Codec.TrueSpeech.TrueSpeechCodec.Decode(rawData);
        return Pcm16(1, sampleRate, shorts, metadata, channelMask, factSampleFrames);
      }
      case 0x0031: {
        RequireMono(numChannels, "Microsoft GSM 06.10");
        if (sampleRate != 8_000)
          throw new InvalidDataException($"Microsoft GSM 06.10 WAVE requires 8000 Hz, got {sampleRate} Hz.");
        if (blockAlign != Codec.Gsm610.Gsm610Wav49.BlockBytes)
          throw new InvalidDataException($"Microsoft GSM 06.10 WAVE requires 65-byte WAV49 blocks, got {blockAlign}.");
        if (fmtExtra.Length < 4 || BinaryPrimitives.ReadUInt16LittleEndian(fmtExtra) != 2 ||
            BinaryPrimitives.ReadUInt16LittleEndian(fmtExtra.AsSpan(2)) != Codec.Gsm610.Gsm610Wav49.SamplesPerBlock)
          throw new InvalidDataException("Microsoft GSM 06.10 WAVE needs cbSize=2 and samplesPerBlock=320.");
        return Pcm16(1, sampleRate, Codec.Gsm610.Gsm610Wav49.Decode(rawData), metadata, channelMask, factSampleFrames);
      }
      case 0x0014: {
        RequireMono(numChannels, "Antex G.723 ADPCM");
        if (bitsPerSample is not (3 or 5))
          throw new InvalidDataException("Antex G.723 ADPCM WAVE uses the 24 or 40 kbit/s G.72x modes (3 or 5 bits/sample).");
        return Pcm16(1, sampleRate, Codec.G72x.G72xCodec.DecodeG726(rawData, bitsPerSample), metadata, channelMask, factSampleFrames);
      }
      case 0x0040: {
        RequireMono(numChannels, "G.721 ADPCM");
        if (bitsPerSample is not (0 or 4))
          throw new InvalidDataException($"G.721 ADPCM expects 4 coded bits/sample, got {bitsPerSample}.");
        return Pcm16(1, sampleRate, Codec.G72x.G72xCodec.DecodeG721(rawData), metadata, channelMask, factSampleFrames);
      }
      case 0x0045 or 0x0064: {
        RequireMono(numChannels, "G.726 ADPCM");
        if (bitsPerSample is not (2 or 3 or 4 or 5))
          throw new InvalidDataException($"G.726 ADPCM requires 2, 3, 4 or 5 coded bits/sample, got {bitsPerSample}.");
        return Pcm16(1, sampleRate, Codec.G72x.G72xCodec.DecodeG726(rawData, bitsPerSample), metadata, channelMask, factSampleFrames);
      }
      case 0x0050 or 0x0055:
        return DecodeMpegAudio(rawData, formatCode, numChannels, sampleRate, fmtExtra, metadata, channelMask, factSampleFrames);
      case 0x0065 or 0x028F: {
        RequireMono(numChannels, "G.722 ADPCM");
        if (sampleRate != 16_000)
          throw new InvalidDataException($"G.722 WAVE requires 16000 Hz decoded sample rate, got {sampleRate} Hz.");
        if (bitsPerSample is not (0 or 4))
          throw new InvalidDataException($"The checked-in G.722 route implements the 64 kbit/s mode (4 coded bits/sample), got {bitsPerSample}.");
        return Pcm16(1, sampleRate, Codec.G722.G722Codec.Decode(rawData), metadata, channelMask, factSampleFrames);
      }
      default:
        return new ParsedWav(numChannels, sampleRate, bitsPerSample, formatCode, rawData, metadata, channelMask);
    }
  }

  /// <summary>
  /// Reads and, on top of <see cref="Read"/>, decodes G.711 payloads that survive verbatim for
  /// remuxing, so callers wanting linear samples never have to know which registration carried them.
  /// </summary>
  public ParsedWav ReadCanonicalPcm(ReadOnlySpan<byte> data) {
    var parsed = this.Read(data);
    return parsed.FormatCode switch {
      0x0006 or 0x0102 => DecodeG711(parsed, Codec.ALaw.ALawCodec.Decode(parsed.InterleavedPcm)),
      0x0007 => DecodeG711(parsed, Codec.MuLaw.MuLawCodec.Decode(parsed.InterleavedPcm)),
      _ => parsed,
    };
  }

  private static ParsedWav DecodeMpegAudio(
    byte[] rawData,
    int formatCode,
    int declaredChannels,
    int declaredSampleRate,
    byte[] fmtExtra,
    IReadOnlyList<(string Id, byte[] Data)> metadata,
    uint? channelMask,
    uint? factSampleFrames) {
    var header = FindFirstMpegHeader(rawData);
    if (formatCode == 0x0055) {
      if (header.Layer != 3)
        throw new InvalidDataException($"WAVE_FORMAT_MPEGLAYER3 carries MPEG Layer {header.Layer}, expected Layer III.");
      if (fmtExtra.Length < 14 || BinaryPrimitives.ReadUInt16LittleEndian(fmtExtra) < 12)
        throw new InvalidDataException("MPEGLAYER3WAVEFORMAT requires cbSize >= 12.");
      var id = BinaryPrimitives.ReadUInt16LittleEndian(fmtExtra.AsSpan(2));
      if (id is not (1 or 2))
        throw new InvalidDataException($"Unsupported MPEGLAYER3WAVEFORMAT wID {id}.");
      if (BinaryPrimitives.ReadUInt16LittleEndian(fmtExtra.AsSpan(10)) == 0)
        throw new InvalidDataException("MPEGLAYER3WAVEFORMAT nFramesPerBlock must be positive.");
    }

    if (header.Channels != declaredChannels)
      throw new InvalidDataException($"MPEG frame declares {header.Channels} channels but WAVE fmt declares {declaredChannels}.");
    if (header.SampleRateHz != declaredSampleRate)
      throw new InvalidDataException($"MPEG frame declares {header.SampleRateHz} Hz but WAVE fmt declares {declaredSampleRate} Hz.");

    using var encoded = new MemoryStream(rawData, writable: false);
    using var decoded = new MemoryStream();
    Codec.Mp3.Mp3Codec.Decompress(encoded, decoded);
    return Pcm16Bytes(declaredChannels, declaredSampleRate, decoded.ToArray(), metadata, channelMask, factSampleFrames);
  }

  private static Codec.Mp3.Mp3FrameHeader FindFirstMpegHeader(ReadOnlySpan<byte> data) {
    for (var offset = 0; offset + 4 <= data.Length; ++offset) {
      if (data[offset] != 0xFF || (data[offset + 1] & 0xE0) != 0xE0)
        continue;
      try {
        var header = Codec.Mp3.Mp3FrameHeader.Parse(data.Slice(offset, 4));
        if (header.FrameLengthBytes > 0 && offset + header.FrameLengthBytes <= data.Length)
          return header;
      } catch (InvalidDataException) {
        // False-positive sync candidate; keep scanning.
      }
    }
    throw new InvalidDataException("MPEG audio WAVE payload contains no complete MPEG frame.");
  }

  private static void RequireMono(int channels, string codec) {
    if (channels != 1)
      throw new InvalidDataException($"{codec} WAVE route currently requires mono; got {channels} channels.");
  }

  private static ParsedWav DecodeG711(ParsedWav parsed, ReadOnlySpan<short> samples) {
    uint? factSampleFrames = null;
    foreach (var (id, body) in parsed.MetadataChunks)
      if (id == "fact" && body.Length >= 4) {
        factSampleFrames = BinaryPrimitives.ReadUInt32LittleEndian(body);
        break;
      }
    return Pcm16(parsed.NumChannels, parsed.SampleRate, samples,
      parsed.MetadataChunks, parsed.ChannelMask, factSampleFrames);
  }

  private static ParsedWav Pcm16(int channels, int sampleRate, ReadOnlySpan<short> samples,
    IReadOnlyList<(string Id, byte[] Data)> metadata, uint? channelMask, uint? factSampleFrames) {
    var sampleCount = samples.Length;
    if (factSampleFrames is { } frames) {
      var requested = Math.Min((long)sampleCount, (long)frames * channels);
      sampleCount = checked((int)requested);
    }
    var pcm = new byte[sampleCount * 2];
    for (var i = 0; i < sampleCount; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), samples[i]);
    return new ParsedWav(channels, sampleRate, 16, 1, pcm, metadata, channelMask);
  }

  private static ParsedWav Pcm16Bytes(int channels, int sampleRate, byte[] pcm,
    IReadOnlyList<(string Id, byte[] Data)> metadata, uint? channelMask, uint? factSampleFrames) {
    if ((pcm.Length & 1) != 0)
      throw new InvalidDataException("Decoded PCM16 payload has odd byte length.");
    var availableSamples = pcm.Length / 2;
    var sampleCount = factSampleFrames is { } frames
      ? checked((int)Math.Min((long)availableSamples, (long)frames * channels))
      : availableSamples;
    if (sampleCount * 2 != pcm.Length)
      Array.Resize(ref pcm, checked(sampleCount * 2));
    return new ParsedWav(channels, sampleRate, 16, 1, pcm, metadata, channelMask);
  }

  private static short[] InterleaveChannels(short[][] perChannel, uint? factSampleFrames) {
    if (perChannel.Length == 0) return [];
    var channels = perChannel.Length;
    var availableFrames = perChannel.Min(static channel => channel.Length);
    var frameCount = factSampleFrames is { } frames
      ? checked((int)Math.Min((long)availableFrames, frames))
      : availableFrames;
    var samples = new short[checked(frameCount * channels)];
    for (var frame = 0; frame < frameCount; ++frame)
      for (var channel = 0; channel < channels; ++channel)
        samples[frame * channels + channel] = perChannel[channel][frame];
    return samples;
  }
}
