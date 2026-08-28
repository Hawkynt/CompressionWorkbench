#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.OrangeFs;

/// <summary>
/// OrangeFS / PVFS2 DBPF storage-object descriptor. A DBPF file is one server-side
/// storage object rather than a complete distributed filesystem namespace; the
/// opaque object payload can nevertheless be created, replaced and removed while
/// preserving its DBPF tag/version/datastream identity.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/waltligon/orangefs</c> — official PVFS/OrangeFS repository (DBPF storage layer)</description></item>
///   <item><description><c>https://www.kernel.org/doc/html/latest/filesystems/orangefs.html</c> — Linux kernel client documentation</description></item>
/// </list>
/// </summary>
public sealed class OrangeFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable {
  public string Id => "OrangeFs";
  public string DisplayName => "OrangeFS / PVFS2 DBPF";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest;
  public string DefaultExtension => ".orangefs";
  public IReadOnlyList<string> Extensions => [".orangefs", ".pvfs", ".bstream"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("PVFS"u8.ToArray(), Offset: 0, Confidence: 0.90),
    new("OGFP"u8.ToArray(), Offset: 0, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "OrangeFS / PVFS2 DBPF storage object — opaque object payload R/W; cluster namespace resolution requires fs.conf.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    if (stream.CanSeek) stream.Position = 0;
    using var r = new OrangeFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (stream.CanSeek) stream.Position = 0;
    using var r = new OrangeFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var payload = FilesOnly(inputs)
      .FirstOrDefault(f => !IsSynthetic(f.Name)).Data ?? [];
    OrangeFsWriter.Create(output, payload);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    var payload = FilesOnly(inputs).LastOrDefault(f => !IsSynthetic(f.Name)).Data;
    if (payload != null)
      OrangeFsWriter.ReplacePayload(archive, payload);
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    if (entryNames.Any(n => string.Equals(Path.GetFileName(n), "object.bin", StringComparison.OrdinalIgnoreCase)))
      OrangeFsWriter.ReplacePayload(archive, []);
  }

  public void Defragment(Stream archive) { }
  public void Defragment(Stream archive, DefragOptions options) { }

  private static bool IsSynthetic(string name) {
    var leaf = Path.GetFileName(name);
    return leaf.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)
      || leaf.StartsWith("FULL.", StringComparison.OrdinalIgnoreCase);
  }
}
