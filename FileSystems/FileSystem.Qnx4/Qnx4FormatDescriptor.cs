#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Qnx4;

/// <summary>
/// R/W descriptor for QNX4 filesystem images. QNX4 has no fixed magic
/// at the start of the image — detection relies on the inode status byte
/// pattern in the root directory cluster (block 1).
///
/// <para>Add / Remove are routed through <see cref="Qnx4Modifier"/>, which
/// mutates the root cluster (LBA 1-4) and the <c>.bitmap</c> (LBA 5) in
/// place. Scope stays flat-root (29 user files) — past that Add throws
/// <see cref="NotSupportedException"/>, matching the WORM writer's capacity
/// guard. Subdirectory emission is still out of scope.</para>
/// </summary>
public sealed class Qnx4FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "Qnx4";
  public string DisplayName => "QNX4 FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".qnx4";
  public IReadOnlyList<string> Extensions => [".qnx4", ".qnx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // QNX4 has no fixed superblock magic. Detection looks for any of the
    // recognised "live inode" status bytes at offset 0x23D (= block 1, first
    // inode entry's di_status field). Status bytes accepted:
    //   0x01 = QNX4_FILE_USED  (Linux-friendly short-name file)
    //   0x08 = QNX4_FILE_LINK  (long-name continuation marker — historical QNX4-utils)
    //   0x09 = QNX4_FILE_USED|LINK (root self-reference emitted by our writer)
    new([0x01], Offset: 0x23D, Confidence: 0.35),
    new([0x08], Offset: 0x23D, Confidence: 0.35),
    new([0x09], Offset: 0x23D, Confidence: 0.40),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "QNX4 filesystem image (1991-2001, QNX Software Systems) — R/W (flat root, max 29 user files).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Qnx4Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Qnx4Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new Qnx4Reader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  // ── IArchiveCreatable ───────────────────────────────────────────────────
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new Qnx4Writer();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }

  // ── IArchiveModifiable ──────────────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces by name) files inside an existing QNX4 image. Routed
  /// through <see cref="Qnx4Modifier.AddFile"/>: the root cluster + bitmap
  /// are mutated in place, the new file's data extent is allocated from the
  /// bitmap, and the inode lands in the first free slot (entries 3..31).
  /// </summary>
  /// <exception cref="NotSupportedException">Root cluster full (29 user
  /// files). The flat-root scope matches the WORM writer's capacity guard.</exception>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FlatFiles(inputs))
      Qnx4Modifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes the named entries from an existing QNX4 image. Routed through
  /// <see cref="Qnx4Modifier.RemoveFile"/>: the dirent is located in the
  /// root cluster, the extent is freed in the bitmap, data blocks are
  /// zero-wiped, and the inode slot is cleared.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      Qnx4Modifier.RemoveFile(archive, name, wipeData: true);
  }
}
