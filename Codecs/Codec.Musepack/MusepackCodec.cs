#pragma warning disable CS1591

namespace Codec.Musepack;

/// <summary>
/// Stream-level metadata for a Musepack stream.
/// </summary>
/// <param name="Channels">Decoded channel count (1 or 2).</param>
/// <param name="SampleRate">Output sample rate in Hz.</param>
/// <param name="SampleCount">Total decoded samples per channel (-1 if unknown).</param>
/// <param name="Version">Stream version (8 for SV8).</param>
/// <param name="MaxBand">Highest coded subband + 1.</param>
/// <param name="MidSideUsed">Whether mid/side stereo coding is enabled.</param>
public sealed record MusepackStreamInfo(
  int Channels, int SampleRate, long SampleCount, int Version, int MaxBand, bool MidSideUsed);

/// <summary>
/// Clean-room Musepack decoder — an MPEG-1 Layer II-derived subband codec with
/// 1152-sample frames over 32 subbands and the same 32-band polyphase synthesis
/// filterbank as MP2. Ported faithfully from FFmpeg's <c>libavcodec/mpc7.c</c> +
/// <c>mpc8.c</c> + <c>mpc.c</c> and <c>libavformat/mpc.c</c> + <c>mpc8.c</c>
/// (LGPL 2.1, © Konstantin Shishkov). Output is interleaved little-endian signed
/// 16-bit PCM.
/// <para>
/// <b>Supported:</b> SV8 (<c>MPCK</c>) mono and stereo, and SV7 (<c>MP+</c>) stereo.
/// Multichannel SV8 (&gt;2) raises <see cref="NotSupportedException"/> with a clear
/// message so callers can fall back to a metadata-only view.
/// </para>
/// </summary>
public static class MusepackCodec {

  private const int SamplesPerBand = MpcTables.SamplesPerBand; // 36
  private const int Bands = MpcTables.Bands;                   // 32

  /// <summary>Decodes a Musepack SV8 stream into interleaved little-endian 16-bit PCM.</summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var data = ReadAll(input);
    if (IsSv7(data)) {
      DecompressSv7(data, output);
      return;
    }

    var (header, firstAudioPos) = ReadHeaderAndLocateAudio(data);
    if (header.Channels > 2)
      throw new NotSupportedException($"Musepack: multichannel SV8 ({header.Channels} channels) is not supported.");

    var decoder = new MpcFrameDecoder(header.Channels, header.MaxBand, header.MidSideUsed, header.FramesPerPacket);

    var pos = firstAudioPos;
    long samplesWritten = 0;
    var totalSamples = header.SampleCount;
    while (pos < data.Length) {
      MpcContainer.Chunk chunk;
      try {
        chunk = MpcContainer.ReadChunkHeader(data, ref pos);
      } catch (InvalidDataException) {
        break; // tolerate truncation: stop at the first malformed trailing chunk
      }

      if (chunk.Tag == "SE")
        break;

      if (chunk.Tag == "AP") {
        // Each AP chunk holds c->frames sub-frames of MPC_FRAME_SIZE samples,
        // packed contiguously and bit-aligned end-to-end.
        var reader = new MpcBitReader(data, chunk.PayloadStart, chunk.PayloadLength);
        for (var f = 0; f < decoder.FramesPerPacket; ++f) {
          if (reader.BitsLeft <= 0)
            break;
          var pcm = decoder.DecodeFrame(reader, f == 0);
          samplesWritten += WriteClipped(output, pcm, header.Channels, totalSamples, samplesWritten);
          if (totalSamples >= 0 && samplesWritten >= totalSamples)
            return;
        }
      }

      pos = chunk.PayloadStart + chunk.PayloadLength;
    }
  }

  /// <summary>Reads stream-level info from the SV8 stream header.</summary>
  public static MusepackStreamInfo ReadStreamInfo(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var data = ReadAll(input);
    if (IsSv7(data)) {
      var sv7 = Mpc7Container.ReadHeader(data, out _);
      // SV7 sample count is per-channel: full frames minus the final-frame trim.
      var samples = sv7.FrameCount == 0 ? -1L
        : (long)(sv7.FrameCount - 1) * MpcTables.FrameSize + sv7.LastFrameLen;
      return new MusepackStreamInfo(
        2, sv7.SampleRate, samples, sv7.Version, sv7.MaxBands + 1, sv7.MidSideUsed);
    }
    var (header, _) = ReadHeaderAndLocateAudio(data);
    return new MusepackStreamInfo(
      header.Channels, header.SampleRate, header.SampleCount,
      header.Version, header.MaxBand, header.MidSideUsed);
  }

  // -- SV7 decode -------------------------------------------------------------

  private static void DecompressSv7(byte[] data, Stream output) {
    var header = Mpc7Container.ReadHeader(data, out var audioStart);
    var decoder = new Mpc7FrameDecoder(header.MaxBands, header.MidSideUsed);

    long samplesWritten = 0;
    foreach (var frame in Mpc7Container.EnumerateFrames(data, audioStart, header.FrameCount)) {
      // Byte-swap the frame's bytes per 32-bit word, then skip the leading bits.
      var swapped = Mpc7Container.ByteSwapWords(data.AsSpan(frame.Offset, frame.Size), frame.Size);
      var gb = new MpcBitReader(swapped, 0, frame.Size);
      gb.SkipBits(frame.Skip);

      short[][] pcm;
      try {
        pcm = decoder.DecodeFrame(gb);
      } catch (InvalidDataException) {
        break; // tolerate truncation / corruption: keep what decoded cleanly
      }

      // The final frame carries only LastFrameLen samples per channel.
      var emit = frame.LastFrame && header.LastFrameLen > 0
        ? Math.Min(header.LastFrameLen, MpcTables.FrameSize)
        : MpcTables.FrameSize;
      samplesWritten += WriteFrame(output, pcm, emit);
    }
    _ = samplesWritten;
  }

  private static int WriteFrame(Stream output, short[][] perChannelPcm, int emit) {
    var bytes = new byte[emit * 2 * 2]; // 2 channels, int16
    var bi = 0;
    for (var s = 0; s < emit; ++s)
      for (var ch = 0; ch < 2; ++ch) {
        var v = perChannelPcm[ch][s];
        bytes[bi++] = (byte)(v & 0xFF);
        bytes[bi++] = (byte)((v >> 8) & 0xFF);
      }
    output.Write(bytes, 0, bytes.Length);
    return emit;
  }

  // -- header / container plumbing -------------------------------------------

  private static (MpcContainer.StreamHeader Header, int FirstAudioPos) ReadHeaderAndLocateAudio(byte[] data) {
    if (data.Length < 4 || !data.AsSpan(0, 4).SequenceEqual(MpcContainer.MagicSv8))
      throw new InvalidDataException("Musepack: not an SV8 (MPCK) stream.");

    var pos = 4;
    MpcContainer.StreamHeader? header = null;

    while (pos < data.Length) {
      var chunk = MpcContainer.ReadChunkHeader(data, ref pos);
      if (chunk.Tag == "SH") {
        header = MpcContainer.ParseStreamHeader(data, chunk);
        pos = chunk.PayloadStart + chunk.PayloadLength;
        return (header, pos);
      }
      pos = chunk.PayloadStart + chunk.PayloadLength;
    }

    throw new InvalidDataException("Musepack: stream header (SH) not found.");
  }

  private static bool IsSv7(byte[] data)
    => data.Length >= 4 && data.AsSpan(0, 3).SequenceEqual(MpcContainer.MagicSv7)
       && data[3] == Mpc7Container.Sv7Version;

  private static byte[] ReadAll(Stream input) {
    if (input is MemoryStream ms && ms.TryGetBuffer(out var seg)) {
      var copy = new byte[seg.Count];
      Array.Copy(seg.Array!, seg.Offset, copy, 0, seg.Count);
      return copy;
    }
    using var tmp = new MemoryStream();
    input.CopyTo(tmp);
    return tmp.ToArray();
  }

  // Writes the frame's interleaved PCM, honouring the declared per-channel sample
  // total so trailing padding samples in the final frame are dropped. Returns the
  // per-channel sample count actually emitted.
  private static long WriteClipped(Stream output, short[][] perChannelPcm, int channels, long totalSamples, long already) {
    var frameSamples = perChannelPcm[0].Length;
    var emit = frameSamples;
    if (totalSamples >= 0) {
      var remaining = totalSamples - already;
      if (remaining < emit)
        emit = (int)Math.Max(0, remaining);
    }

    var bytes = new byte[emit * channels * 2];
    var bi = 0;
    for (var s = 0; s < emit; ++s)
      for (var ch = 0; ch < channels; ++ch) {
        var v = perChannelPcm[ch][s];
        bytes[bi++] = (byte)(v & 0xFF);
        bytes[bi++] = (byte)((v >> 8) & 0xFF);
      }
    output.Write(bytes, 0, bytes.Length);
    return emit;
  }
}
