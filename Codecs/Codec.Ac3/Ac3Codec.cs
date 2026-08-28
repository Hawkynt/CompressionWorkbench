#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>
/// Stream-level metadata extracted from an AC-3 / E-AC-3 stream's first sync frame.
/// </summary>
public sealed record Ac3StreamInfo(
  int SampleRate, int Channels, int Bitrate, int Acmod, bool Lfe, bool IsEnhanced, long DurationSamples);

/// <summary>
/// Managed AC-3 / E-AC-3 codec. Legacy AC-3 encoding is implemented in the companion partial
/// source file. Decoding supports legacy AC-3 sync frames (bsid ≤ 10) plus independent E-AC-3
/// substreams (bsid 11..16) to interleaved little-endian signed 16-bit PCM at the native channel
/// count, with LFE last when present.
/// </summary>
public static partial class Ac3Codec {

  private const int BlocksPerFrame = 6;
  private const int SamplesPerBlock = 256;

  /// <summary>Reads stream-level info (sample rate, native channel count, bitrate, duration) from the first sync frame.</summary>
  public static Ac3StreamInfo ReadStreamInfo(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var data = ReadAll(input);
    var offset = FindSync(data, 0);
    if (offset < 0 || Ac3FrameHeader.TryParse(data, offset) is not { } h)
      throw new InvalidDataException("AC-3 stream contains no parseable sync frame.");

    var channels = Ac3FrameHeader.AcmodChannelCount(h.Acmod) + (h.LowFrequencyEffects ? 1 : 0);

    long sampleFrames = 0;
    var pos = offset;
    while (pos + 6 <= data.Length && Ac3FrameHeader.TryParse(data, pos) is { } fh && fh.FrameSize > 0) {
      if (!fh.IsDependentSubstream && !(fh.IsEnhanced && fh.SubstreamId != 0))
        sampleFrames += fh.IsEnhanced ? fh.NumBlocks : BlocksPerFrame;
      pos += fh.FrameSize;
    }
    var duration = sampleFrames * SamplesPerBlock;

    return new Ac3StreamInfo(h.SampleRate, channels, h.Bitrate, h.Acmod, h.LowFrequencyEffects, h.IsEnhanced, duration);
  }

  /// <summary>
  /// Decodes an AC-3 / E-AC-3 stream into raw interleaved little-endian signed 16-bit PCM on
  /// <paramref name="output"/>. Channels are emitted in acmod order with LFE last. AC-3 (bsid ≤ 10)
  /// and E-AC-3 independent substreams (bsid 11..16, frame type 0/2) decode; E-AC-3 dependent
  /// substreams (frame type 1) are skipped.
  /// </summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    var data = ReadAll(input);

    var pos = FindSync(data, 0);
    if (pos < 0)
      throw new InvalidDataException("AC-3 stream contains no parseable sync frame.");

    var decoder = new FrameDecoder();
    var eacDecoder = new Ac3EnhancedFrameDecoder();
    while (pos + 6 <= data.Length) {
      if (Ac3FrameHeader.TryParse(data, pos) is not { } header || header.FrameSize <= 0)
        break;
      if (pos + header.FrameSize > data.Length)
        break;

      if (header.IsDependentSubstream || (header.IsEnhanced && header.SubstreamId != 0)) {
        pos += header.FrameSize;
        continue;
      }

      var pcm = header.IsEnhanced
        ? eacDecoder.DecodeFrame(data, pos, header)
        : decoder.DecodeFrame(data, pos, header);
      if (pcm == null)
        break;
      output.Write(pcm, 0, pcm.Length);
      pos += header.FrameSize;
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
    for (var i = start; i + 1 < data.Length; ++i)
      if (data[i] == 0x0B && data[i + 1] == 0x77 && Ac3FrameHeader.TryParse(data, i) is not null)
        return i;
    return -1;
  }
}
