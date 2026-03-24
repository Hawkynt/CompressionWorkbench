#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ace;

public sealed class AceFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Ace";
  public string DisplayName => "ACE";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsPassword | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ace";
  public IReadOnlyList<string> Extensions => [".ace"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'*', (byte)'*', (byte)'A', (byte)'C', (byte)'E', (byte)'*', (byte)'*'], Offset: 7, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ace1", "ACE 1"), new("ace2", "ACE 2"), new("store", "Store")];
  public string? TarCompressionFormatId => null;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new AceReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      $"ACE {e.CompressionType}", false, e.IsEncrypted, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new AceReader(stream, password: password);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var dictBits = options.DictSize > 0
      ? Math.Clamp((int)Math.Log2(options.DictSize), 10, 22) : 15;
    var solid = options.SolidSize == 0;
    var compType = options.MethodName switch {
      "store" => 0,
      "ace20" or "ace2" => 2,
      _ => 1,
    };
    var w = new AceWriter(dictionaryBits: dictBits, password: options.Password,
      solid: solid, compressionType: compType);
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }
}
