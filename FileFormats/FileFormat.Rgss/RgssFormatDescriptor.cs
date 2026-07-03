#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Rgss;

public sealed class RgssFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>
  /// Adds (or replaces by name) files inside an existing RGSS archive via the
  /// verified extract -> edit -> re-create rebuild. The synthetic
  /// <c>metadata.ini</c> listing entry (a derived view of the header) is dropped
  /// from the extracted tree before re-creation so it is not duplicated as a
  /// real entry.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      DropSyntheticMetadata(tmpDir);
      foreach (var input in inputs) {
        if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
        var dest = Path.Combine(tmpDir, input.ArchiveName.Replace('/', Path.DirectorySeparatorChar));
        var destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
        File.WriteAllBytes(dest, input.ReadContent());
      }
    });
  }

  /// <summary>
  /// Removes the named entries via the verified extract -> edit -> re-create
  /// rebuild, dropping the synthetic <c>metadata.ini</c> the same way
  /// <see cref="Add"/> does.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    var skip = new HashSet<string>(entryNames ?? [], StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      DropSyntheticMetadata(tmpDir);
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        if (skip.Contains(rel) || skip.Contains(Path.GetFileName(rel)))
          File.Delete(file);
      }
    });
  }

  private static void DropSyntheticMetadata(string tmpDir) {
    var meta = Path.Combine(tmpDir, "metadata.ini");
    if (File.Exists(meta)) File.Delete(meta);
  }

  /// <summary>Rebuild-based defrag: extracts then re-creates the RGSS archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the RGSS archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new RgssReader(stream);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        var entries = files.Select(f => (Name: f.Name, Data: f.Data)).ToList();
        new RgssWriter(ms).Write(entries);
        return ms.ToArray();
      });
  }


  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    // 8-byte magic header "RGSSAD\0V"
    yield return new DefragBlockInfo(0, 8, DefragBlockKind.MetadataReserved, FileName: "RGSS Header");
    RgssReader r;
    try {
      r = new RgssReader(archive);
    } catch {
      yield break;
    }
    foreach (var e in r.Entries) {
      if (e.Size > 0 && e.Offset >= 0)
        yield return new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  public string Id => "Rgss";
  public string DisplayName => "RPG Maker RGSSAD";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable archive. Add/Replace/Remove go through the verified extract ->
  // edit -> re-create rebuild (with the synthetic metadata.ini view filtered);
  // relayouting the container on edit is honest R/W. See FormatCapabilities.cs
  // (WORM vs R/W).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".rgssad";
  public IReadOnlyList<string> Extensions => [".rgssad", ".rgss2a", ".rgss3a"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'R', (byte)'G', (byte)'S', (byte)'S', (byte)'A', (byte)'D', 0, 1], Confidence: 0.95),
    new([(byte)'R', (byte)'G', (byte)'S', (byte)'S', (byte)'A', (byte)'D', 0, 2], Confidence: 0.95),
    new([(byte)'R', (byte)'G', (byte)'S', (byte)'S', (byte)'A', (byte)'D', 0, 3], Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rgss", "RGSSAD")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "RPG Maker XP/VX/VX Ace encrypted resource archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new RgssReader(stream);
    var list = new List<ArchiveEntryInfo> {
      new(0, "metadata.ini", 0, 0, "Stored", false, false, null)
    };
    int idx = 1;
    foreach (var e in r.Entries)
      list.Add(new ArchiveEntryInfo(idx++, e.Name, e.Size, e.Size, "XOR", false, true, null));
    return list;
  }

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. The underlying
  /// reader produces the entry's bytes (decoded if the format compresses
  /// per-entry); the returned stream is a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's logical length so adjacent entries and any trailing
  /// padding are physically unreachable through this view.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new RgssReader(archive);
    if (string.Equals(entryName, "metadata.ini", StringComparison.OrdinalIgnoreCase)) {
      var sb = new StringBuilder();
      sb.AppendLine("[rgss]");
      sb.AppendLine($"version={r.Version}");
      sb.AppendLine($"file_count={r.Entries.Count}");
      if (r.Version == 3)
        sb.AppendLine($"master_key=0x{r.MasterKeyV3:X8}");
      var meta = Encoding.UTF8.GetBytes(sb.ToString());
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(meta, writable: false), meta.Length, leaveOpen: false);
    }
    foreach (var e in r.Entries) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var entries = FormatHelpers.FilesOnly(inputs).ToList();
    var w = new RgssWriter(output);
    w.Write(entries);
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new RgssReader(stream);

    if (files == null || MatchesFilter("metadata.ini", files)) {
      var sb = new StringBuilder();
      sb.AppendLine("[rgss]");
      sb.AppendLine($"version={r.Version}");
      sb.AppendLine($"file_count={r.Entries.Count}");
      if (r.Version == 3)
        sb.AppendLine($"master_key=0x{r.MasterKeyV3:X8}");
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(sb.ToString()));
    }

    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }
}
