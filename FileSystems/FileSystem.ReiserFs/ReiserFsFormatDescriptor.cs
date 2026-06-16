#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.ReiserFs;

public sealed class ReiserFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable {
  // R/W write constraints — ReiserFS has no inherent ceiling; real mkfs.reiserfs minimum ≈ 128 MB.
  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => 128L * 1024 * 1024;
  public string AcceptedInputsDescription => "ReiserFS v3.6 filesystem image; full multi-leaf S+tree with nested directories and INDIRECT-item file bodies.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  public string Id => "ReiserFs";
  public string DisplayName => "ReiserFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  public string DefaultExtension => ".reiserfs";
  public IReadOnlyList<string> Extensions => [".reiserfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ReIsErFs"u8.ToArray(), Offset: 65536 + 52, Confidence: 0.95),
    new("ReIsEr2Fs"u8.ToArray(), Offset: 65536 + 52, Confidence: 0.95),
    new("ReIsEr3Fs"u8.ToArray(), Offset: 65536 + 52, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// ReiserFS v3.6 filesystem image — R/W. The writer emits a real
  /// spec-compliant multi-leaf S+tree image (superblock at +65536, R5-hashed
  /// directory entries, INDIRECT items with dedicated data blocks for file
  /// bodies &gt; 1 KB, internal pages above leaves). In-place
  /// <see cref="IArchiveModifiable"/> is implemented via read-modify-rebuild:
  /// every existing entry is materialised, the requested edit is applied in
  /// memory, and a fresh image is written back to the stream. This covers
  /// nested paths, leaf splits and merges, multi-leaf descent, INDIRECT-sized
  /// bodies, and root tree-height growth — all paths that previously fell back
  /// to NotSupportedException. The cost is O(image size) per Add / Remove
  /// rather than O(edit-locality), but the result passes reiserfsck.
  /// </summary>
  public string Description => "ReiserFS v3 filesystem image (R/W, full S+tree mutation via rebuild)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ReiserFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ReiserFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single filesystem entry as a bounded read-only stream. The
  /// reader produces the decoded file bytes by walking the entry's extent
  /// or block chain; the matched bytes are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's logical length so cluster/extent slack past the entry's
  /// end is physically unreachable through this view.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new ReiserFsReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
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
    var w = new ReiserFsWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, i.ReadContent());
    }
    w.WriteTo(output);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware ReiserFS v3.6 defragmentor via read-extract-rebuild dispatch
  /// through <see cref="DefragRebuilder"/>. The writer always emits a fresh
  /// contiguous-from-start single-leaf image (superblock at +65536, root SD
  /// + DIRENTRY + per-file SD/DIRECT items, R5-hashed key ordering).
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options, ReadEntries, BuildImage);

  // ── IArchiveModifiable ────────────────────────────────────────────────
  // Read-modify-rebuild via the multi-leaf writer. Every Add and Remove
  // materialises the live entries from the current image, applies the edit
  // in memory, and rewrites the image. Covers nested paths, leaf splits and
  // merges, multi-leaf descent, INDIRECT-sized bodies and root tree-height
  // growth — every path that previously fell back to NotSupportedException.

  /// <summary>
  /// Adds (or replaces, on name collision) the given files inside an existing
  /// ReiserFS image. Routed through <see cref="ReiserFsModifier"/> which does
  /// the full read-modify-rebuild via <see cref="ReiserFsWriter"/>.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      ReiserFsModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing ReiserFS image. The rebuild
  /// always starts from zeroed bytes so the removed file data leaves no
  /// forensic trace.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      ReiserFsModifier.RemoveFile(archive, name, wipeData: true);
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new ReiserFsReader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new ReiserFsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }
}
