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
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "OrangeFs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "OrangeFS / PVFS2 DBPF";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".orangefs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".orangefs", ".pvfs", ".bstream"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("PVFS"u8.ToArray(), Offset: 0, Confidence: 0.90),
    new("OGFP"u8.ToArray(), Offset: 0, Confidence: 0.90),
  ];
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
  public string Description =>
    "OrangeFS / PVFS2 DBPF storage object — opaque object payload R/W; cluster namespace resolution requires fs.conf.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    if (stream.CanSeek) stream.Position = 0;
    using var r = new OrangeFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (stream.CanSeek) stream.Position = 0;
    using var r = new OrangeFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var payload = FilesOnly(inputs)
      .FirstOrDefault(f => !IsSynthetic(f.Name)).Data ?? [];
    OrangeFsWriter.Create(output, payload);
  }

  /// <summary>
  /// Adds the supplied entry to the target container.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    var payload = FilesOnly(inputs).LastOrDefault(f => !IsSynthetic(f.Name)).Data;
    if (payload != null)
      OrangeFsWriter.ReplacePayload(archive, payload);
  }

  /// <summary>
  /// Removes the specified entry from the target container.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    if (entryNames.Any(n => string.Equals(Path.GetFileName(n), "object.bin", StringComparison.OrdinalIgnoreCase)))
      OrangeFsWriter.ReplacePayload(archive, []);
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive) { }
  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) { }

  private static bool IsSynthetic(string name) {
    var leaf = Path.GetFileName(name);
    return leaf.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)
      || leaf.StartsWith("FULL.", StringComparison.OrdinalIgnoreCase);
  }
}
