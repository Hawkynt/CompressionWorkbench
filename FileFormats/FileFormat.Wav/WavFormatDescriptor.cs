#pragma warning disable CS1591
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Wav;

/// <summary>
/// Exposes a WAV/RIFF file as an archive of <c>FULL.wav</c> plus one mono WAV per
/// channel plus any ancillary RIFF metadata chunks (INFO/LIST/bext).
/// </summary>
public sealed class WavFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable, IArchiveDefragmentable, IFileInternalLayoutMap, IFileInternalChunkMover {

    /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "WAV is a single-blob audio file (RIFF chunks + PCM data) — defragmentation isn't meaningful; use WavOptimizer instead.");
    /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Wav";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "WAV (RIFF audio)";
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
public string DefaultExtension => ".wav";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".wav", ".wave"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("RIFF"u8.ToArray(), Confidence: 0.55),
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
public string Description => "WAV audio; full file + per-channel PCM + RIFF metadata chunks.";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = BuildEntries(stream, out var parsed);
    var fullMethod = WavCodecName(parsed.FormatCode);
    return entries.Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      // FULL.wav carries the file's original codec; per-channel WAVs are
      // freshly-built integer PCM; metadata chunks are raw bytes.
      Method: e.Name.Equals("FULL.wav", StringComparison.OrdinalIgnoreCase) ? fullMethod
            : e.Kind == "Channel" ? "pcm"
            : "stored",
      IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();
  }

  /// <summary>
  /// Maps the RIFF <c>WAVE_FORMAT_*</c> code (the <c>wFormatTag</c> in the <c>fmt </c>
  /// chunk) to a short human-readable codec name. Falls back to the hex code for
  /// uncatalogued values so the user can look it up in the WAVE format registry.
  /// </summary>
  private static string WavCodecName(int formatCode) => formatCode switch {
    0x0001 => "pcm",
    0x0002 => "ms_adpcm",
    0x0003 => "pcm_float",
    0x0006 => "alaw",
    0x0007 => "mulaw",
    0x0011 => "ima_adpcm",
    0x0022 => "truespeech",
    0x0031 => "gsm610",
    0x0050 => "mpeg",
    0x0055 => "mp3",
    0x0092 => "ac3",
    0x0161 => "wma",
    0x0162 => "wmapro",
    0x0163 => "wmalossless",
    0x2000 => "ac3_dolby",
    0x2001 => "dts",
    0xFFFE => "extensible",
    _ => $"format_0x{formatCode:X4}",
  };

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream, out _)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

    /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input, out _)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  // ── IArchiveCreatable: remux per-channel WAVs (LEFT/RIGHT/…) into one multi-channel WAV ──

    /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // If FULL.wav is provided, passthrough it verbatim (archive-view semantics).
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();
    var full = fileList.FirstOrDefault(f => System.IO.Path.GetFileName(f.Name).Equals("FULL.wav", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    // Otherwise, look for per-channel mono WAVs and interleave them.
    var channelBlobs = fileList
      .Where(f => {
        var name = System.IO.Path.GetFileName(f.Name);
        return name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals("FULL.wav", StringComparison.OrdinalIgnoreCase);
      })
      .OrderBy(f => ChannelOrder(System.IO.Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("WAV archive create needs either FULL.wav or one or more per-channel WAVs.");

    var channels = new List<WavReader.ParsedWav>();
    foreach (var (_, data) in channelBlobs) channels.Add(new WavReader().Read(data));

    var first = channels[0];
    if (channels.Any(c => c.SampleRate != first.SampleRate || c.BitsPerSample != first.BitsPerSample || c.NumChannels != 1))
      throw new InvalidOperationException(
        "All channel WAVs must be mono and share sample rate + bit depth.");

    var bytesPerSample = first.BitsPerSample / 8;
    var frameCount = first.InterleavedPcm.Length / bytesPerSample;
    if (channels.Any(c => c.InterleavedPcm.Length / bytesPerSample != frameCount))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");

    var interleaved = PcmCodec.Interleave(channels.Select(c => c.InterleavedPcm).ToList(), first.BitsPerSample);

    var blob = Codec.Pcm.PcmCodec.ToWavBlob(
      interleaved, channels.Count, first.SampleRate, first.BitsPerSample, formatCode: 1);
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
    "WAV archive accepts: FULL.wav, LEFT/RIGHT/CENTER/… .wav (per-channel), metadata/*.bin";

    /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = System.IO.Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    var dir = System.IO.Path.GetDirectoryName(input.ArchiveName)?.Replace('\\', '/').ToLowerInvariant() ?? "";

    if (dir == "" && name.EndsWith(".wav")) { reason = null; return true; }
    if (dir == "metadata" && name.EndsWith(".bin")) { reason = null; return true; }
    reason = $"not a WAV-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private readonly WavOptimizer _optimizer = new();

  /// <inheritdoc />
    /// <summary>
  /// Enumerates the chunks.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateChunks(Stream file) => WavLayoutMap.Enumerate(file);

  /// <inheritdoc />
    /// <summary>
  /// Performs the optimize operation.
  /// </summary>
public void Optimize(Stream file) => _optimizer.Optimize(file);

  /// <inheritdoc />
    /// <summary>
  /// Performs the optimize operation.
  /// </summary>
public void Optimize(Stream file, MetadataPlacementProfile? profile) => _optimizer.Optimize(file, profile);

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream, out WavReader.ParsedWav parsed) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    parsed = new WavReader().Read(blob);

    var entries = new List<(string, string, byte[])> {
      ("FULL.wav", "Container", blob),
    };

    // Split PCM integer formats (code 1) per-channel; IEEE-float (code 3, 32/64-bit)
    // is split into per-channel float WAVs. Other codecs are surfaced as FULL only.
    if (parsed.FormatCode == 1 && parsed.BitsPerSample is 8 or 16 or 24 or 32 && parsed.NumChannels > 1) {
      foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(
          parsed.InterleavedPcm, parsed.NumChannels, parsed.SampleRate, parsed.BitsPerSample,
          parsed.ChannelMask))
        entries.Add(($"{name}.wav", "Channel", wavBlob));
    } else if (parsed.FormatCode == 3 && parsed.BitsPerSample is 32 or 64 && parsed.NumChannels > 1) {
      foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedFloat(
          parsed.InterleavedPcm, parsed.NumChannels, parsed.SampleRate, parsed.BitsPerSample,
          parsed.ChannelMask))
        entries.Add(($"{name}.wav", "Channel", wavBlob));
    }
    foreach (var (id, data) in parsed.MetadataChunks)
      entries.Add(($"metadata/{id.Trim()}.bin", "Tag", data));

    return entries;
  }
}
