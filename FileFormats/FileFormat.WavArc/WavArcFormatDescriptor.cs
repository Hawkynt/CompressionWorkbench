#pragma warning disable CS1591
using System.Text;
using Codec.Pcm;
using Codec.WavArc;
using Compression.Registry;

namespace FileFormat.WavArc;

/// <summary>
/// Exposes a WavArc (<c>.wa</c>) file as a pseudo-archive of <c>FULL.wa</c> (Kind
/// <c>Container</c>) plus, when the bitstream decodes, one mono WAV per channel
/// (Kind <c>Channel</c>, named via <see cref="ChannelLayout"/>) and a
/// <c>metadata.ini</c> (Kind <c>Tag</c>). The <c>0CPY</c> (raw copy) and <c>1DIF</c>
/// (fixed-difference) methods decode byte-exact; the adaptive-LPC methods
/// (<c>2SLP</c>/<c>3NLP</c>/<c>4ALP</c>/<c>5ELP</c>) are not yet verified and fall
/// back to a FULL-only listing rather than emit wrong PCM. The WavArc header places
/// its codec tag after a variable-length filename, so detection is by the <c>.wa</c>
/// extension.
/// </summary>
public sealed class WavArcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  public string Id => "WavArc";
  public string DisplayName => "WavArc";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".wa";
  public IReadOnlyList<string> Extensions => [".wa"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // The codec tag sits after a variable-length filename, so there is no fixed-offset
  // magic — detection is by the .wa extension.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("wavarc", "WavArc")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;
  public string Description => "WavArc lossless audio (.wa); full file + decoded per-channel PCM.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── Shared archive-entry builder ─────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.wa", "Container", blob, "wavarc"),
    };

    try {
      var info = WavArcCodec.ReadStreamInfo(blob, out _);
      var pcm = WavArcCodec.Decompress(blob);

      if (info.Channels <= 1) {
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(pcm, 1, info.SampleRate, info.BitsPerSample, formatCode: 1), "pcm"));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(
            pcm, info.Channels, info.SampleRate, info.BitsPerSample))
          entries.Add(new($"{name}.wav", "Channel", wav, "pcm"));
      }

      var meta = new StringBuilder();
      meta.AppendLine("format=WavArc");
      meta.AppendLine($"method={info.Method}");
      meta.AppendLine($"channels={info.Channels}");
      meta.AppendLine($"sample_rate={info.SampleRate}");
      meta.AppendLine($"bits_per_sample={info.BitsPerSample}");
      meta.AppendLine($"original_filename={info.OriginalFileName}");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString()), "stored"));
    } catch (Exception) {
      // Graceful fallback: surface the original WavArc file only (e.g. adaptive-LPC methods).
    }

    return entries;
  }
}
