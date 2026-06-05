#pragma warning disable CS1591
using System.Text;
using Codec.Bonk;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Bonk;

/// <summary>
/// Exposes a Bonk (<c>.bonk</c>) file as a pseudo-archive of <c>FULL.bonk</c> (Kind
/// <c>Container</c>) plus, when the bitstream decodes, one mono WAV per channel
/// (Kind <c>Channel</c>, named via <see cref="ChannelLayout"/>) and a
/// <c>metadata.ini</c> (Kind <c>Tag</c>). Decode failures degrade gracefully to a
/// FULL-only listing. A Bonk file carries a length-prefixed original filename before
/// its <c>'\0BONK'</c> tag, so the tag is rarely at offset 0; detection therefore
/// leans on the <c>.bonk</c> extension plus a low-confidence offset-0 tag match,
/// while listing scans for the tag wherever it sits.
/// </summary>
public sealed class BonkFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  public string Id => "Bonk";
  public string DisplayName => "Bonk Audio";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".bonk";
  public IReadOnlyList<string> Extensions => [".bonk"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // The '\0BONK' tag follows a variable-length filename, so a fixed-offset magic only
  // matches the (rare) tag-at-offset-0 case; keep it low confidence to avoid clashes.
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x00, (byte)'B', (byte)'O', (byte)'N', (byte)'K'], Offset: 0, Confidence: 0.30)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("bonk", "Bonk")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;
  public string Description => "Bonk lossless/lossy audio; full file + decoded per-channel PCM.";

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
      new("FULL.bonk", "Container", blob, "bonk"),
    };

    try {
      var info = BonkCodec.ReadStreamInfo(blob, out _);
      var pcm = BonkCodec.Decompress(blob);

      if (info.Channels <= 1) {
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(pcm, 1, info.SampleRate, 16, formatCode: 1), "pcm"));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(
            pcm, info.Channels, info.SampleRate, 16))
          entries.Add(new($"{name}.wav", "Channel", wav, "pcm"));
      }

      var meta = new StringBuilder();
      meta.AppendLine("format=Bonk");
      meta.AppendLine($"channels={info.Channels}");
      meta.AppendLine($"sample_rate={info.SampleRate}");
      meta.AppendLine("bits_per_sample=16");
      meta.AppendLine($"samples_per_channel={info.SamplesPerChannel}");
      meta.AppendLine($"lossless={info.Lossless}");
      meta.AppendLine($"mid_side={info.MidSide}");
      meta.AppendLine($"n_taps={info.NTaps}");
      meta.AppendLine($"down_sampling={info.DownSampling}");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString()), "stored"));
    } catch (Exception) {
      // Graceful fallback: surface the original Bonk file only.
    }

    return entries;
  }
}
