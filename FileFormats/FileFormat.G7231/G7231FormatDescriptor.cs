#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.G7231;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.G7231;

/// <summary>
/// Raw ITU-T G.723.1 (dual-rate 5.3 / 6.3 kbit/s) speech container: a headerless stream of
/// variable-size frames whose first byte's low two bits select the frame type and size (24 / 20 /
/// 4 / 1 bytes). Like raw LPC-10, CVSD and G.711 there is no magic and no embedded sample rate or
/// channel count, so dispatch is extension-only. G.723.1 is defined at mono 8000 Hz; that
/// assumption is documented in the surfaced <c>metadata.ini</c>.
/// <para>The archive view surfaces <c>FULL.g723</c> (the byte-exact coded stream, Kind
/// <c>Container</c>), <c>MONO.wav</c> (the whole payload decoded to 16-bit LE PCM @ 8000 Hz, Kind
/// <c>Channel</c>) and <c>metadata.ini</c> (per-type frame counts and duration, Kind <c>Tag</c>).
/// The format is <b>read-only</b>: G.723.1 has no encoder (neither does FFmpeg), so there is no
/// create path.</para>
/// </summary>
public sealed class G7231FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  /// <summary>The G.723.1 sample rate (8 kHz mono).</summary>
  private const int DefaultSampleRate = 8000;

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "G7231";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "ITU-T G.723.1";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Audio;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".g723";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".g723", ".g7231"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  // Headerless: no magic — dispatch is extension-only (precedent: raw LPC-10 / CVSD / G.711).
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("g7231", "G.723.1")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Raw ITU-T G.723.1 (5.3/6.3 kbit/s) speech stream; full file + decoded mono PCM WAV.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.g723", "Container", blob, "g7231"),
    };

    var linear = G7231Codec.Decode(blob);
    var wavBlob = PcmCodec.ToWavBlob(
      ShortsToLePcm(linear), channels: 1, DefaultSampleRate, bitsPerSample: 16, formatCode: 1);
    entries.Add(new("MONO.wav", "Channel", wavBlob, "pcm"));

    var infos = G7231Codec.ReadInfo(blob);
    var active = 0;
    var sid = 0;
    var untransmitted = 0;
    foreach (var info in infos)
      switch (info.Type) {
        case G7231FrameType.Active or G7231FrameType.Active5300: ++active; break;
        case G7231FrameType.Sid: ++sid; break;
        case G7231FrameType.Untransmitted: ++untransmitted; break;
      }

    var durationSeconds = infos.Count * (double)G7231Codec.SamplesPerFrame / DefaultSampleRate;

    var meta = new StringBuilder();
    meta.AppendLine("codec=ITU-T G.723.1 (dual-rate 5.3/6.3 kbit/s)");
    meta.AppendLine("channels=1");
    meta.AppendLine($"sample_rate={DefaultSampleRate}");
    meta.AppendLine("frame_samples=240");
    meta.AppendLine($"frames={infos.Count}");
    meta.AppendLine($"frames_active={active}");
    meta.AppendLine($"frames_sid={sid}");
    meta.AppendLine($"frames_untransmitted={untransmitted}");
    meta.AppendLine($"duration_seconds={durationSeconds:0.###}");
    meta.AppendLine("note=headerless raw stream; mono 8000 Hz assumed per the G.723.1 default.");
    meta.AppendLine("note=decode-only; G.723.1 has no encoder.");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));

    return entries;
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }
}
