#pragma warning disable CS1591

namespace Codec.Dts;

/// <summary>
/// Stream-level metadata extracted from a DTS (Coherent Acoustics) stream's first core frame.
/// </summary>
public sealed record DtsStreamInfo(
  int SampleRate, int Channels, int Bitrate, int Amode, bool Lfe, long DurationSamples);

/// <summary>
/// Clean-room DTS Coherent Acoustics (DCA) core decoder, a faithful port of the FFmpeg reference
/// decoder (<c>libavcodec/dcadec.c</c> + <c>dcadata.c</c> + <c>dcahuff.h</c>). Decodes the core
/// sub-stream of a DTS frame to interleaved little-endian signed 16-bit PCM at the stream's native
/// channel count (the AMODE full-bandwidth channels in document order, with the LFE channel last
/// when present). The full DCA core pipeline is implemented: the primary audio coding header, the
/// per-sub-subframe bit allocation / scale factors / quantized subband samples (Huffman, block-code
/// and plain quantization), inverse ADPCM prediction, high-frequency VQ, the LFE decimation FIR and
/// the 32-band cosine-modulated QMF synthesis.
/// <para>
/// Scope: only the standard 16-bit big-endian framing (sync 0x7FFE8001) is decoded; the 14-bit and
/// byte-swapped framings throw <see cref="NotSupportedException"/>. The DTS-HD extension substreams
/// (XCH / XXCH / X96 / XBR / XLL and the EXSS container) are not decoded — when present the embedded
/// core is still decoded and the extensions are skipped.
/// </para>
/// </summary>
public static class DtsCodec {

  /// <summary>Reads stream-level info (sample rate, native channel count, bitrate, duration) from the first core frame.</summary>
  public static DtsStreamInfo ReadStreamInfo(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var data = ReadAll(input);
    var offset = FindSync(data, 0);
    if (offset < 0 || DtsFrameHeader.TryParse(data, offset) is not { } h)
      throw new InvalidDataException("DTS stream contains no parseable core sync frame.");

    var channels = DtsFrameHeader.AmodeChannelCount(h.Amode) + (h.Lfe > 0 ? 1 : 0);

    long frames = 0;
    long totalSamples = 0;
    var pos = offset;
    while (pos + 14 <= data.Length && DtsFrameHeader.TryParse(data, pos) is { } fh && fh.FrameSize > 0) {
      ++frames;
      totalSamples += (long)fh.SampleBlocks * 32;
      pos += fh.FrameSize;
    }

    return new DtsStreamInfo(h.SampleRate, channels, h.BitRate, h.Amode, h.Lfe > 0, totalSamples);
  }

  /// <summary>
  /// Decodes a DTS stream into raw interleaved little-endian signed 16-bit PCM on
  /// <paramref name="output"/>. Channels are emitted in AMODE document order with LFE last.
  /// Throws <see cref="NotSupportedException"/> for the unsupported 14-bit / LE framings.
  /// </summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    var data = ReadAll(input);

    RejectUnsupportedFraming(data);

    var pos = FindSync(data, 0);
    if (pos < 0)
      throw new InvalidDataException("DTS stream contains no parseable core sync frame.");

    var decoder = new DtsFrameDecoder();
    while (pos + 14 <= data.Length) {
      if (DtsFrameHeader.TryParse(data, pos) is not { } header || header.FrameSize <= 0)
        break;
      if (pos + header.FrameSize > data.Length)
        break;                                  // truncated trailing frame

      var pcm = decoder.DecodeFrame(data, pos, header, out var channels);
      if (pcm == null || channels <= 0)
        break;                                  // undecodable frame — stop gracefully

      WriteInterleaved(output, pcm, channels);
      pos += header.FrameSize;
    }
  }

  /// <summary>Decodes the first frame to per-channel float PCM (used by tests / channel split); null when undecodable.</summary>
  internal static float[][]? DecodeFirstFrame(byte[] data, out int channels) {
    channels = 0;
    var pos = FindSync(data, 0);
    if (pos < 0 || DtsFrameHeader.TryParse(data, pos) is not { } header)
      return null;
    return new DtsFrameDecoder().DecodeFrame(data, pos, header, out channels);
  }

  // -- helpers ---------------------------------------------------------------

  private static void WriteInterleaved(Stream output, float[][] pcm, int channels) {
    if (channels == 0 || pcm[0].Length == 0)
      return;
    var samples = pcm[0].Length;
    var bytes = new byte[samples * channels * 2];
    var p = 0;
    for (var n = 0; n < samples; ++n) {
      for (var c = 0; c < channels; ++c) {
        var s = (int)Math.Round(pcm[c][n] * 32768f);
        s = Math.Clamp(s, short.MinValue, short.MaxValue);
        bytes[p++] = (byte)(s & 0xFF);
        bytes[p++] = (byte)((s >> 8) & 0xFF);
      }
    }
    output.Write(bytes, 0, bytes.Length);
  }

  private static void RejectUnsupportedFraming(byte[] data) {
    // 14-bit packed (0x1FFFE800) and byte-swapped LE (0xFE7F0180) core framings are out of scope.
    for (var i = 0; i + 4 <= data.Length && i < 1 << 20; ++i) {
      // 16-bit BE core sync is handled — bail out of the scan as soon as we find it.
      if (data[i] == 0x7F && data[i + 1] == 0xFE && data[i + 2] == 0x80 && data[i + 3] == 0x01)
        return;
      var le = data[i] == 0xFE && data[i + 1] == 0x7F && data[i + 2] == 0x01 && data[i + 3] == 0x80;
      var b14 = data[i] == 0x1F && data[i + 1] == 0xFF && data[i + 2] == 0xE8 && data[i + 3] == 0x00;
      if (le || b14)
        throw new NotSupportedException("Only 16-bit big-endian DTS core framing (0x7FFE8001) is supported.");
    }
  }

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

  private static int FindSync(byte[] data, int start) {
    for (var i = start; i + 4 <= data.Length; ++i)
      if (data[i] == 0x7F && data[i + 1] == 0xFE && data[i + 2] == 0x80 && data[i + 3] == 0x01
          && DtsFrameHeader.TryParse(data, i) is not null)
        return i;
    return -1;
  }
}
