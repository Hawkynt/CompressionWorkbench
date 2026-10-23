#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.CompactPro;

/// <summary>
/// Compact Pro (Bill Goodman / Cyclos, 1990-1995), the classic Mac archiver. Each file's forks are
/// RLE-coded, optionally LZH-compressed below that. A resource fork is exposed as "name.rsrc".
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/MacPaw/XADMaster</c> — The Unarchiver's XADCompactProParser and fork handles (LGPL-2.1), converted for the reader</description></item>
///   <item><description><c>http://justsolve.archiveteam.org/wiki/Compact_Pro</c> — format overview</description></item>
/// </list>
/// </summary>
public sealed class CompactProFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveLayoutMap {

  /// <summary>Suffix naming a resource-fork entry.</summary>
  public const string ResourceSuffix = ".rsrc";

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new CompactProReader(archive);
    foreach (var e in r.Entries) {
      if (e.ResourceForkCompressedSize > 0)
        yield return new DefragBlockInfo(e.ResourceOffset, e.ResourceForkCompressedSize, DefragBlockKind.Used, FileName: e.FileName + ResourceSuffix);
      if (e.DataForkCompressedSize > 0)
        yield return new DefragBlockInfo(e.DataOffset, e.DataForkCompressedSize, DefragBlockKind.Used, FileName: e.FileName);
    }
  }

  public string Id => "CompactPro";
  public string DisplayName => "Compact Pro";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: create writes RLE-coded archives The Unarchiver extracts; edits use the verified
  // extract -> edit -> re-create rebuild (default IArchiveModifiable).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".cpt";
  public IReadOnlyList<string> Extensions => [".cpt"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x01], Confidence: 0.20)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rle", "RLE")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Macintosh Compact Pro archive (RLE and LZH+RLE forks; resource forks as name.rsrc)";

  private static IEnumerable<(string Name, CompactProEntry Entry, bool Resource)> Items(CompactProReader r) {
    foreach (var e in r.Entries) {
      if (e.IsDirectory) {
        yield return (e.FileName, e, false);
        continue;
      }
      if (e.ResourceForkSize > 0)
        yield return (e.FileName + ResourceSuffix, e, true);
      if (e.DataForkSize > 0 || e.ResourceForkSize == 0)
        yield return (e.FileName, e, false);
    }
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new CompactProReader(stream);
    var i = 0;
    return Items(r).Select(x => new ArchiveEntryInfo(i++, x.Name,
      x.Resource ? x.Entry.ResourceForkSize : x.Entry.DataForkSize,
      x.Resource ? x.Entry.ResourceForkCompressedSize : x.Entry.DataForkCompressedSize,
      x.Entry.IsDirectory ? "dir" : (x.Resource ? x.Entry.ResourceForkLzh : x.Entry.DataForkLzh) ? "LZH+RLE" : "RLE",
      x.Entry.IsDirectory, x.Entry.IsEncrypted, x.Entry.IsDirectory ? null : x.Entry.ModifiedDate)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new CompactProReader(stream);
    foreach (var (name, entry, resource) in Items(r)) {
      if (files != null && !MatchesFilter(name, files)) continue;
      if (entry.IsDirectory) {
        Directory.CreateDirectory(Path.Combine(outputDir, name));
        continue;
      }
      var (data, rsrc) = r.ExtractForks(entry);
      WriteFile(outputDir, name, resource ? rsrc : data);
    }
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;
    var r = new CompactProReader(archive);
    foreach (var (name, entry, resource) in Items(r)) {
      if (entry.IsDirectory || !string.Equals(name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var (data, rsrc) = r.ExtractForks(entry);
      return resource ? rsrc : data;
    }
    throw new FileNotFoundException($"No entry '{entryName}' in the Compact Pro archive.");
  }

  /// <summary>
  /// Creates an archive. An input named "x.rsrc" becomes the resource fork of "x" when "x" is also an
  /// input (or has no data fork of its own); everything else is a data fork.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new CompactProWriter();
    var byName = inputs.Where(i => !i.IsDirectory).ToDictionary(i => i.ArchiveName.Replace('\\', '/'), StringComparer.Ordinal);
    foreach (var input in inputs) {
      var name = input.ArchiveName.Replace('\\', '/');
      if (input.IsDirectory) {
        w.AddFolder(name);
        continue;
      }
      if (name.EndsWith(ResourceSuffix, StringComparison.Ordinal) && name.Length > ResourceSuffix.Length) {
        var owner = name[..^ResourceSuffix.Length];
        if (byName.ContainsKey(owner))
          continue; // attached to its data fork below
        w.AddFile(owner, [], input.ReadContent(), modified: Modified(input));
        continue;
      }
      var resource = byName.TryGetValue(name + ResourceSuffix, out var r) ? r.ReadContent() : null;
      w.AddFile(name, input.ReadContent(), resource, modified: Modified(input));
    }
    w.WriteTo(output);
  }

  private static DateTime? Modified(ArchiveInputInfo i)
    => i.Metadata?.LastWriteTimeUtc?.UtcDateTime
       ?? (i.InMemoryContent == null && File.Exists(i.FullPath) ? File.GetLastWriteTimeUtc(i.FullPath) : null);
}
