#pragma warning disable CS1591
using System.Text;
using Codec.Pcm;
using Codec.Qoa;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Qoa;

/// <summary>
/// Exposes a Quite OK Audio (<c>.qoa</c>) file as a pseudo-archive of
/// <c>FULL.qoa</c> (Kind <c>Container</c>) plus, when the bitstream decodes, one
/// mono WAV per channel (Kind <c>Channel</c>, named via <see cref="ChannelLayout"/>)
/// and a <c>metadata.ini</c> (Kind <c>Tag</c>). Decode failures degrade gracefully
/// to a FULL-only listing. The descriptor is also creatable (WORM): it passes a
/// supplied <c>FULL.qoa</c> through unchanged or interleaves per-channel mono WAVs
/// and encodes them with <see cref="QoaCodec"/>.
/// </summary>
public sealed class QoaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Qoa";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Quite OK Audio (QOA)";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Audio;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".qoa";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".qoa"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [new("qoaf"u8.ToArray(), Confidence: 0.95)];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("qoa", "QOA")];
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Entropy;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Quite OK Audio (QOA); full file + decoded per-channel PCM.";

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

  // ── IArchiveWriteConstraints ────────────────────────────────────────────────

    /// <summary>
  /// Gets the max total archive size.
  /// </summary>
public long? MaxTotalArchiveSize => null;
    /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
public string AcceptedInputsDescription =>
    "QOA archive accepts: FULL.qoa, LEFT/RIGHT/… .wav (per-channel), metadata.ini";

    /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.qoa" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not a QOA-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── IArchiveCreatable: pass through FULL.qoa or assemble from per-channel WAVs ──

    /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.qoa", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(f => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("QOA archive create needs either FULL.qoa or one or more per-channel WAVs.");

    var channels = channelBlobs.Select(b => new WavReader().Read(b.Data)).ToList();
    var first = channels[0];
    if (channels.Any(c => c.SampleRate != first.SampleRate || c.NumChannels != 1))
      throw new InvalidOperationException("All channel WAVs must be mono and share the sample rate.");
    if (first.BitsPerSample != 16)
      throw new InvalidOperationException("QOA create requires 16-bit PCM channel WAVs.");
    if (channels.Any(c => c.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");

    var interleaved = PcmCodec.Interleave(channels.Select(c => c.InterleavedPcm).ToList(), first.BitsPerSample);

    using var pcm = new MemoryStream(interleaved);
    QoaCodec.Compress(pcm, output, channels.Count, first.SampleRate);
  }

  // ── Shared archive-entry builder ─────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.qoa", "Container", blob, "qoa"),
    };

    try {
      using var probe = new MemoryStream(blob, writable: false);
      var info = QoaCodec.ReadStreamInfo(probe);

      using var src = new MemoryStream(blob, writable: false);
      using var pcm = new MemoryStream();
      QoaCodec.Decompress(src, pcm);
      var pcmBytes = pcm.ToArray();

      if (info.Channels <= 1) {
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(pcmBytes, 1, info.SampleRate, 16, formatCode: 1), "pcm"));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(
            pcmBytes, info.Channels, info.SampleRate, 16))
          entries.Add(new($"{name}.wav", "Channel", wav, "pcm"));
      }

      var meta = new StringBuilder();
      meta.AppendLine("format=QOA");
      meta.AppendLine($"channels={info.Channels}");
      meta.AppendLine($"sample_rate={info.SampleRate}");
      meta.AppendLine("bits_per_sample=16");
      meta.AppendLine($"samples_per_channel={info.SamplesPerChannel}");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString()), "stored"));
    } catch (Exception) {
      // Graceful fallback: surface the original QOA file only.
    }

    return entries;
  }
}
