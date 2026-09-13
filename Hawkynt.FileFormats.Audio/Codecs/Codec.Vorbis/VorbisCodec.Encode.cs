#pragma warning disable CS1591

using OggVorbisEncoder;

namespace Codec.Vorbis;

/// <summary>Parameters for managed Ogg Vorbis I encoding.</summary>
public sealed record VorbisEncoderOptions(
  int SampleRate,
  int Channels,
  float Quality = 0.5f,
  int? SerialNumber = null,
  IReadOnlyDictionary<string, string>? Comments = null
);

/// <summary>
/// Encodes vorbis data.
/// </summary>
public static partial class VorbisEncoder {
  private const int WriteBlock = 1024;

  /// <summary>
  /// Encodes interleaved PCM16 to a complete Ogg Vorbis I stream. Quality is the libvorbis-style
  /// VBR quality scalar accepted by the managed encoder (-0.1 through 1.0). Mono, stereo and
  /// multichannel configurations supported by the underlying setup templates are passed through.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> interleaved, VorbisEncoderOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Channels < 1)
      throw new ArgumentOutOfRangeException(nameof(options), "Vorbis requires at least one channel.");
    if (options.SampleRate < 1)
      throw new ArgumentOutOfRangeException(nameof(options), "Sample rate must be positive.");
    if (options.Quality is < -0.1f or > 1.0f)
      throw new ArgumentOutOfRangeException(nameof(options), "Vorbis VBR quality must be between -0.1 and 1.0.");
    if (interleaved.Length % options.Channels != 0)
      throw new ArgumentException("Interleaved sample count must be a multiple of the channel count.", nameof(interleaved));

    var frames = interleaved.Length / options.Channels;
    var planar = new float[options.Channels][];
    for (var c = 0; c < options.Channels; ++c)
      planar[c] = new float[frames];
    for (var frame = 0; frame < frames; ++frame)
      for (var c = 0; c < options.Channels; ++c)
        planar[c][frame] = interleaved[frame * options.Channels + c] / 32768f;

    var info = VorbisInfo.InitVariableBitRate(options.Channels, options.SampleRate, options.Quality);
    var serial = options.SerialNumber ?? unchecked((int)0x43574256); // "CWBV", deterministic by default
    var ogg = new OggStream(serial);
    var comments = new Comments();
    if (options.Comments != null)
      foreach (var (key, value) in options.Comments)
        comments.AddTag(key, value);

    ogg.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
    ogg.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(comments));
    ogg.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));

    using var output = new MemoryStream();
    FlushPages(ogg, output, force: true);

    var state = ProcessingState.Create(info);
    for (var offset = 0; offset < frames; offset += WriteBlock) {
      var count = Math.Min(WriteBlock, frames - offset);
      state.WriteData(planar, count, offset);
      DrainPackets(state, ogg, output);
    }

    state.WriteEndOfStream();
    DrainPackets(state, ogg, output);
    FlushPages(ogg, output, force: true);
    return output.ToArray();
  }

  private static void DrainPackets(ProcessingState state, OggStream ogg, Stream output) {
    while (!ogg.Finished && state.PacketOut(out OggPacket packet)) {
      ogg.PacketIn(packet);
      FlushPages(ogg, output, force: false);
    }
  }

  private static void FlushPages(OggStream ogg, Stream output, bool force) {
    while (ogg.PageOut(out OggPage page, force)) {
      output.Write(page.Header, 0, page.Header.Length);
      output.Write(page.Body, 0, page.Body.Length);
    }
  }
}
