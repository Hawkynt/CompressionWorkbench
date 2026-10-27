#pragma warning disable CS1591
using Compression.Core.DiskImage;
using Compression.Registry;

namespace FileFormat.PartitionedDisk;

/// <summary>
/// A raw whole-disk image — the byte-for-byte dump of a drive, as <c>dd</c>, a USB imager or a
/// virtual machine's flat disk writes it — identified by the partition table it carries rather
/// than by its name. Each partition is listed as a <c>PartitionN_Type/</c> directory holding the
/// filesystem found inside it (detected from the partition's own bytes); a partition whose
/// content is not recognised is listed as a single <c>PartitionN_Type.raw</c> entry.
/// </summary>
/// <remarks>
/// <para>
/// There is no signature to register. The format is claimed by
/// <c>FormatDetector</c>'s disk-image probe only after the table has been validated: a GPT header
/// whose CRC32 matches, a legacy MBR whose four entries all carry a legal boot indicator and
/// describe non-overlapping ranges that start inside the image, or an Apple Partition Map. No
/// extension is claimed either: <c>.img</c>, <c>.raw</c>, <c>.dd</c> and <c>.bin</c> are just
/// as often a bare filesystem or something else entirely, and it is the content that decides.
/// </para>
/// <para>
/// Partitions are read through a window over the image, never copied out, so browsing a
/// multi-gigabyte disk touches only the sectors each filesystem reader asks for.
/// </para>
/// References:
/// <list type="bullet">
///   <item><description>UEFI Specification 2.10, §5.2 (legacy MBR) and §5.3 (GUID Partition Table)</description></item>
///   <item><description>Apple, "Inside Macintosh: Devices", chapter 3 (Apple Partition Map)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Disk_image</c> — overview</description></item>
/// </list>
/// </remarks>
public sealed class PartitionedDiskFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>Gets the id.</summary>
  public string Id => "PartitionedDisk";
  /// <summary>Gets the display name.</summary>
  public string DisplayName => "Partitioned disk image";
  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>Gets the default extension.</summary>
  public string DefaultExtension => ".img";
  /// <summary>Gets the extensions — none: generic disk-image extensions are settled by content.</summary>
  public IReadOnlyList<string> Extensions => [];
  /// <summary>Gets the compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>Gets the magic signatures — none: the partition table is validated structurally.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>Gets the methods.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;
  /// <summary>Gets the family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>Gets the description.</summary>
  public string Description =>
    "Raw whole-disk image (MBR, GPT or APM); each partition browses as the filesystem inside it.";

  private const string WholeDiskEntry = "disk.raw";

  /// <summary>
  /// Lists every partition's filesystem entries under its <c>PartitionN_Type/</c> prefix. A disk
  /// whose table no longer parses degrades to one <c>disk.raw</c> entry instead of throwing.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      stream.Position = 0;
      if (PartitionedDiskLister.List(stream, password) is { } partitioned)
        return partitioned;
    } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) {
      // fall through to the whole-disk view
    }
    return [new ArchiveEntryInfo(0, WholeDiskEntry, stream.Length, stream.Length, "Stored", false, false, null)];
  }

  /// <summary>Extracts each partition's filesystem into its own <c>PartitionN_Type</c> directory.</summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    stream.Position = 0;
    if (PartitionedDiskLister.Extract(stream, outputDir, password, files))
      return;

    if (files != null && !files.Contains(WholeDiskEntry, StringComparer.Ordinal))
      return;
    Directory.CreateDirectory(outputDir);
    using var output = File.Create(Path.Combine(outputDir, WholeDiskEntry));
    stream.Position = 0;
    stream.CopyTo(output);
  }

  /// <summary>
  /// Opens an entry without extracting the partition around it: a <c>PartitionN_Type.raw</c>
  /// entry is a read-only window over the partition's sectors, and a file inside a recognised
  /// filesystem is opened by that filesystem's reader over the same window.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

    var name = entryName.Replace('\\', '/');
    var table = PartitionTableDetector.Detect(archive);
    foreach (var part in table.Partitions) {
      var prefix = PartitionedDiskLister.MakePartitionPrefix(part);

      if (name.Equals(prefix + ".raw", StringComparison.Ordinal))
        return new ReadOnlyWindow(new PartitionWindowStream(archive, part.StartOffset, ClampedSize(archive, part)));

      if (!name.StartsWith(prefix + "/", StringComparison.Ordinal))
        continue;

      var window = new PartitionWindowStream(archive, part.StartOffset, ClampedSize(archive, part));
      if (BsdDisklabelParser.IsDisklabel(window))
        break; // slices nest one level deeper; the lister's own layout handles them
      if (InnerFsDetector.Detect(window) is IArchiveFormatOperations inner) {
        window.Position = 0;
        return inner.OpenEntry(window, name[(prefix.Length + 1)..], password);
      }
    }

    // BSD slices, and anything else the lister nests deeper, take the extracting default.
    return ((IArchiveFormatOperations)new ExtractingView(this)).OpenEntry(archive, entryName, password);
  }

  /// <summary>Exposes List/Extract without the OpenEntry override, to reach the interface default.</summary>
  private sealed class ExtractingView(PartitionedDiskFormatDescriptor owner) : IArchiveFormatOperations {
    public List<ArchiveEntryInfo> List(Stream stream, string? password) => owner.List(stream, password);
    public void Extract(Stream stream, string outputDir, string? password, string[]? files)
      => owner.Extract(stream, outputDir, password, files);
  }

  private static long ClampedSize(Stream disk, PartitionEntry part)
    => Math.Max(0, Math.Min(part.Size, disk.Length - part.StartOffset));

  /// <summary>Read-only view of a partition window: browsing must never write to the disk.</summary>
  private sealed class ReadOnlyWindow(Stream inner) : Stream {
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) {
      if (disposing) inner.Dispose();
      base.Dispose(disposing);
    }
  }
}
