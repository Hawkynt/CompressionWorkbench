#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Rar;

public sealed class RarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "RAR defragmentation is not supported — solid blocks, recovery records, and per-archive signatures " +
      "would all need to be regenerated, which is not safe via a generic rebuild path.");
  public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => RarLayoutMap.Enumerate(archive);

  public string Id => "Rar";
  public string DisplayName => "RAR";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsPassword | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".rar";
  public IReadOnlyList<string> Extensions => [".rar"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x00], Confidence: 0.95),
    new([(byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x01, 0x00], Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("rar5", "RAR 5"), new("rar4", "RAR 4"), new("store", "Store")
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "RAR archive with solid compression and recovery records";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new RarReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.CompressedSize,
      $"Method {e.CompressionMethod}", e.IsDirectory, e.IsEncrypted, e.ModifiedTime?.DateTime)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new RarReader(stream, password: password);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Name)); continue; }
      WriteFile(outputDir, e.Name, r.Extract(i));
    }
  }

  /// <summary>
  /// Builds a RAR archive from <paramref name="inputs"/>. Selects RAR4 or RAR5
  /// based on <c>options.MethodName</c> and resolves dictionary / level from
  /// <c>options.DictSize</c> / <c>options.Level</c>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var useRar4 = options.MethodName == "rar4";
    var useStore = options.MethodName is "store" or "copy";
    var rarLevel = useStore ? 0 : options.Level switch {
      0 => 0, 1 => 1, 2 => 2, 3 or 4 => 3, 5 or 6 => 4, >= 7 => 5, _ => 3,
    };

    if (useRar4) {
      var windowBits = options.DictSize > 0
        ? Math.Clamp((int)Math.Log2(options.DictSize), 15, 22) : 20;
      var rar4Method = (byte)(0x30 + rarLevel);
      var w4 = new Rar4Writer(output, method: rar4Method, windowBits: windowBits,
        solid: options.SolidSize == 0, password: options.Password);
      foreach (var i in inputs) {
        if (i.IsDirectory) continue; // RAR writer doesn't track empty dirs here
        w4.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath));
      }
      w4.Finish();
    } else {
      var dictLog = options.DictSize > 0
        ? Math.Clamp((int)Math.Log2(options.DictSize), 17, 28) : 17;
      var w = new RarWriter(output, method: rarLevel, dictionarySizeLog: dictLog,
        solid: options.SolidSize == 0, password: options.Password,
        encryptHeaders: options.EncryptFilenames);
      foreach (var i in inputs) {
        if (i.IsDirectory) continue;
        w.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath));
      }
      w.Finish();
    }
  }
}
