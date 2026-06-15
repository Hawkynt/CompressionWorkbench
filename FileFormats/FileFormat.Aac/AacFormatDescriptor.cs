#pragma warning disable CS1591
using Codec.Aac;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Aac;

/// <summary>
/// Exposes an AAC (ADTS-framed) audio file as a pseudo-archive of <c>FULL.aac</c>
/// (Kind <c>Track</c>) plus, when the bitstream can be decoded, one mono WAV per
/// channel (<c>LEFT.wav</c>/<c>RIGHT.wav</c>/<c>MONO.wav</c>, Kind <c>Channel</c>).
/// The decoder targets AAC-LC; for inputs it can't handle (Main/SSR/LTP/HE-AAC,
/// or the not-yet-implemented spectral pipeline) the descriptor falls back to a
/// FULL-only listing rather than failing.
/// </summary>
public sealed class AacFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints {

  public string Id => "Aac";
  public string DisplayName => "AAC (ADTS)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".aac";
  public IReadOnlyList<string> Extensions => [".aac"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // ADTS sync is a 12-bit 0xFFF word; the most common variant is MPEG-4, no CRC
  // (0xFFF1). Moderate confidence keeps false positives low for arbitrary streams.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xFF, 0xF1], Confidence: 0.40),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("aac", "AAC")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "AAC (ADTS) audio; full file + decoded per-channel PCM (AAC-LC).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveWriteConstraints (no AAC encoder; archive-view inputs only) ──────

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "AAC archive accepts: FULL.aac, LEFT/RIGHT/… .wav (per-channel)";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name == "full.aac" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not an AAC-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── Shared archive-entry builder ─────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.aac", "Container", blob, "aac"),
    };

    // Best-effort decode-and-split. The decoder targets AAC-LC and throws
    // NotSupportedException for other profiles (and InvalidDataException for
    // malformed input); in every failure case we keep the FULL-only listing.
    try {
      using var probe = new MemoryStream(blob, writable: false);
      var info = AacCodec.ReadStreamInfo(probe);

      using var src = new MemoryStream(blob, writable: false);
      using var pcm = new MemoryStream();
      AacCodec.Decompress(src, pcm);
      var pcmBytes = pcm.ToArray();

      // The decoded PCM is the AAC-LC core band, so the WAV must carry the CORE
      // sample rate to play at the right speed. For HE-AAC streams info.SampleRate
      // is the SBR-doubled effective rate (surfaced as metadata); SBR audio
      // reconstruction is gated, so we deliberately do not retime the core PCM.
      using var rateProbe = new MemoryStream(blob, writable: false);
      var coreRate = AacCodec.ReadCoreSampleRate(rateProbe);

      const int bitsPerSample = 16;
      if (info.Channels <= 1) {
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(pcmBytes, 1, coreRate, bitsPerSample, formatCode: 1), "pcm"));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(
            pcmBytes, info.Channels, coreRate, bitsPerSample))
          entries.Add(new($"{name}.wav", "Channel", wav, "pcm"));
      }
    } catch (Exception) {
      // Graceful fallback: surface the original AAC file only.
    }

    return entries;
  }
}
