#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mpq;

/// <summary>
/// Blizzard MPQ (Mo'PaQ) game archive used by Diablo, StarCraft, WarCraft III and World of Warcraft.
///
/// References:
/// <list type="bullet">
///   <item><description><c>http://www.zezula.net/en/mpq/main.html</c> — Ladislav Zezula's MPQ format documentation — the de-facto specification</description></item>
///   <item><description><c>https://github.com/ladislav-zezula/StormLib</c> — StormLib — maintained reference implementation</description></item>
/// </list>
/// </summary>
public sealed class MpqFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the MPQ archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Rebuild-based defrag: extracts then re-creates the MPQ archive per the
  /// requested mode. The auto-generated <c>(listfile)</c> is excluded from the
  /// extracted set — the writer regenerates it and refuses it as an explicit input.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new MpqReader(stream);
        var list = new List<(string Name, byte[] Data)>();
        foreach (var e in r.Entries) {
          if (!e.Exists) continue;
          if (string.Equals(e.FileName, "(listfile)", StringComparison.OrdinalIgnoreCase)) continue;
          try { list.Add((e.FileName, r.Extract(e))); } catch { /* skip unreadable */ }
        }
        return list;
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        var w = new MpqWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        w.WriteTo(ms);
        return ms.ToArray();
      });
  }

  /// <inheritdoc />
    /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    MpqReader r;
    try {
      archive.Position = 0;
      r = new MpqReader(archive);
    } catch {
      yield break;
    }
    // MPQ header is 32 bytes (v1) at _headerOffset
    yield return new DefragBlockInfo(r.HeaderOffset, 32, DefragBlockKind.MetadataReserved, FileName: "MPQ Header");
    foreach (var e in r.Entries) {
      if (!e.Exists || e.CompressedSize <= 0) continue;
      yield return new DefragBlockInfo(r.HeaderOffset + e.FileOffset, e.CompressedSize, DefragBlockKind.Used, FileName: e.FileName);
    }
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing MPQ archive via the
  /// verified extract -> edit -> re-create rebuild. The auto-generated
  /// <c>(listfile)</c> is dropped from the extracted tree before re-creation
  /// (the writer regenerates it and refuses it as an explicit input), so entry
  /// names still round-trip without duplicating the listing.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      DropGeneratedListfile(tmpDir);
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
  /// rebuild, dropping the auto-generated <c>(listfile)</c> the same way
  /// <see cref="Add"/> does.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    var skip = new HashSet<string>(entryNames ?? [], StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      DropGeneratedListfile(tmpDir);
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        if (skip.Contains(rel) || skip.Contains(Path.GetFileName(rel)))
          File.Delete(file);
      }
    });
  }

  private static void DropGeneratedListfile(string tmpDir) {
    foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories))
      if (Path.GetFileName(file).Equals("(listfile)", StringComparison.OrdinalIgnoreCase))
        File.Delete(file);
  }

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Mpq";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "MPQ";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable archive. Add/Replace/Remove go through the verified extract ->
  // edit -> re-create rebuild (with the auto-generated "(listfile)" filtered);
  // relayouting the container on edit is honest R/W. See FormatCapabilities.cs
  // (WORM vs R/W).
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".mpq";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".mpq"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'M', (byte)'P', (byte)'Q', 0x1A], Confidence: 0.95),
    new([(byte)'M', (byte)'P', (byte)'Q', 0x1B], Confidence: 0.95),
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("mpq", "MPQ")];
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
public string Description => "Blizzard MPQ game archive (Diablo/StarCraft/WoW)";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MpqReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.IsCompressed ? "Compressed" : "Stored", false, e.IsEncrypted, null)).ToList();
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MpqReader(stream);
    foreach (var e in r.Entries) {
      if (!e.Exists) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      try { WriteFile(outputDir, e.FileName, r.Extract(e)); } catch { }
    }
  }

  /// <summary>
  /// Opens a single MPQ entry as a bounded read-only stream. The reader
  /// decodes per-entry compression/encryption; the decoded bytes are
  /// wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's original (uncompressed) length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new MpqReader(archive);
    foreach (var e in r.Entries) {
      if (!e.Exists) continue;
      if (!string.Equals(e.FileName, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      byte[] bytes;
      try { bytes = r.Extract(e); }
      catch { bytes = System.Array.Empty<byte>(); }
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

    /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: produce a v1 MPQ with stored (uncompressed) file entries plus an
    // auto-generated "(listfile)" so file names roundtrip. Compression isn't
    // emitted -- the existing per-method decoders (zlib/bzip2/PKWARE/Huffman)
    // don't have paired encoders here, and stored files are valid MPQ entries.
    var w = new MpqWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, i.ReadContent());
    }
    w.WriteTo(output);
  }
}
