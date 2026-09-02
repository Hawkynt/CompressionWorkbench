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

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Aac";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "AAC (ADTS)";
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
  public string DefaultExtension => ".aac";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".aac"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  // ADTS sync is a 12-bit 0xFFF word; the most common variant is MPEG-4, no CRC
  // (0xFFF1). Moderate confidence keeps false positives low for arbitrary streams.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xFF, 0xF1], Confidence: 0.40),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("aac", "AAC")];
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
  public string Description => "AAC (ADTS) audio; full file + decoded per-channel PCM (AAC-LC).";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    AudioPseudoArchive.List(BuildEntries(stream));

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveWriteConstraints (no AAC encoder; archive-view inputs only) ──────

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    "AAC archive accepts: FULL.aac, LEFT/RIGHT/… .wav (per-channel)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
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
