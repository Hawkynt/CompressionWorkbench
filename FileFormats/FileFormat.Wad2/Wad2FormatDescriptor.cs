#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Wad2;

/// <summary>
/// WAD2 texture/lump archive used by Quake (WAD3 variant used by GoldSrc/Half-Life).
///
/// References:
/// <list type="bullet">
///   <item><description>id Software "Quake Specifications" v3.4 — documents the WAD2 lump directory</description></item>
///   <item><description><c>https://developer.valvesoftware.com/wiki/WAD</c> — Valve Developer Community — the WAD3 (GoldSrc) variant</description></item>
/// </list>
/// </summary>
public sealed class Wad2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the WAD2 archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the WAD2 archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new Wad2Reader(stream);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new Wad2Writer(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddEntry(n, d);
        }
        return ms.ToArray();
      });
  }


  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new Wad2Reader(archive);
    // 12-byte header
    yield return new DefragBlockInfo(0, 12, DefragBlockKind.MetadataReserved, FileName: "WAD2 Header");
    foreach (var e in r.Entries) {
      if (e.CompressedSize > 0)
        yield return new DefragBlockInfo(e.DataOffset, e.CompressedSize, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  public string Id => "Wad2";
  public string DisplayName => "WAD2/WAD3";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".wad";
  public IReadOnlyList<string> Extensions => [".wad"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("WAD2"u8.ToArray(), Confidence: 0.90),
    new("WAD3"u8.ToArray(), Confidence: 0.90)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("wad2", "WAD2/WAD3")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Quake/Half-Life texture archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Wad2Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.CompressedSize,
      e.Compression == 0 ? "Stored" : "LZSS", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Wad2Reader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
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
    var r = new Wad2Reader(archive);
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
    using var w = new Wad2Writer(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddEntry(name, data);
  }

  // ── IArchiveModifiable (in-place) ─────────────────────────────────────

  /// <summary>
  /// Appends (or replaces by name) entries inside an existing WAD2/WAD3 archive.
  /// WAD's directory lives at the END of the file with a pointer in the 12-byte
  /// header, so Add only has to:
  /// <list type="number">
  ///   <item>Truncate the trailing directory.</item>
  ///   <item>Append the new entry's bytes at the new EOF.</item>
  ///   <item>Re-emit the directory (old entries + new entry).</item>
  ///   <item>Patch the 4-byte numEntries and 4-byte dirOffset fields in the header.</item>
  /// </list>
  /// The 4-byte magic at <c>[0, 4)</c> and the data region <c>[12, oldDirOffset)</c>
  /// survive byte-identical — that's the contract.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs)) {
      var entryName = Path.GetFileName(name);
      Wad2Modifier.RemoveEntry(archive, entryName);
      Wad2Modifier.AddEntry(archive, entryName, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing WAD2/WAD3 archive. For each
  /// entry, walks the directory to find it, rewrites the data region with
  /// that entry's bytes dropped, re-emits the directory, and patches the
  /// header.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      Wad2Modifier.RemoveEntry(archive, Path.GetFileName(name));
  }
}
