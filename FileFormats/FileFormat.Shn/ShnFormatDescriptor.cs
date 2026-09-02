#pragma warning disable CS1591
using System.Text;
using Codec.Pcm;
using Codec.Shorten;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Shn;

/// <summary>
/// Exposes a Shorten (<c>.shn</c>) file as a pseudo-archive of <c>FULL.shn</c> (the byte-exact
/// container) plus one decoded mono PCM WAV per channel, named per
/// <see cref="ChannelLayout"/> (mono → <c>MONO.wav</c>, stereo → <c>LEFT.wav</c>/<c>RIGHT.wav</c>).
/// A <c>metadata.ini</c> records the decoded properties and notes that the Shorten container
/// carries no sample rate — the surfaced WAVs assume 44100 Hz.
/// <para>
/// If the stream cannot be decoded (e.g. an unsupported file type or a QLPC-encoded stream this
/// codec cannot reconstruct), the descriptor degrades gracefully to a <c>FULL.shn</c>-only view
/// rather than failing the listing.
/// </para>
/// </summary>
public sealed class ShnFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>Sample rate assumed for surfaced channel WAVs (Shorten stores none).</summary>
  internal const int AssumedSampleRate = 44100;

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Shn";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Shorten (SHN)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Audio;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".shn";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".shn"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("ajkg"u8.ToArray(), Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("shorten", "Shorten")];
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
  public string Description => "Shorten lossless audio (SHN); full file + decoded per-channel PCM.";

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

  // ── IArchiveCreatable: assemble a .shn from FULL.shn or per-channel mono WAVs ───────

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    // Passthrough a provided FULL.shn verbatim (archive-view semantics).
    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.shn", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(f => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("SHN archive create needs either FULL.shn or one or more per-channel WAVs.");

    var channels = channelBlobs.Select(f => new WavReader().Read(f.Data)).ToList();
    var first = channels[0];
    if (channels.Any(c => c.BitsPerSample != first.BitsPerSample || c.NumChannels != 1))
      throw new InvalidOperationException("All channel WAVs must be mono and share the same bit depth.");
    if (first.BitsPerSample is not (8 or 16))
      throw new InvalidOperationException("Shorten create supports 8-bit or 16-bit PCM only.");

    var bytesPerSample = first.BitsPerSample / 8;
    var frameCount = first.InterleavedPcm.Length / bytesPerSample;
    if (channels.Any(c => c.InterleavedPcm.Length / bytesPerSample != frameCount))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");

    var interleaved = PcmCodec.Interleave(channels.Select(c => c.InterleavedPcm).ToList(), first.BitsPerSample);

    using var src = new MemoryStream(interleaved);
    ShortenCodec.Compress(src, output, channels.Count, first.SampleRate, first.BitsPerSample);
  }

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    "SHN archive accepts: FULL.shn, or LEFT/RIGHT/CENTER/… .wav (per-channel mono PCM)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    var dir = Path.GetDirectoryName(input.ArchiveName)?.Replace('\\', '/').ToLowerInvariant() ?? "";

    if (dir == "" && (name == "full.shn" || name.EndsWith(".wav") || name == "metadata.ini")) {
      reason = null;
      return true;
    }
    reason = $"not a SHN-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── Shared archive-entry builder ─────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.shn", "Container", blob, "shorten"),
    };

    try {
      using var infoStream = new MemoryStream(blob);
      var info = ShortenCodec.ReadStreamInfo(infoStream);

      using var src = new MemoryStream(blob);
      using var pcm = new MemoryStream();
      ShortenCodec.Decompress(src, pcm);
      var pcmBytes = pcm.ToArray();

      if (info.Channels == 1) {
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(pcmBytes, 1, AssumedSampleRate, info.BitsPerSample), "pcm"));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(
            pcmBytes, info.Channels, AssumedSampleRate, info.BitsPerSample))
          entries.Add(new($"{name}.wav", "Channel", wav, "pcm"));
      }

      entries.Add(new("metadata.ini", "Tag",
        Encoding.UTF8.GetBytes(BuildMetadata(info, pcmBytes.Length))));
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) {
      // Undecodable stream: surface the container only.
    }

    return entries;
  }

  private static string BuildMetadata(ShortenCodec.ShortenStreamInfo info, int pcmByteLength) {
    var sb = new StringBuilder();
    sb.AppendLine("[shorten]");
    sb.AppendLine($"channels={info.Channels}");
    sb.AppendLine($"bits_per_sample={info.BitsPerSample}");
    sb.AppendLine($"file_type={info.FileType}");
    sb.AppendLine("sample_rate=unknown(assumed 44100)");
    sb.AppendLine($"decoded_pcm_bytes={pcmByteLength}");
    return sb.ToString();
  }
}
