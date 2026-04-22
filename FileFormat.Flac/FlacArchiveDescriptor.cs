#pragma warning disable CS1591
using Compression.Core.Audio;
using Compression.Registry;

namespace FileFormat.Flac;

/// <summary>
/// Archive-shaped view of a FLAC file: full blob + decoded per-channel WAVs.
/// The existing <see cref="FlacFormatDescriptor"/> keeps its stream-decompressor
/// contract for back-compat; this descriptor provides the recursive-descent path
/// so users can pull out <c>LEFT.wav</c>/<c>RIGHT.wav</c> from a FLAC directly.
/// </summary>
public sealed class FlacArchiveDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "FlacArchive";
  public string DisplayName => "FLAC (archive view)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".flac";
  // Empty — the primary Flac descriptor owns the magic; this one is picked up only
  // via explicit registry lookup (e.g. `cwb list --format FlacArchive`).
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "FLAC audio as archive: full file + decoded per-channel PCM.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

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
      ("FULL.flac", "Track", blob),
    };

    var props = FlacReader.ReadAudioProperties(blob);

    // Decode to interleaved PCM, then split per-channel.
    using var src = new MemoryStream(blob);
    using var pcm = new MemoryStream();
    FlacReader.Decompress(src, pcm);
    var pcmBytes = pcm.ToArray();

    if (props.Channels == 1) {
      entries.Add(("MONO.wav", "Channel",
        ChannelSplitter.ToWavBlob(pcmBytes, 1, props.SampleRate, props.BitsPerSample)));
    } else {
      foreach (var (name, wav) in ChannelSplitter.SplitInterleavedPcm(
          pcmBytes, props.Channels, props.SampleRate, props.BitsPerSample))
        entries.Add(($"{name}.wav", "Channel", wav));
    }
    return entries;
  }
}
