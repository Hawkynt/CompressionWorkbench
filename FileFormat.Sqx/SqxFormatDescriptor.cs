#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Sqx;

public sealed class SqxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Sqx";
  public string DisplayName => "SQX";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsPassword | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".sqx";
  public IReadOnlyList<string> Extensions => [".sqx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'S', (byte)'Q', (byte)'X', 0x01], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("sqx", "SQX")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "SQX archive with multiple compression algorithms";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new SqxReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      $"SQX {e.Method}", false, e.IsEncrypted, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new SqxReader(stream, password: password);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var solid = options.SolidSize == 0;
    var dictSize = options.DictSize > 0 ? (int)Math.Min(options.DictSize, 4 * 1024 * 1024) : 256 * 1024;
    var w = new SqxWriter(password: options.Password, solid: solid,
      dictSize: dictSize);
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }
}
