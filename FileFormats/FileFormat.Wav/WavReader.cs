#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Wav;

/// <summary>
/// RIFF/WAVE header + per-channel PCM extraction. Supports linear PCM, IEEE float,
/// G.711, IMA/MS ADPCM, TrueSpeech and GSM 06.10. Block-based codecs are decoded
/// to canonical little-endian PCM and trimmed to the optional <c>fact</c> sample
/// count so codec block padding never leaks into downstream transcoding.
/// <para>G.711 is the exception: A-law/µ-law bytes carry identically into AU, AIFC and CAF, so
/// <see cref="Read"/> surfaces them verbatim under their own format code and container remuxing
/// can hand them on without a lossy decode/re-encode cycle. Callers that want samples rather than
/// packets use <see cref="ReadCanonicalPcm"/>, which decodes them like every other codec.</para>
/// </summary>
public sealed class WavReader {
  /// <summary>
  /// Represents a parsed wav.
  /// </summary>
  public sealed record ParsedWav(
    int NumChannels,
    int SampleRate,
    int BitsPerSample,
    int FormatCode,
    byte[] InterleavedPcm,
    IReadOnlyList<(string Id, byte[] Data)> MetadataChunks,
    uint? ChannelMask = null);

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
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
      case 0x0022: {
        var shorts = Codec.TrueSpeech.TrueSpeechCodec.Decode(rawData);
        return Pcm16(1, sampleRate, shorts, metadata, channelMask, factSampleFrames);
      }
      case 0x0031: {
        var shorts = Codec.Gsm610.Gsm610Codec.Decode(rawData, numChannels);
        return Pcm16(numChannels, sampleRate, shorts, metadata, channelMask, factSampleFrames);
      }
      default:
        return new ParsedWav(numChannels, sampleRate, bitsPerSample, formatCode, rawData, metadata, channelMask);
    }
  }

  /// <summary>
  /// Reads and, on top of <see cref="Read"/>, decodes the G.711 payloads that survive verbatim for
  /// remuxing, so callers wanting linear samples never have to know which codec carried them.
  /// </summary>
  public ParsedWav ReadCanonicalPcm(ReadOnlySpan<byte> data) {
    var parsed = this.Read(data);
    return parsed.FormatCode switch {
      6 => DecodeG711(parsed, Codec.ALaw.ALawCodec.Decode(parsed.InterleavedPcm)),
      7 => DecodeG711(parsed, Codec.MuLaw.MuLawCodec.Decode(parsed.InterleavedPcm)),
      _ => parsed,
    };
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
