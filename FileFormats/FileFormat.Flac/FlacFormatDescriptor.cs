#pragma warning disable CS1591

using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Flac;

/// <summary>
/// Format descriptor and stream operations for the FLAC (Free Lossless Audio Codec) format.
/// Also surfaces an archive view: <c>FULL.flac</c> plus one mono WAV per channel
/// (<c>LEFT.wav</c>/<c>RIGHT.wav</c>/...) so multi-channel FLAC files can be
/// decomposed in the archive browser.
/// </summary>
public sealed class FlacFormatDescriptor : IFormatDescriptor, IStreamFormatOperations,
  IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => FlacLayoutMap.Enumerate(archive);

  public string Id => "Flac";
  public string DisplayName => "FLAC";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.CanList | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".flac";
  public IReadOnlyList<string> Extensions => [".flac", ".fla"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x66, 0x4C, 0x61, 0x43], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("flac", "FLAC")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;
  public string Description => "Free Lossless Audio Codec; full file + decoded per-channel PCM.";

  // ── IStreamFormatOperations ──────────────────────────────────────────
  public void Decompress(Stream input, Stream output) => FlacReader.Decompress(input, output);
  public void Compress(Stream input, Stream output) => FlacWriter.Compress(input, output);

  // ── IArchiveFormatOperations ─────────────────────────────────────────

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: e.Name.Equals("FULL.flac", StringComparison.OrdinalIgnoreCase) ? "flac" : "pcm",
      IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  // ── IArchiveInMemoryExtract ──────────────────────────────────────────

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  // ── Shared archive-entry builder ─────────────────────────────────────

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
