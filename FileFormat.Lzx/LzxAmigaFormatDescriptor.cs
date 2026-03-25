#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Lzx;

public sealed class LzxAmigaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "LzxAmiga";
  public string DisplayName => "LZX (Amiga)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".lzx";
  public IReadOnlyList<string> Extensions => [".lzx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'L', (byte)'Z', (byte)'X'], Confidence: 0.80)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzx", "LZX")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga LZX archive with LZ+Huffman";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new LzxAmigaReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.Method == 0 ? "Stored" : "LZX", false, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new LzxAmigaReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new LzxAmigaWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data, DateTime.Now);
  }
}
