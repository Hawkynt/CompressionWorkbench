#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Erofs;

/// <summary>
/// Descriptor for EROFS images. Reading covers the uncompressed + inline inode layouts;
/// creation produces a minimal uncompressed (FLAT_PLAIN) image via <see cref="ErofsWriter"/>.
/// Full-fidelity, compressed images remain the job of <c>mkfs.erofs</c>; our writer targets
/// the round-trippable WORM subset (compact inodes, plain data, nested directories).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://docs.kernel.org/filesystems/erofs.html</c> — Linux kernel EROFS documentation (on-disk overview)</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/erofs</c> — mainline implementation (<c>erofs_fs.h</c> defines the on-disk structures)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/EROFS</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class ErofsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IArchiveCreatable, IFormatOptionsSchema, ILayoutOptimizable {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The one tunable the uncompressed writer honours: the volume label written
  /// into the superblock <c>volume_name</c> field (16 bytes) via
  /// <see cref="ErofsWriter.VolumeName"/> and read back as
  /// <c>ErofsReader.VolumeName</c>. The 4&#160;KB block size is fixed by the
  /// FLAT_PLAIN/FLAT_INLINE layout, so it is not exposed.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 16),
  ];

  public string Id => "Erofs";
  public string DisplayName => "EROFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".erofs";
  public IReadOnlyList<string> Extensions => [".erofs", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Magic sits at offset 1024 (start of superblock). Value is 0xE0F5E1E2 stored
    // little-endian, so the on-disk byte sequence is E2 E1 F5 E0.
    new([0xE2, 0xE1, 0xF5, 0xE0], Offset: 1024, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Android read-only compressed filesystem; uncompressed + inline inode layouts.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var reader = OpenReader(stream);
    var result = new List<ArchiveEntryInfo>(reader.Entries.Count);
    for (var i = 0; i < reader.Entries.Count; ++i) {
      var e = reader.Entries[i];
      result.Add(new ArchiveEntryInfo(
        Index: i,
        Name: e.Path,
        OriginalSize: e.Size,
        CompressedSize: e.Size,
        Method: "stored",
        IsDirectory: e.IsDirectory,
        IsEncrypted: false,
        LastModified: null,
        IsSymlink: e.IsSymlink,
        LinkTarget: e.LinkTarget));
    }
    return SymlinkResolver.Resolve(result);
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var reader = OpenReader(stream);
    foreach (var e in reader.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Path, files))
        continue;
      try {
        var data = reader.ExtractFile(e);
        FormatHelpers.WriteFile(outputDir, e.Path, data);
      } catch (NotSupportedException) {
        // Compressed-inode entry we can't decode yet; write an empty placeholder so
        // the user sees it exists but the content is unavailable.
        FormatHelpers.WriteFile(outputDir, e.Path + ".compressed-unsupported", []);
      }
    }
  }

  /// <summary>
  /// Opens a single EROFS file as a bounded read-only stream. The reader
  /// produces the decoded file bytes; the matched bytes are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var reader = OpenReader(archive);
    foreach (var e in reader.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Path, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      byte[] bytes;
      try { bytes = reader.ExtractFile(e); }
      catch (NotSupportedException) { bytes = System.Array.Empty<byte>(); }
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
    var writer = new ErofsWriter();
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (!string.IsNullOrEmpty(label))
      writer.VolumeName = label;
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed to lay the image out; reading a large input
      // into a byte[] would cap it at what an array can hold.
      if (info.InMemoryContent is { } bytes)
        writer.AddFile(info.ArchiveName, bytes);
      else
        writer.AddStreamingFile(info.ArchiveName, new FileInfo(info.FullPath).Length,
                                () => File.OpenRead(info.FullPath));
    }
    writer.WriteTo(output);
  }

  private static ErofsReader OpenReader(Stream stream) {
    // Straight from the stream: copying the image into a byte[] capped the
    // reader at the array limit, which EROFS's block addresses do not.
    if (stream.CanSeek) return new ErofsReader(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return new ErofsReader(ms.ToArray());
  }
}
