#pragma warning disable CS1591
using System.Text.RegularExpressions;
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
/// whose CRC32 matches (at 512- or 4096-byte logical blocks), a legacy MBR whose four entries all
/// carry a legal boot indicator and describe non-overlapping ranges that start inside the image,
/// an Apple Partition Map, or a checksummed SGI volume header, Sun label, stand-alone BSD
/// disklabel or Amiga Rigid Disk Block. No extension is claimed either: <c>.img</c>,
/// <c>.raw</c>, <c>.dd</c> and <c>.bin</c> are just as often a bare filesystem or something else
/// entirely, and it is the content that decides.
/// </para>
/// <para>
/// Partitions are read through a window over the image, never copied out, so browsing a
/// multi-gigabyte disk touches only the sectors each filesystem reader asks for.
/// </para>
/// <para>
/// Editing goes through two paths. A file inside a partition is added or removed by that
/// partition's own filesystem editor, working in place on a staged window over the partition —
/// refused when the filesystem has no lossless in-place editor, and committed only when the whole
/// edit succeeds. Whole partitions are added (a root-level filesystem or <c>*.raw</c> image) and
/// deleted (<c>PartitionN_Type</c>) on GPT and MBR tables. <see cref="Create"/> lays partition
/// images out on a fresh GPT or MBR disk.
/// </para>
/// References:
/// <list type="bullet">
///   <item><description>UEFI Specification 2.10, §5.2 (legacy MBR) and §5.3 (GUID Partition Table)</description></item>
///   <item><description>Apple, "Inside Macintosh: Devices", chapter 3 (Apple Partition Map)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Disk_image</c> — overview</description></item>
/// </list>
/// </remarks>
public sealed class PartitionedDiskFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveModifiable, IArchiveCreatable,
    IArchiveWriteConstraints {
  /// <summary>Gets the id.</summary>
  public string Id => "PartitionedDisk";
  /// <summary>Gets the display name.</summary>
  public string DisplayName => "Partitioned disk image";
  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
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
    "Raw whole-disk image (GPT, MBR, APM, BSD, Amiga RDB, Sun, SGI); each partition browses as the filesystem inside it.";

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
  /// filesystem is opened by that filesystem's reader over the same window — for a BSD slice,
  /// the window over the slice.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

    var targets = PartitionedDiskLister.EnumerateTargets(archive, PartitionTableDetector.Detect(archive));
    if (PartitionedDiskLister.Resolve(targets, entryName, out var innerPath) is { } target) {
      var window = new PartitionWindowStream(archive, target.Offset, target.Size);
      if (innerPath.Length == 0)
        return new ReadOnlyWindow(window);
      if (InnerFsDetector.Detect(window) is IArchiveFormatOperations inner) {
        window.Position = 0;
        return inner.OpenEntry(window, innerPath, password);
      }
    }

    return ((IArchiveFormatOperations)new ExtractingView(this)).OpenEntry(archive, entryName, password);
  }

  // ── IArchiveModifiable ─────────────────────────────────────────────

  /// <summary>
  /// Adds files inside partitions (<c>PartitionN_Type/path</c>) through each partition
  /// filesystem's own in-place editor, and root-level filesystem or <c>*.raw</c> images as new
  /// partitions. Refusals leave the disk byte-identical: see
  /// <see cref="PartitionedDiskLister.TryAdd"/>.
  /// </summary>
  /// <exception cref="InvalidOperationException">The edit was refused; nothing was written.</exception>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    if (!PartitionedDiskLister.TryAdd(archive, inputs))
      throw new InvalidOperationException("The image carries no partition table that can be edited.");
  }

  /// <summary>
  /// Removes files inside partitions through each partition filesystem's own in-place editor,
  /// and deletes whole partitions (<c>PartitionN_Type</c>) from GPT and MBR tables. Refusals
  /// leave the disk byte-identical: see <see cref="PartitionedDiskLister.TryRemove"/>.
  /// </summary>
  /// <exception cref="InvalidOperationException">The edit was refused; nothing was written.</exception>
  /// <exception cref="FileNotFoundException">A name addresses nothing on the disk; nothing was written.</exception>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!PartitionedDiskLister.TryRemove(archive, entryNames ?? []))
      throw new InvalidOperationException("The image carries no partition table that can be edited.");
  }

  /// <summary>
  /// Not offered: emptying a whole disk would mean deleting every partition, which is a
  /// partition-table decision, not a maintenance verb — each volume is maintained on its own.
  /// </summary>
  public bool CanPurgeToEmpty => false;

  // ── IArchiveWriteConstraints ───────────────────────────────────────

  /// <summary>
  /// Inside a partition (<c>PartitionN_Type/…</c>) any file goes to that partition's filesystem,
  /// which has the last word. At the disk root only partition images belong: a filesystem image,
  /// or a raw image named <c>*.raw</c>/<c>*.img</c>/<c>PartitionN_…</c>.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    ArgumentNullException.ThrowIfNull(input);
    reason = null;
    var name = input.ArchiveName.Replace('\\', '/').Trim('/');
    if (input.IsDirectory || name.Contains('/') || IsNamedPartitionImage(name)) return true;
    try {
      using var content = OpenInput(input);
      if (DetectPartitionType(content) is not null) return true;
    } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
      // unreadable: refuse below
    }
    reason = $"'{name}' is not a partition image; open a partition's folder to add files to its filesystem.";
    return false;
  }

  /// <summary>No ceiling of its own: each partition's filesystem bounds what it holds.</summary>
  public long? MaxTotalArchiveSize => null;

  /// <summary>Summary for drop tooltips.</summary>
  public string AcceptedInputsDescription =>
    "inside a partition folder: files for its filesystem; at the disk root: filesystem or *.raw/*.img partition images";

  private static bool IsNamedPartitionImage(string name) {
    var leaf = Path.GetFileName(name);
    return leaf.EndsWith(".raw", StringComparison.OrdinalIgnoreCase)
           || leaf.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
           || PartitionName.IsMatch(leaf);
  }

  // ── IArchiveCreatable ──────────────────────────────────────────────

  private static readonly Regex PartitionName = new(@"^Partition(\d+)_", RegexOptions.CultureInvariant);

  /// <summary>
  /// Writes a fresh disk holding one partition per input image. An input must be a filesystem
  /// image (recognised by content) or be named as a raw partition image (<c>*.raw</c>,
  /// <c>*.img</c>, or <c>PartitionN_…</c> as this format lists them); a loose file is refused.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Layout: the first partition starts at 1 MiB and each further one at the next 1 MiB
  /// boundary — the alignment <c>sgdisk</c>, <c>parted</c> and Windows use — sized to its image
  /// rounded up to whole sectors. Inputs named <c>PartitionN_…</c> are placed in N order,
  /// others in the order given. The partition type follows the image's filesystem.
  /// </para>
  /// <para>
  /// Options (<see cref="FormatCreateOptions.FormatSpecific"/>): <c>scheme</c> = <c>gpt</c>
  /// (default, up to 128 partitions, named after the input) or <c>mbr</c> (up to 4 primaries,
  /// below 2^32 sectors); <c>sectorSize</c> = <c>512</c> (default) or <c>4096</c> for a 4Kn disk.
  /// </para>
  /// </remarks>
  /// <exception cref="ArgumentException">No inputs, a loose file, or an unknown option value.</exception>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    options ??= new FormatCreateOptions();

    var scheme = (options.GetString("scheme") ?? "gpt").ToLowerInvariant() switch {
      "gpt" => PartitionScheme.Gpt,
      "mbr" or "dos" or "msdos" => PartitionScheme.Mbr,
      var other => throw new ArgumentException($"Unknown partition scheme '{other}'; use gpt or mbr.", nameof(options)),
    };
    var sectorSize = int.TryParse(options.GetString("sectorSize") ?? "512", out var ss) ? ss : 0;
    if (!PartitionEditor.SupportedSectorSizes.Contains(sectorSize))
      throw new ArgumentException("sectorSize must be 512 or 4096.", nameof(options));

    var images = inputs.Where(i => !i.IsDirectory && !string.IsNullOrEmpty(i.ArchiveName)).ToList();
    if (images.Count == 0)
      throw new ArgumentException("A partitioned disk needs at least one partition image.", nameof(inputs));
    if (images.All(i => PartitionName.IsMatch(Path.GetFileName(i.ArchiveName))))
      images = [.. images.OrderBy(i => int.Parse(PartitionName.Match(Path.GetFileName(i.ArchiveName)).Groups[1].Value,
        System.Globalization.CultureInfo.InvariantCulture))];

    var capacity = scheme == PartitionScheme.Gpt ? 128 : 4;
    if (images.Count > capacity)
      throw new ArgumentException($"A {scheme} table holds at most {capacity} partitions; {images.Count} images were given.", nameof(inputs));

    // Plan the layout before writing anything.
    const long alignment = 1024 * 1024;
    var layout = new List<(ArchiveInputInfo Input, long Start, long Length, PartitionType Type)>();
    var cursor = alignment;
    foreach (var image in images) {
      using var content = OpenInput(image);
      var type = DetectPartitionType(content);
      if (type is null && !IsNamedPartitionImage(image.ArchiveName))
        throw new ArgumentException(
          $"'{image.ArchiveName}' is neither a filesystem image nor a raw partition image (*.raw/*.img); a disk holds partitions, not loose files.",
          nameof(inputs));
      if (content.Length == 0)
        throw new ArgumentException($"'{image.ArchiveName}' is empty; a partition needs at least one sector.", nameof(inputs));
      var length = (content.Length + sectorSize - 1) / sectorSize * sectorSize;
      layout.Add((image, cursor, length, type ?? PartitionType.Linux));
      cursor = (cursor + length + alignment - 1) / alignment * alignment;
    }

    var lastEnd = layout[^1].Start + layout[^1].Length;
    var trailer = scheme == PartitionScheme.Gpt ? (16384L + sectorSize - 1) / sectorSize * sectorSize + sectorSize : 0;
    var diskLength = (lastEnd + trailer + sectorSize - 1) / sectorSize * sectorSize;
    if (scheme == PartitionScheme.Mbr && diskLength / sectorSize > uint.MaxValue)
      throw new ArgumentException("The images need more than 2^32 sectors, which an MBR cannot address; use scheme=gpt.", nameof(options));

    output.SetLength(0);
    output.SetLength(diskLength);
    var editor = PartitionEditor.CreateEmpty(output, scheme, sectorSize);
    foreach (var (input, start, length, type) in layout) {
      using (var content = OpenInput(input)) {
        output.Position = start;
        content.CopyTo(output);
      }
      editor.AddPartition(start, length, type, scheme == PartitionScheme.Gpt ? GptLabel(input.ArchiveName) : null);
    }
    output.Flush();
  }

  /// <summary>A GPT name from the input's file name: without extension and <c>PartitionN_</c> prefix, at most 36 UTF-16 units.</summary>
  private static string GptLabel(string archiveName) {
    var stem = Path.GetFileNameWithoutExtension(archiveName.Replace('\\', '/'));
    stem = PartitionName.Replace(stem, "");
    return stem.Length > 36 ? stem[..36] : stem;
  }

  private static Stream OpenInput(ArchiveInputInfo input)
    => input.InMemoryContent is { } bytes
      ? new MemoryStream(bytes, writable: false)
      : new FileStream(input.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);

  /// <summary>The partition type an image's filesystem calls for, or <c>null</c> when no filesystem is recognised.</summary>
  private static PartitionType? DetectPartitionType(Stream image) {
    if (image.Length < 512) return null;
    return InnerFsDetector.Detect(image)?.Id switch {
      null => null,
      "Fat" => PartitionType.Fat32Lba,
      "ExFat" or "Ntfs" or "Hpfs" => PartitionType.NtfsExfat,
      "HfsPlus" or "Hfs" => PartitionType.AppleHfsPlus,
      "Apfs" => PartitionType.AppleApfs,
      _ => PartitionType.Linux,
    };
  }

  /// <summary>Exposes List/Extract without the OpenEntry override, to reach the interface default.</summary>
  private sealed class ExtractingView(PartitionedDiskFormatDescriptor owner) : IArchiveFormatOperations {
    public List<ArchiveEntryInfo> List(Stream stream, string? password) => owner.List(stream, password);
    public void Extract(Stream stream, string outputDir, string? password, string[]? files)
      => owner.Extract(stream, outputDir, password, files);
  }

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
