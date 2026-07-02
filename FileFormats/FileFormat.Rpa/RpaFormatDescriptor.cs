#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Rpa;

/// <summary>
/// Ren'Py visual-novel resource archive (RPA) — pickle-encoded index, zlib-compressed header.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/renpy/renpy</c> — Ren'Py engine — its loader defines the RPA format</description></item>
///   <item><description><c>https://github.com/Shizmob/rpatool</c> — rpatool — standalone RPA reader/writer</description></item>
///   <item><description><c>https://www.renpy.org/</c> — Ren'Py project home</description></item>
/// </list>
/// </summary>
public sealed class RpaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveLayoutMap, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IWipeEmpty {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    RpaReader r;
    try {
      archive.Position = 0;
      r = new RpaReader(archive);
    } catch {
      yield break;
    }
    // Header line (text, up to ~34 bytes) before the index offset
    yield return new DefragBlockInfo(0, Math.Min(r.IndexOffset, 64), DefragBlockKind.MetadataReserved, FileName: "RPA Header");
    // File data regions
    foreach (var e in r.Entries) {
      if (e.Length > 0 && e.Offset >= 0)
        yield return new DefragBlockInfo(e.Offset, e.Length, DefragBlockKind.Used, FileName: e.Path);
    }
    // Index (pickle) at end of file
    if (r.IndexOffset > 0 && r.IndexOffset < archive.Length) {
      var idxLen = archive.Length - r.IndexOffset;
      yield return new DefragBlockInfo(r.IndexOffset, idxLen, DefragBlockKind.MetadataReserved, FileName: "Pickle Index");
    }
  }

  public string Id => "Rpa";
  public string DisplayName => "Ren'Py Archive";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable archive. Add/Replace/Remove go through the verified extract ->
  // edit -> re-create rebuild (default IArchiveModifiable); relayouting the container
  // on edit is honest R/W. See FormatCapabilities.cs (WORM vs R/W).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".rpa";
  public IReadOnlyList<string> Extensions => [".rpa"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("RPA-2.0 "u8.ToArray(), Confidence: 0.95),
    new("RPA-3.0 "u8.ToArray(), Confidence: 0.95),
    new("RPA-3.2 "u8.ToArray(), Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rpa", "Ren'Py RPA")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Ren'Py visual-novel resource archive (pickle-indexed, zlib header)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new RpaReader(stream);
    var list = new List<ArchiveEntryInfo>();
    int idx = 0;

    // Always surface passthrough + metadata
    list.Add(new ArchiveEntryInfo(idx++, "FULL.rpa", stream.Length, stream.Length, "Stored", false, false, null));
    list.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "Stored", false, false, null));

    foreach (var e in r.Entries)
      list.Add(new ArchiveEntryInfo(idx++, e.Path, e.Length, e.Length, "Stored", false, false, null));
    return list;
  }

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. Handles three
  /// synthetic shapes: <c>FULL.rpa</c> (a passthrough view of the entire
  /// archive), <c>metadata.ini</c> (built on the fly), and the regular
  /// pickle-indexed entries whose bytes are decoded by the reader. All
  /// returns are wrapped in
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to their logical length so adjacent regions can't leak.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    if (string.Equals(entryName, "FULL.rpa", StringComparison.OrdinalIgnoreCase)) {
      // Passthrough view of the whole archive.
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new Compression.Registry.Streaming.ReadOnlyStreamSlice(archive, 0, archive.Length),
        archive.Length, leaveOpen: false);
    }
    var r = new RpaReader(archive);
    if (string.Equals(entryName, "metadata.ini", StringComparison.OrdinalIgnoreCase)) {
      var sb = new StringBuilder();
      sb.AppendLine("[rpa]");
      sb.AppendLine($"version={r.Version}");
      sb.AppendLine($"index_offset=0x{r.IndexOffset:X}");
      if (r.XorKey != 0)
        sb.AppendLine($"xor_key=0x{r.XorKey:X8}");
      sb.AppendLine($"file_count={r.Entries.Count}");
      sb.AppendLine($"pickle_parsed={r.PickleParsed}");
      var meta = Encoding.UTF8.GetBytes(sb.ToString());
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(meta, writable: false), meta.Length, leaveOpen: false);
    }
    foreach (var e in r.Entries) {
      if (!string.Equals(e.Path, entryName, StringComparison.OrdinalIgnoreCase)) continue;
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

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new RpaReader(stream);

    // FULL passthrough
    if (files == null || MatchesFilter("FULL.rpa", files)) {
      stream.Position = 0;
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      WriteFile(outputDir, "FULL.rpa", ms.ToArray());
    }

    // metadata.ini
    if (files == null || MatchesFilter("metadata.ini", files)) {
      var sb = new StringBuilder();
      sb.AppendLine("[rpa]");
      sb.AppendLine($"version={r.Version}");
      sb.AppendLine($"index_offset=0x{r.IndexOffset:X}");
      if (r.XorKey != 0)
        sb.AppendLine($"xor_key=0x{r.XorKey:X8}");
      sb.AppendLine($"file_count={r.Entries.Count}");
      sb.AppendLine($"pickle_parsed={r.PickleParsed}");
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(sb.ToString()));
    }

    // TODO: if pickle parse is fragile on future RPA revisions, the reader sets PickleParsed=false
    //       and we only surface FULL + metadata. Known to work on RPA-2.0 / 3.0 / 3.2 protocol-2 pickles.
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      WriteFile(outputDir, e.Path, r.Extract(e));
    }
  }

  /// <summary>
  /// Creates an RPA-3.0 archive at <paramref name="output"/> containing
  /// <paramref name="inputs"/>. Synthetic entries from the listing layer
  /// (<c>FULL.rpa</c>, <c>metadata.ini</c>) are skipped automatically so
  /// round-trips through Extract→Create don't accidentally embed the
  /// passthrough copy.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    using var w = new RpaWriter(output, leaveOpen: true);
    foreach (var (name, data) in FilesOnly(inputs)) {
      if (string.Equals(name, "FULL.rpa", StringComparison.OrdinalIgnoreCase)) continue;
      if (string.Equals(name, "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
      w.AddEntry(name, data);
    }
  }

  /// <summary>
  /// Zeros every dead byte in the archive: any byte not covered by a live extent
  /// in the layout map (headers, entry data and directory structures are live and
  /// preserved, so the archive still lists and extracts identically). Cluster-tip
  /// wiping is N/A (entries are stored byte-exact with no per-file slack).
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = this.EnumerateLayout(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }
}
