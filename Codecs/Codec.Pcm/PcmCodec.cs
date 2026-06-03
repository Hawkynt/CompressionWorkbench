namespace Codec.Pcm;

/// <summary>
/// PCM codec: integer/float sample packing, channel interleave/deinterleave, and
/// canonical RIFF/WAVE header framing. Used by audio-container descriptors (WAV,
/// FLAC-archive, future Opus/Vorbis) that surface per-channel mono WAVs as archive
/// entries.
/// </summary>
public static class PcmCodec {

  /// <summary>
  /// Conventional channel names per layout (FFmpeg default layouts, mono → 22.2);
  /// unmapped counts fall back to CH_0..CH_N. See <see cref="ChannelLayout"/>.
  /// </summary>
  public static IReadOnlyList<string> LayoutNames(int channels)
    => ChannelLayout.DefaultNames(channels);

  /// <summary>
  /// Splits interleaved little-endian signed-integer PCM into per-channel mono WAV blobs.
  /// Channels are returned in the order they occur in <paramref name="interleaved"/>.
  /// When the container carries an explicit speaker bitmap (WAVE_FORMAT_EXTENSIBLE
  /// <c>dwChannelMask</c>, CAF channel bitmap), pass it via <paramref name="channelMask"/>
  /// so each mono WAV is named for its real speaker; otherwise the FFmpeg default
  /// layout for the channel count applies.
  /// </summary>
  public static IReadOnlyList<(string Name, byte[] WavBlob)> SplitInterleavedPcm(
      byte[] interleaved, int channels, int sampleRate, int bitsPerSample, ulong? channelMask = null) {
    if (channels <= 1)
      return [("MONO", ToWavBlob(interleaved, channels: 1, sampleRate, bitsPerSample, formatCode: 1))];

    var bytesPerSample = bitsPerSample / 8;
    var frameBytes = bytesPerSample * channels;
    if (interleaved.Length % frameBytes != 0)
      throw new ArgumentException("Interleaved PCM length is not a multiple of frame size.");

    var frameCount = interleaved.Length / frameBytes;
    var names = channelMask is { } mask
      ? ChannelLayout.NamesFromMask(mask, channels)
      : ChannelLayout.DefaultNames(channels);
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
  /// Splits interleaved little-endian IEEE-float PCM into per-channel mono WAV blobs
  /// (RIFF format code 3). Mirrors <see cref="SplitInterleavedPcm"/>'s frame walk but
  /// emits float WAVs; <paramref name="bitsPerSample"/> must be 32 or 64. As with the
  /// integer split, an explicit <paramref name="channelMask"/> (WAVE_FORMAT_EXTENSIBLE
  /// <c>dwChannelMask</c>, CAF channel bitmap) names each mono WAV for its real speaker;
  /// otherwise the FFmpeg default layout for the channel count applies.
  /// </summary>
  public static IReadOnlyList<(string Name, byte[] WavBlob)> SplitInterleavedFloat(
      byte[] interleaved, int channels, int sampleRate, int bitsPerSample, ulong? channelMask = null) {
    if (bitsPerSample is not (32 or 64))
      throw new ArgumentException("Float PCM split requires 32-bit or 64-bit samples.", nameof(bitsPerSample));
    if (channels <= 1)
      return [("MONO", ToWavBlob(interleaved, channels: 1, sampleRate, bitsPerSample, formatCode: 3))];

    var bytesPerSample = bitsPerSample / 8;
    var frameBytes = bytesPerSample * channels;
    if (interleaved.Length % frameBytes != 0)
      throw new ArgumentException("Interleaved float PCM length is not a multiple of frame size.");

    var frameCount = interleaved.Length / frameBytes;
    var names = channelMask is { } mask
      ? ChannelLayout.NamesFromMask(mask, channels)
      : ChannelLayout.DefaultNames(channels);
    var result = new List<(string, byte[])>(channels);

    for (var c = 0; c < channels; ++c) {
      var mono = new byte[frameCount * bytesPerSample];
      for (var f = 0; f < frameCount; ++f) {
        var src = f * frameBytes + c * bytesPerSample;
        var dst = f * bytesPerSample;
        Buffer.BlockCopy(interleaved, src, mono, dst, bytesPerSample);
      }
      result.Add((names[c], ToWavBlob(mono, channels: 1, sampleRate, bitsPerSample, formatCode: 3)));
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
  /// Weaves per-channel mono PCM blobs back into one interleaved buffer — the inverse
  /// of <see cref="SplitInterleavedPcm"/>. All channels must share the same byte length
  /// (i.e. the same frame count at the given <paramref name="bitsPerSample"/>). Channels
  /// are interleaved in the order supplied. A single channel is returned unchanged.
  /// </summary>
  public static byte[] Interleave(IReadOnlyList<byte[]> monoChannels, int bitsPerSample) {
    if (monoChannels.Count == 0) return [];
    if (monoChannels.Count == 1) return monoChannels[0];

    var bytesPerSample = bitsPerSample / 8;
    var monoLength = monoChannels[0].Length;
    if (monoChannels.Any(c => c.Length != monoLength))
      throw new ArgumentException("All channel PCM buffers must have the same length (frame count).");
    if (monoLength % bytesPerSample != 0)
      throw new ArgumentException("Channel PCM length is not a multiple of the sample size.");

    var channels = monoChannels.Count;
    var frameCount = monoLength / bytesPerSample;
    var interleaved = new byte[frameCount * channels * bytesPerSample];
    for (var f = 0; f < frameCount; ++f) {
      var srcOff = f * bytesPerSample;
      for (var c = 0; c < channels; ++c) {
        var dstOff = (f * channels + c) * bytesPerSample;
        Buffer.BlockCopy(monoChannels[c], srcOff, interleaved, dstOff, bytesPerSample);
      }
    }
    return interleaved;
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
