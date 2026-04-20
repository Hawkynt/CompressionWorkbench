#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mpq;

public sealed class MpqFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Mpq";
  public string DisplayName => "MPQ";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mpq";
  public IReadOnlyList<string> Extensions => [".mpq"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'M', (byte)'P', (byte)'Q', 0x1A], Confidence: 0.95),
    new([(byte)'M', (byte)'P', (byte)'Q', 0x1B], Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("mpq", "MPQ")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Blizzard MPQ game archive (Diablo/StarCraft/WoW)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MpqReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.IsCompressed ? "Compressed" : "Stored", false, e.IsEncrypted, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MpqReader(stream);
    foreach (var e in r.Entries) {
      if (!e.Exists) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      try { WriteFile(outputDir, e.FileName, r.Extract(e)); } catch { }
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: produce a v1 MPQ with stored (uncompressed) file entries plus an
    // auto-generated "(listfile)" so file names roundtrip. Compression isn't
    // emitted -- the existing per-method decoders (zlib/bzip2/PKWARE/Huffman)
    // don't have paired encoders here, and stored files are valid MPQ entries.
    var w = new MpqWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath));
    }
    w.WriteTo(output);
  }
}
