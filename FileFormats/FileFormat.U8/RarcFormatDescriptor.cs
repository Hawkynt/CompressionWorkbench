#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Rarc;

/// <summary>
/// Nintendo Resource Archive (RARC), used heavily by GameCube-era JSystem titles and
/// some Wii software. The raw container is big-endian; Yaz0/Yay0 compression is an
/// independent outer layer and is intentionally not folded into this descriptor.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://wiki.cloudmodding.com/zgcn/ARC</c> — RARC headers, nodes, file entries and flags</description></item>
///   <item><description><c>https://kuribo64.net/wiki/?page=RARC</c> — alignment, hierarchy and filename hash documentation</description></item>
///   <item><description><c>https://www.lumasworkshop.com/wiki/RARC_(File_Format)</c> — MRAM/ARAM/DVD data-block layout</description></item>
/// </list>
/// </summary>
public sealed class RarcFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveCreatable,
  IArchiveModifiable,
  IArchiveDefragmentable,
  IArchiveLayoutMap {

  public string Id => "Rarc";
  public string DisplayName => "Nintendo RARC";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".arc";
  public IReadOnlyList<string> Extensions => [".arc", ".rarc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("RARC"u8.ToArray(), Confidence: 0.99),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Nintendo Resource Archive (RARC) with directory nodes and 32-byte-aligned file data";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    if (stream.CanSeek)
      stream.Position = 0;
    var reader = new RarcReader(stream);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      Index: index,
      Name: entry.Name,
      OriginalSize: entry.Size,
      CompressedSize: entry.Size,
      Method: DescribeMethod(entry.Attributes),
      IsDirectory: entry.IsDirectory,
      IsEncrypted: false,
      LastModified: null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (stream.CanSeek)
      stream.Position = 0;
    var reader = new RarcReader(stream);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory)
        continue;
      if (files is { Length: > 0 } && !MatchesFilter(entry.Name, files))
        continue;
      WriteFile(outputDir, entry.Name, reader.Extract(entry));
    }
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek)
      archive.Position = 0;
    var reader = new RarcReader(archive);
    var entry = reader.Entries.FirstOrDefault(candidate =>
      !candidate.IsDirectory && string.Equals(candidate.Name, entryName, StringComparison.OrdinalIgnoreCase));
    return entry is null ? [] : reader.Extract(entry);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    using var writer = new RarcWriter(output, leaveOpen: true);
    foreach (var input in inputs) {
      if (input.IsDirectory)
        continue;
      writer.AddEntry(input.ArchiveName, input.ReadContent());
    }
  }

  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    if (archive.CanSeek)
      archive.Position = 0;
    var reader = new RarcReader(archive);
    foreach (var entry in reader.Entries)
      if (!entry.IsDirectory && entry.Size > 0)
        yield return new DefragBlockInfo(entry.Offset, entry.Size, DefragBlockKind.Used, FileName: entry.Name);
  }

  private static string DescribeMethod(RarcEntryAttributes attributes) {
    if ((attributes & RarcEntryAttributes.Yaz0Compressed) != 0)
      return "Yaz0";
    if ((attributes & RarcEntryAttributes.Compressed) != 0)
      return "Yay0/Compressed";
    return "Stored";
  }
}
