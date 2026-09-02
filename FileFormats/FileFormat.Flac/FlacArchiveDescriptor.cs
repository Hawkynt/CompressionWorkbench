#pragma warning disable CS1591
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Flac;

/// <summary>
/// Archive-shaped view of a FLAC file: full blob + decoded per-channel WAVs.
/// The existing <see cref="FlacFormatDescriptor"/> keeps its stream-decompressor
/// contract for back-compat; this descriptor provides the recursive-descent path
/// so users can pull out <c>LEFT.wav</c>/<c>RIGHT.wav</c> from a FLAC directly.
/// </summary>
public sealed class FlacArchiveDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "FlacArchive";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "FLAC (archive view)";
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
  public string DefaultExtension => ".flac";
  // Empty — the primary Flac descriptor owns the magic; this one is picked up only
  // via explicit registry lookup (e.g. `cwb list --format FlacArchive`).
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
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
  public string Description => "FLAC audio as archive: full file + decoded per-channel PCM.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var entries = new List<(string, string, byte[])> {
      ("FULL.flac", "Container", blob),
    };

    var props = FlacReader.ReadAudioProperties(blob);

    // Decode to interleaved PCM, then split per-channel.
    using var src = new MemoryStream(blob);
    using var pcm = new MemoryStream();
    FlacReader.Decompress(src, pcm);
    var pcmBytes = pcm.ToArray();

    if (props.Channels == 1) {
      entries.Add(("MONO.wav", "Channel",
        PcmCodec.ToWavBlob(pcmBytes, 1, props.SampleRate, props.BitsPerSample)));
    } else {
      foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(
          pcmBytes, props.Channels, props.SampleRate, props.BitsPerSample))
        entries.Add(($"{name}.wav", "Channel", wav));
    }
    return entries;
  }
}
