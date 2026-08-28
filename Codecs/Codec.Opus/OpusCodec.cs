#pragma warning disable CS1591

namespace Codec.Opus;

/// <summary>
/// Clean-room Opus decoder. Input: an Ogg Opus stream (RFC 7845) whose packets
/// carry Opus-encoded frames (RFC 6716). Output: interleaved little-endian
/// signed 16-bit PCM at the stream's native sample rate (48 kHz for CELT).
/// <para>
/// <b>Ported from:</b> libopus (Xiph) — BSD 3-clause, commit-agnostic clean-room
/// port tracking the RFC 6716 / RFC 7845 / RFC 8251 bitstream specification.
/// </para>
/// <para>
/// <b>Supported surface (first pass):</b>
/// <list type="bullet">
///   <item>Ogg page walker + <c>OpusHead</c> / <c>OpusTags</c> metadata parsing.</item>
///   <item>TOC byte parsing + all four frame-packing codes (0/1/2/3) per RFC 6716 §3.2.</item>
///   <item>Range decoder (ec_dec) skeleton — reads tell, bits, and cdf symbols.</item>
///   <item>CELT-only configs (16-31) — framing only. Full spectral inverse MDCT
///         is <b>not</b> landed in this first pass and currently emits silence
///         for the expected number of samples so downstream tooling can round-trip
///         file structure and sample counts. Use <see cref="ReadStreamInfo"/> to
///         introspect stream metadata deterministically.</item>
///   <item>SILK-only configs (0-11) — framing only (same silence fallback).</item>
///   <item>Hybrid configs (12-15) — throws <see cref="NotSupportedException"/>.</item>
/// </list>
/// </para>
/// </summary>
public static partial class OpusCodec {

  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var reader = new OggOpusReader(input);
    var head = reader.ReadHead();
    _ = reader.TryReadTags();
    int channels = head.ChannelCount;
    int preSkipRemaining = head.PreSkip;

    while (reader.TryReadPacket(out var packet)) {
      if (packet.Length < 1) continue;
      var toc = OpusPacketReader.ParseToc(packet[0]);
      if (toc.Mode == OpusMode.Hybrid)
        throw new NotSupportedException(
          $"Opus hybrid mode (config {toc.Config}) is not supported in this decoder. " +
          "Only CELT-only (configs 16-31) and SILK-only (configs 0-11) are scaffolded.");

      var frameCount = OpusPacketReader.CountFrames(packet);
      var samplesPerFrame = toc.FrameSamplesAt48k;
      var totalSamples = frameCount * samplesPerFrame;
      EmitSilence(output, totalSamples, channels, ref preSkipRemaining);
    }
  }

  public static OpusStreamInfo ReadStreamInfo(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var reader = new OggOpusReader(input);
    var head = reader.ReadHead();
    var tags = reader.TryReadTags();
    return new OpusStreamInfo(
      SampleRate: 48000,
      Channels: head.ChannelCount,
      PreSkip: head.PreSkip,
      InputSampleRate: (int)head.InputSampleRate,
      Vendor: tags?.Vendor);
  }

  private static void EmitSilence(Stream output, int sampleCount, int channels, ref int preSkipRemaining) {
    var take = sampleCount;
    if (preSkipRemaining > 0) {
      var skip = Math.Min(preSkipRemaining, sampleCount);
      preSkipRemaining -= skip;
      take -= skip;
      if (take <= 0) return;
    }

    var byteCount = take * channels * 2;
    Span<byte> zeros = stackalloc byte[256];
    zeros.Clear();
    while (byteCount > 0) {
      var chunk = Math.Min(byteCount, zeros.Length);
      output.Write(zeros[..chunk]);
      byteCount -= chunk;
    }
  }
}

public sealed record OpusStreamInfo(int SampleRate, int Channels, int PreSkip, int InputSampleRate, string? Vendor);
