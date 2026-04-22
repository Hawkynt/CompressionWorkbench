namespace Codec.Pcm;

/// <summary>
/// PCM codec: integer/float sample packing, channel interleave/deinterleave, and
/// canonical RIFF/WAVE header framing. Used by audio-container descriptors (WAV,
/// FLAC-archive, future Opus/Vorbis) that surface per-channel mono WAVs as archive
/// entries.
/// </summary>
public static class PcmCodec {

  /// <summary>Conventional channel names per layout; falls back to CH_0..CH_N.</summary>
  public static IReadOnlyList<string> LayoutNames(int channels) => channels switch {
    1 => ["MONO"],
    2 => ["LEFT", "RIGHT"],
    3 => ["LEFT", "RIGHT", "CENTER"],
    4 => ["FRONT_LEFT", "FRONT_RIGHT", "BACK_LEFT", "BACK_RIGHT"],
    5 => ["FRONT_LEFT", "FRONT_RIGHT", "CENTER", "BACK_LEFT", "BACK_RIGHT"],
    6 => ["FRONT_LEFT", "FRONT_RIGHT", "CENTER", "LFE", "BACK_LEFT", "BACK_RIGHT"],
    8 => ["FRONT_LEFT", "FRONT_RIGHT", "CENTER", "LFE",
          "BACK_LEFT", "BACK_RIGHT", "SIDE_LEFT", "SIDE_RIGHT"],
    _ => Enumerable.Range(0, channels).Select(i => $"CH_{i}").ToArray(),
  };

  /// <summary>
  /// Splits interleaved little-endian signed-integer PCM into per-channel mono WAV blobs.
  /// Channels are returned in the order they occur in <paramref name="interleaved"/>.
  /// </summary>
  public static IReadOnlyList<(string Name, byte[] WavBlob)> SplitInterleavedPcm(
      byte[] interleaved, int channels, int sampleRate, int bitsPerSample) {
    if (channels <= 1)
      return [("MONO", ToWavBlob(interleaved, channels: 1, sampleRate, bitsPerSample, formatCode: 1))];

    var bytesPerSample = bitsPerSample / 8;
    var frameBytes = bytesPerSample * channels;
    if (interleaved.Length % frameBytes != 0)
      throw new ArgumentException("Interleaved PCM length is not a multiple of frame size.");

    var frameCount = interleaved.Length / frameBytes;
    var names = LayoutNames(channels);
    var result = new List<(string, byte[])>(channels);

    for (var c = 0; c < channels; ++c) {
      var mono = new byte[frameCount * bytesPerSample];
      for (var f = 0; f < frameCount; ++f) {
        var src = f * frameBytes + c * bytesPerSample;
        var dst = f * bytesPerSample;
        Buffer.BlockCopy(interleaved, src, mono, dst, bytesPerSample);
      }
      result.Add((names[c], ToWavBlob(mono, channels: 1, sampleRate, bitsPerSample, formatCode: 1)));
    }
    return result;
  }

  /// <summary>
  /// Splits per-channel integer samples into per-channel mono WAV blobs. Widths wider
  /// than <paramref name="bitsPerSample"/> are truncated via two's-complement masking.
  /// </summary>
  public static IReadOnlyList<(string Name, byte[] WavBlob)> SplitPerChannelIntSamples(
      int[][] perChannel, int sampleRate, int bitsPerSample) {
    if (perChannel.Length == 0) return [];
    var frameCount = perChannel[0].Length;
    var bytesPerSample = bitsPerSample / 8;
    var names = LayoutNames(perChannel.Length);
    var result = new List<(string, byte[])>(perChannel.Length);

    for (var c = 0; c < perChannel.Length; ++c) {
      if (perChannel[c].Length != frameCount)
        throw new ArgumentException("Per-channel sample arrays must have equal length.");
      var mono = new byte[frameCount * bytesPerSample];
      for (var f = 0; f < frameCount; ++f) {
        var v = perChannel[c][f];
        for (var b = 0; b < bytesPerSample; ++b)
          mono[f * bytesPerSample + b] = (byte)((v >> (b * 8)) & 0xFF);
      }
      result.Add((names[c], ToWavBlob(mono, channels: 1, sampleRate, bitsPerSample, formatCode: 1)));
    }
    return result;
  }

  /// <summary>
  /// Wraps raw little-endian PCM bytes in a minimal RIFF/WAVE header.
  /// <paramref name="formatCode"/>: 1 = PCM integer, 3 = IEEE float.
  /// </summary>
  public static byte[] ToWavBlob(byte[] pcm, int channels, int sampleRate, int bitsPerSample, int formatCode = 1) {
    var byteRate = sampleRate * channels * bitsPerSample / 8;
    var blockAlign = (ushort)(channels * bitsPerSample / 8);
    const int fmtSize = 16;
    var dataSize = pcm.Length;
    var fileSize = 4 + (8 + fmtSize) + (8 + dataSize);

    var wav = new byte[8 + fileSize];
    var s = wav.AsSpan();
    "RIFF"u8.CopyTo(s);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)fileSize);
    "WAVE"u8.CopyTo(s[8..]);
    "fmt "u8.CopyTo(s[12..]);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s[16..], fmtSize);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(s[20..], (ushort)formatCode);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(s[22..], (ushort)channels);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)sampleRate);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s[28..], (uint)byteRate);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(s[32..], blockAlign);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(s[34..], (ushort)bitsPerSample);
    "data"u8.CopyTo(s[36..]);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s[40..], (uint)dataSize);
    pcm.CopyTo(wav.AsSpan(44));
    return wav;
  }
}
