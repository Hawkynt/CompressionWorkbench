#pragma warning disable CS1591
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Rf64;

/// <summary>
/// Exposes an RF64 / BWF (EBU 3306, Broadcast Wave) file as a channel-extractable
/// pseudo-archive: a <c>FULL.rf64</c> blob (Kind <c>Track</c>), one mono WAV per
/// decoded channel (Kind <c>Channel</c>), and any ancillary chunks such as
/// <c>bext</c> or <c>LIST</c>/<c>INFO</c> (Kind <c>Tag</c>). Targets the
/// <c>RF64</c> magic only — plain <c>RIFF</c> WAV is owned by the Wav descriptor.
/// </summary>
public sealed class Rf64FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Rf64";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "RF64 / BWF (Broadcast Wave)";
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
public string DefaultExtension => ".rf64";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".rf64", ".bwf"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("RF64"u8.ToArray(), Confidence: 0.95),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
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
public string Description => "RF64/BWF Broadcast Wave; full file + per-channel PCM + RIFF metadata chunks.";

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

  // ── IArchiveCreatable: passthrough FULL.rf64, or remux per-channel WAVs into one RF64 ──

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    // If FULL.rf64 is provided, pass it through verbatim (archive-view semantics).
    var full = fileList.FirstOrDefault(f =>
      System.IO.Path.GetFileName(f.Name).Equals("FULL.rf64", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    // Otherwise gather per-channel mono WAVs and interleave them into a new RF64.
    var channelBlobs = fileList
      .Where(f => {
        var name = System.IO.Path.GetFileName(f.Name);
        return name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals("FULL.rf64", StringComparison.OrdinalIgnoreCase);
      })
      .OrderBy(f => ChannelOrder(System.IO.Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("RF64 archive create needs either FULL.rf64 or one or more per-channel WAVs.");

    var channels = new List<WavReader.ParsedWav>();
    foreach (var (_, data) in channelBlobs) channels.Add(new WavReader().Read(data));

    var first = channels[0];
    if (channels.Any(c => c.SampleRate != first.SampleRate || c.BitsPerSample != first.BitsPerSample || c.NumChannels != 1))
      throw new InvalidOperationException("All channel WAVs must be mono and share sample rate + bit depth.");

    var bytesPerSample = first.BitsPerSample / 8;
    var frameCount = first.InterleavedPcm.Length / bytesPerSample;
    if (channels.Any(c => c.InterleavedPcm.Length / bytesPerSample != frameCount))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");

    var interleaved = PcmCodec.Interleave(channels.Select(c => c.InterleavedPcm).ToList(), first.BitsPerSample);

    var blob = Rf64Writer.Build(
      interleaved, channels.Count, first.SampleRate, first.BitsPerSample, formatCode: 1, bext: null);
    output.Write(blob);
  }

  // Canonical speaker ordering (FFmpeg/WAVE bit order, mono through 22.2).
  private static int ChannelOrder(string name) => ChannelLayout.OrderIndex(name);

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
public string AcceptedInputsDescription =>
    "RF64 archive accepts: FULL.rf64, LEFT/RIGHT/CENTER/… .wav (per-channel), metadata/*.bin";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = System.IO.Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    var dir = System.IO.Path.GetDirectoryName(input.ArchiveName)?.Replace('\\', '/').ToLowerInvariant() ?? "";

    if (dir == "" && name.Equals("full.rf64", StringComparison.Ordinal)) { reason = null; return true; }
    if (dir == "" && name.EndsWith(".wav")) { reason = null; return true; }
    if (dir == "metadata" && name.EndsWith(".bin")) { reason = null; return true; }
    reason = $"not an RF64-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = new Rf64Reader().Read(blob);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.rf64", "Container", blob),
    };

    // Split integer PCM (format code 1) per-channel; other formats are surfaced as FULL only.
    if (parsed.FormatCode == 1 && parsed.BitsPerSample is 8 or 16 or 24 or 32 && parsed.NumChannels > 1) {
      foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(
          parsed.InterleavedPcm, parsed.NumChannels, parsed.SampleRate, parsed.BitsPerSample,
          parsed.ChannelMask))
        entries.Add(new($"{name}.wav", "Channel", wavBlob, "pcm"));
    } else if (parsed.FormatCode == 1 && parsed.BitsPerSample is 8 or 16 or 24 or 32 && parsed.NumChannels == 1) {
      entries.Add(new("MONO.wav", "Channel",
        PcmCodec.ToWavBlob(parsed.InterleavedPcm, channels: 1, parsed.SampleRate, parsed.BitsPerSample, formatCode: 1), "pcm"));
    }

    foreach (var (id, data) in parsed.MetadataChunks)
      entries.Add(new($"metadata/{id.Trim()}.bin", "Tag", data));

    return entries;
  }
}
