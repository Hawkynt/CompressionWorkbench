#pragma warning disable CS1591
using Codec.Opus;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Opus;

/// <summary>
/// Archive-shaped view of an Opus file (<c>.opus</c>, Ogg-encapsulated, RFC 7845):
/// a <c>FULL.opus</c> blob plus one mono WAV per decoded channel
/// (<c>LEFT.wav</c>/<c>RIGHT.wav</c>/... or <c>MONO.wav</c>) so multi-channel Opus
/// files can be decomposed in the archive browser. Decoding goes through the
/// in-repo <see cref="OpusCodec"/>; when that codec cannot handle the input
/// (e.g. hybrid-mode configs, which throw <see cref="NotSupportedException"/>,
/// or a malformed stream), the view degrades gracefully to <c>FULL.opus</c> only.
/// </summary>
public sealed class OpusFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Opus";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Opus (Ogg)";
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
public string DefaultExtension => ".opus";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".opus"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  // Empty — Opus is Ogg-framed ("OggS"), so the generic Ogg descriptor owns that
  // magic. This descriptor is reached only via explicit registry lookup
  // (e.g. `cwb list --format Opus`), exactly like FlacArchiveDescriptor.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("opus", "Opus")];
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
public string Description => "Opus audio (Ogg-encapsulated); full file + decoded per-channel PCM.";

  // ── IArchiveFormatOperations ─────────────────────────────────────────

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

  // ── IArchiveInMemoryExtract ──────────────────────────────────────────

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveWriteConstraints ─────────────────────────────────────────
  // There is no Opus encoder in-repo, so this descriptor is read-only
  // (no IArchiveCreatable); the constraints only describe the entry shape.

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
public string AcceptedInputsDescription =>
    "Opus archive accepts: FULL.opus, LEFT/RIGHT/CENTER/… .wav (per-channel)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.opus" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not an Opus-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── Shared archive-entry builder ─────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.opus", "Container", blob, "opus"),
    };

    // Decode to 16-bit LE interleaved PCM and split per channel. The decoder
    // throws NotSupportedException for unsupported configs (hybrid mode) and
    // InvalidDataException for malformed input; either way we keep FULL-only.
    try {
      OpusStreamInfo info;
      using (var infoStream = new MemoryStream(blob))
        info = OpusCodec.ReadStreamInfo(infoStream);

      byte[] pcm;
      using (var src = new MemoryStream(blob))
      using (var pcmStream = new MemoryStream()) {
        OpusCodec.Decompress(src, pcmStream);
        pcm = pcmStream.ToArray();
      }

      var sampleRate = info.SampleRate > 0 ? info.SampleRate : 48000;
      const int bitsPerSample = 16;

      if (info.Channels == 1) {
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(pcm, 1, sampleRate, bitsPerSample, formatCode: 1), "pcm"));
      } else if (info.Channels > 1) {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(
            pcm, info.Channels, sampleRate, bitsPerSample))
          entries.Add(new($"{name}.wav", "Channel", wav, "pcm"));
      }
    } catch (Exception) {
      // Graceful fallback: surface FULL.opus only.
    }

    return entries;
  }
}
