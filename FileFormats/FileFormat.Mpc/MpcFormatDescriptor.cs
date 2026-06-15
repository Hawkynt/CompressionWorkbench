#pragma warning disable CS1591
using System.Text;
using Codec.Musepack;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Mpc;

/// <summary>
/// Exposes a Musepack (<c>.mpc</c>/<c>.mpp</c>/<c>.mp+</c>) file as a pseudo-archive
/// of <c>FULL.mpc</c> (Kind <c>Container</c>) plus, when the bitstream decodes,
/// one mono WAV per channel (Kind <c>Channel</c>, named via <see cref="ChannelLayout"/>)
/// and a <c>metadata.ini</c> (Kind <c>Tag</c>). The decoder targets SV8 (<c>MPCK</c>)
/// mono/stereo and SV7 (<c>MP+</c>) stereo; any undecodable input degrades gracefully
/// to a FULL + metadata-only listing. READ-ONLY (no Musepack encoder).
/// </summary>
public sealed class MpcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints {

  public string Id => "Mpc";
  public string DisplayName => "Musepack (MPC)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mpc";
  public IReadOnlyList<string> Extensions => [".mpc", ".mpp", ".mp+"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("MPCK"u8.ToArray(), Confidence: 0.95),
    new("MP+"u8.ToArray(), Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("mpc", "Musepack")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Musepack (MPC) audio; full file + decoded per-channel PCM (SV8).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveWriteConstraints (no Musepack encoder; archive-view inputs only) ──

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "Musepack archive accepts: FULL.mpc, LEFT/RIGHT/… .wav (per-channel), metadata.ini";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.mpc" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not a Musepack-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── Shared archive-entry builder ─────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.mpc", "Container", blob, "mpc"),
    };

    // Best-effort decode-and-split. Anything the decoder can't handle (multichannel
    // SV8, corrupt input) raises NotSupportedException / InvalidDataException; in every
    // failure case the FULL container is still surfaced, with a metadata note when the
    // stream header could at least be read.
    MusepackStreamInfo? info = null;
    try {
      using var probe = new MemoryStream(blob, writable: false);
      info = MusepackCodec.ReadStreamInfo(probe);
    } catch (Exception) {
      // header unreadable (e.g. SV7) → FULL-only
    }

    if (info != null) {
      try {
        using var src = new MemoryStream(blob, writable: false);
        using var pcm = new MemoryStream();
        MusepackCodec.Decompress(src, pcm);
        var pcmBytes = pcm.ToArray();

        const int bitsPerSample = 16;
        if (info.Channels <= 1) {
          entries.Add(new("MONO.wav", "Channel",
            PcmCodec.ToWavBlob(pcmBytes, 1, info.SampleRate, bitsPerSample, formatCode: 1), "pcm"));
        } else {
          foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(
              pcmBytes, info.Channels, info.SampleRate, bitsPerSample))
            entries.Add(new($"{name}.wav", "Channel", wav, "pcm"));
        }
      } catch (Exception) {
        // decode failed after a readable header → keep FULL + metadata only
      }

      entries.Add(new("metadata.ini", "Tag", BuildMetadata(info), "stored"));
    }

    return entries;
  }

  private static byte[] BuildMetadata(MusepackStreamInfo info) {
    var meta = new StringBuilder();
    meta.AppendLine("format=Musepack");
    meta.AppendLine($"version=SV{info.Version}");
    meta.AppendLine($"channels={info.Channels}");
    meta.AppendLine($"sample_rate={info.SampleRate}");
    meta.AppendLine($"samples_per_channel={info.SampleCount}");
    meta.AppendLine($"max_band={info.MaxBand}");
    meta.AppendLine($"mid_side={(info.MidSideUsed ? "1" : "0")}");
    return Encoding.UTF8.GetBytes(meta.ToString());
  }
}
