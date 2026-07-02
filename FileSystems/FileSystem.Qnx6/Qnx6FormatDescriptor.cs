#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Qnx6;

/// <summary>
/// Descriptor for QNX6 (Neutrino) filesystem images. Magic 0x68191122 (LE) at
/// file offset 0x2000. Read + R/W (Add/Remove): the writer (<see cref="Qnx6Writer"/>)
/// emits paired superblocks (primary at 0x2000 + identical secondary mirror at
/// the tail of the volume) — the power-safe contract — alongside a flat 128-byte
/// inode array and 32-byte directory entries. The modifier (<see cref="Qnx6Modifier"/>)
/// mutates that layout in place and re-mirrors the superblock to the new tail
/// after each Add/Remove so the dual-superblock pairing remains byte-identical.
/// Self-round-trips through <see cref="Qnx6Reader"/>.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.kernel.org/doc/html/latest/filesystems/qnx6.html</c> — kernel documentation of the on-disk layout (dual superblocks)</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/qnx6</c> — Linux reference implementation</description></item>
///   <item><description>QNX Neutrino <c>fs-qnx6.so</c> documentation (QNX Software Systems)</description></item>
/// </list>
/// </summary>
public sealed class Qnx6FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable {
  public string Id => "Qnx6";
  public string DisplayName => "QNX6 Neutrino FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".qnx6";
  public IReadOnlyList<string> Extensions => [".qnx6"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x22, 0x11, 0x19, 0x68], Offset: 0x2000, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "QNX6 Neutrino filesystem — R/W (paired superblocks; reader walks a single-block directory and direct-extent files; Add/Remove mutate in place with synchronous dual-superblock mirror).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Qnx6Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Qnx6Reader(stream);
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
    var r = new Qnx6Reader(archive);
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

  /// <summary>
  /// Emits a fresh QNX6 image containing <paramref name="inputs"/>. Files are
  /// flattened to leaf names (directory components dropped) — the Stage-1
  /// reader walks a single-block root directory, so a flat layout matches what
  /// it can read back. The output is a complete image: boot region, primary
  /// superblock, inode table, root dir block, file data extents, and a mirror
  /// secondary superblock at the tail.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var image = Qnx6Writer.Build(FlatFiles(inputs).ToList());
    output.Write(image, 0, image.Length);
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing QNX6 image. The
  /// modifier locates a free inode slot, lays down a contiguous data extent
  /// past the current high-water mark, writes the dirent into the single-block
  /// root directory, and re-mirrors the primary superblock to the new tail —
  /// the dual-superblock pairing is updated synchronously so the power-safe
  /// contract holds across the whole sequence.
  /// </summary>
  /// <exception cref="NotSupportedException">When the root directory is full
  /// (the Stage-2 modifier preserves the single-block root limit of 32 dirents).</exception>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FlatFiles(inputs))
      Qnx6Modifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes the named entries from an existing QNX6 image. Data blocks are
  /// zeroed (wipe contract), the inode slot is cleared, and trailing dirents
  /// are compacted into the freed slot so reads see no gap. The secondary
  /// superblock mirror is refreshed afterwards.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    Qnx6Modifier.RemoveFiles(archive, entryNames);
  }
}
