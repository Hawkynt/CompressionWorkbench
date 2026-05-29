using Compression.Core.DiskImage;
using Compression.Lib.FsConversion;
using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// Orchestrates partition-editor operations on raw disk images and virtual-disk
/// containers (VHD/VHDX/VMDK/QCOW2/VDI). Detects whether the host file is a
/// container (cast its descriptor to <see cref="IPartitionEditable"/> and open
/// the guest disk) or a raw image (use the host stream directly), then applies
/// the requested edit through <see cref="PartitionEditor"/>.
/// </summary>
/// <remarks>
/// All methods auto-initialize the format registry. The host stream is opened
/// with <see cref="FileAccess.ReadWrite"/>; mutating operations are flushed to
/// disk before the method returns.
/// </remarks>
public static class PartitionOperations {

  /// <summary>
  /// Reports a single partition's metadata.
  /// </summary>
  /// <param name="Index">Zero-based index reported by <see cref="PartitionEditor.ListPartitions"/>.</param>
  /// <param name="Source">"MBR", "MBR (Extended Container)", "EBR", or "GPT".</param>
  /// <param name="StartOffset">Start offset in bytes from the disk origin.</param>
  /// <param name="Size">Partition length in bytes.</param>
  /// <param name="TypeCode">MBR byte (e.g. "0x83") or GPT GUID.</param>
  /// <param name="TypeName">Human-readable type label.</param>
  /// <param name="Name">Partition label (GPT only).</param>
  /// <param name="IsActive">Active/bootable flag.</param>
  public sealed record PartitionInfo(
    int Index,
    string Source,
    long StartOffset,
    long Size,
    string TypeCode,
    string TypeName,
    string Name,
    bool IsActive);

  /// <summary>
  /// Snapshot returned by <see cref="List"/>: the detected scheme plus all
  /// primary and logical entries currently on disk.
  /// </summary>
  public sealed record PartitionListResult(
    PartitionScheme Scheme,
    IReadOnlyList<PartitionInfo> Partitions);

  /// <summary>
  /// Lists all partitions in the given image.
  /// </summary>
  public static PartitionListResult List(string imagePath) {
    FormatRegistration.EnsureInitialized();
    var editable = ResolveEditable(imagePath);
    using var host = OpenHostReadWrite(imagePath);
    using var guest = OpenGuestDisk(host, editable);
    var editor = new PartitionEditor(guest);
    var snapshot = editor.ListPartitions()
      .Select(p => new PartitionInfo(p.Index, p.Source, p.StartOffset, p.Size, p.TypeCode, p.TypeName, p.Name, p.IsActive))
      .ToList()
      .AsReadOnly();
    return new PartitionListResult(editor.Scheme, snapshot);
  }

  /// <summary>
  /// Adds a new primary or logical partition. If an extended container exists
  /// and <paramref name="startByteOffset"/> falls inside it, the entry is added
  /// as a logical partition; otherwise it becomes a primary.
  /// </summary>
  public static void Add(
    string imagePath, long startByteOffset, long lengthBytes,
    PartitionType type, string? label = null) {
    FormatRegistration.EnsureInitialized();
    var editable = ResolveEditable(imagePath);
    using var host = OpenHostReadWrite(imagePath);
    using var guest = OpenGuestDisk(host, editable);
    var editor = new PartitionEditor(guest);

    var extended = editor.ListPartitions().FirstOrDefault(p => p.Source == "MBR (Extended Container)");
    var insideExtended = extended is not null
      && startByteOffset >= extended.StartOffset
      && startByteOffset + lengthBytes <= extended.StartOffset + extended.Size;

    if (insideExtended)
      editor.AddLogicalPartition(startByteOffset, lengthBytes, type, label);
    else
      editor.AddPartition(startByteOffset, lengthBytes, type, label);
  }

  /// <summary>Deletes a partition by index without zero-filling its bytes.</summary>
  public static void Delete(string imagePath, int index) {
    FormatRegistration.EnsureInitialized();
    var editable = ResolveEditable(imagePath);
    using var host = OpenHostReadWrite(imagePath);
    using var guest = OpenGuestDisk(host, editable);
    var editor = new PartitionEditor(guest);
    editor.DeletePartition(index);
  }

  /// <summary>Deletes a partition and zero-fills its on-disk byte range.</summary>
  public static void Purge(string imagePath, int index) {
    FormatRegistration.EnsureInitialized();
    var editable = ResolveEditable(imagePath);
    using var host = OpenHostReadWrite(imagePath);
    using var guest = OpenGuestDisk(host, editable);
    var editor = new PartitionEditor(guest);
    editor.PurgePartition(index);
  }

  /// <summary>
  /// Converts the partition table to <paramref name="targetScheme"/> (MBR or GPT).
  /// No-ops when the image is already in the requested scheme.
  /// </summary>
  public static void Convert(string imagePath, PartitionScheme targetScheme) {
    if (targetScheme is not (PartitionScheme.Mbr or PartitionScheme.Gpt))
      throw new ArgumentException("Target scheme must be Mbr or Gpt.", nameof(targetScheme));

    FormatRegistration.EnsureInitialized();
    var editable = ResolveEditable(imagePath);
    using var host = OpenHostReadWrite(imagePath);
    using var guest = OpenGuestDisk(host, editable);
    var editor = new PartitionEditor(guest);

    if (editor.Scheme == targetScheme) return;
    if (targetScheme == PartitionScheme.Gpt)
      editor.ConvertMbrToGpt();
    else
      editor.ConvertGptToMbr();
  }

  /// <summary>
  /// Writes a fresh filesystem image of <paramref name="formatId"/> into the
  /// partition at <paramref name="index"/>. The generated bytes must fit
  /// within the partition.
  /// </summary>
  public static void Format(string imagePath, int index, string formatId) {
    FormatRegistration.EnsureInitialized();
    var editable = ResolveEditable(imagePath);
    using var host = OpenHostReadWrite(imagePath);
    using var guest = OpenGuestDisk(host, editable);
    var editor = new PartitionEditor(guest);
    editor.FormatPartition(index, formatId, new FormatCreateOptions());
  }

  /// <summary>
  /// Crash-safely migrates every file from the partition at
  /// <paramref name="srcIndex"/> (running <paramref name="srcFormatId"/>) to
  /// the partition at <paramref name="dstIndex"/> (running
  /// <paramref name="dstFormatId"/>, expected to be freshly formatted and
  /// empty). Each file is copied to dst, flushed, then deleted from src and
  /// flushed; a sidecar manifest on dst records progress so a power-fail
  /// anywhere is resumable.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Source must support both <see cref="IArchiveFormatOperations"/> (to list
  /// + extract files) and <see cref="IArchiveModifiable"/> (to delete
  /// migrated files). Destination must support
  /// <see cref="IArchiveFormatOperations"/> + <see cref="IArchiveModifiable"/>
  /// (to add files). The pair (src, dst) must both already be initialised in
  /// their respective formats — this method does not format anything.
  /// </para>
  /// <para>
  /// On crash, re-open the same image and re-call this method. The manifest
  /// inside the destination drives recovery so no extra orchestration is
  /// needed at the call site.
  /// </para>
  /// </remarks>
  public static void MigrateFilesystem(
    string imagePath, int srcIndex, string srcFormatId, int dstIndex, string dstFormatId) {
    FormatRegistration.EnsureInitialized();
    var editable = ResolveEditable(imagePath);
    using var host = OpenHostReadWrite(imagePath);
    using var guest = OpenGuestDisk(host, editable);
    var editor = new PartitionEditor(guest);
    var partitions = editor.ListPartitions();
    if (srcIndex < 0 || srcIndex >= partitions.Count)
      throw new ArgumentOutOfRangeException(nameof(srcIndex));
    if (dstIndex < 0 || dstIndex >= partitions.Count)
      throw new ArgumentOutOfRangeException(nameof(dstIndex));
    if (srcIndex == dstIndex)
      throw new ArgumentException("Source and destination partitions must differ.");

    var src = partitions[srcIndex];
    var dst = partitions[dstIndex];

    // Wrap each partition byte range in a writable sub-stream so the FS
    // descriptors see "their" disk starting at offset 0. The sub-streams
    // share the underlying guest stream; their Flush()es propagate down
    // and ultimately hit the host file.
    using var srcStream = new WritableSubStream(guest, src.StartOffset, src.Size);
    using var dstStream = new WritableSubStream(guest, dst.StartOffset, dst.Size);

    var converter = new MigrationConverter(srcStream, dstStream, srcFormatId, dstFormatId);
    converter.Resume();
  }

  /// <summary>Verifies on-disk MBR/GPT integrity.</summary>
  public static PartitionTableVerification Verify(string imagePath) {
    FormatRegistration.EnsureInitialized();
    var editable = ResolveEditable(imagePath);
    using var host = OpenHostReadWrite(imagePath);
    using var guest = OpenGuestDisk(host, editable);
    var editor = new PartitionEditor(guest);
    return editor.Verify();
  }

  /// <summary>
  /// Attempts to convert the filesystem inside the partition at
  /// <paramref name="index"/> to <paramref name="newFsId"/> via metadata-only
  /// edits — no file data bytes are copied. Supported pairs:
  /// <list type="bullet">
  ///   <item>FAT12 ↔ FAT16 ↔ FAT32 (when geometry permits).</item>
  ///   <item>ext2 → ext3 → ext4 (forward only — downgrades fall through).</item>
  /// </list>
  /// Returns <c>true</c> when the in-place path completed; <c>false</c> when
  /// the source/target pair has no metadata-only conversion (the caller is
  /// expected to fall back to a full extract → reformat → re-import migration
  /// or <see cref="MigrateFilesystem"/> at that point).
  /// </summary>
  public static bool ConvertFilesystem(string imagePath, int index, string newFsId) {
    FormatRegistration.EnsureInitialized();
    var editable = ResolveEditable(imagePath);
    using var host = OpenHostReadWrite(imagePath);
    using var guest = OpenGuestDisk(host, editable);
    var editor = new PartitionEditor(guest);
    return editor.ConvertFilesystem(index, newFsId);
  }

  /// <summary>
  /// Parses a partition-type token (case-insensitive). Accepts the
  /// <see cref="PartitionType"/> enum name (e.g. <c>Linux</c>, <c>fat32lba</c>)
  /// or a few common aliases (<c>fat</c>, <c>fat32</c>, <c>ntfs</c>, <c>exfat</c>,
  /// <c>extended</c>, <c>efi</c>).
  /// </summary>
  public static PartitionType ParseType(string typeName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
    var normalised = typeName.Trim();

    // Aliases first so users don't have to type the exact enum name.
    var aliased = normalised.ToLowerInvariant() switch {
      "fat" or "fat32" => PartitionType.Fat32Lba,
      "fat12" => PartitionType.Fat12,
      "fat16" => PartitionType.Fat16,
      "ntfs" or "exfat" => PartitionType.NtfsExfat,
      "linux" or "ext" or "ext2" or "ext3" or "ext4" => PartitionType.Linux,
      "swap" => PartitionType.LinuxSwap,
      "lvm" => PartitionType.LinuxLvm,
      "raid" => PartitionType.LinuxRaid,
      "hfs" or "hfs+" => PartitionType.AppleHfsPlus,
      "apfs" => PartitionType.AppleApfs,
      "ufs" => PartitionType.AppleUfs,
      "extended" or "ebr" => PartitionType.ExtendedLba,
      "efi" or "esp" => PartitionType.EfiSystem,
      "biosboot" or "bios" => PartitionType.BiosBoot,
      "msr" or "reserved" => PartitionType.MicrosoftReserved,
      "msft" or "basicdata" => PartitionType.MicrosoftBasicData,
      _ => (PartitionType?)null,
    };
    if (aliased is { } a) return a;

    return Enum.TryParse<PartitionType>(normalised, ignoreCase: true, out var parsed)
      ? parsed
      : throw new ArgumentException(
        $"Unknown partition type '{typeName}'. Known: {string.Join(", ", Enum.GetNames<PartitionType>())}.");
  }

  /// <summary>
  /// Parses the target scheme token (case-insensitive): "mbr" or "gpt".
  /// </summary>
  public static PartitionScheme ParseScheme(string scheme) {
    ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
    return scheme.Trim().ToLowerInvariant() switch {
      "mbr" => PartitionScheme.Mbr,
      "gpt" => PartitionScheme.Gpt,
      _ => throw new ArgumentException($"Unknown scheme '{scheme}'. Expected 'mbr' or 'gpt'."),
    };
  }

  // ── helpers ────────────────────────────────────────────────────────

  private static FileStream OpenHostReadWrite(string imagePath) {
    if (!File.Exists(imagePath))
      throw new FileNotFoundException($"File not found: {imagePath}", imagePath);
    return new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
  }

  /// <summary>
  /// Pre-resolves the <see cref="IPartitionEditable"/> descriptor (if any) for
  /// the given image, by invoking <see cref="FormatDetector"/> via its own
  /// short-lived file handle. Doing this before we open the host stream keeps
  /// the detector from clashing with our exclusive write handle.
  /// </summary>
  private static IPartitionEditable? ResolveEditable(string imagePath) {
    if (!File.Exists(imagePath)) return null;
    var format = FormatDetector.Detect(imagePath);
    var desc = FormatRegistry.GetById(format.ToString());
    return desc as IPartitionEditable;
  }

  /// <summary>
  /// Returns a writable guest-disk stream — either the host file itself (for
  /// raw images / unknown formats) or a virtual-disk container's inner view.
  /// For raw images we wrap in a non-owning <see cref="HostStreamView"/> so
  /// downstream code can safely dispose its "guest" stream without closing
  /// the underlying host file.
  /// </summary>
  private static Stream OpenGuestDisk(FileStream host, IPartitionEditable? editable)
    => editable is null
      ? new HostStreamView(host)
      : editable.OpenGuestDiskStream(host);

  /// <summary>
  /// Non-owning passthrough over an existing <see cref="Stream"/>. Used so
  /// PartitionEditor can be given a guest-disk view that doesn't dispose the
  /// underlying host file when the editor's helpers wrap it in
  /// <c>using</c> blocks.
  /// </summary>
  private sealed class HostStreamView(Stream inner) : Stream {
    private readonly Stream _inner = inner;
    public override bool CanRead => this._inner.CanRead;
    public override bool CanSeek => this._inner.CanSeek;
    public override bool CanWrite => this._inner.CanWrite;
    public override long Length => this._inner.Length;
    public override long Position { get => this._inner.Position; set => this._inner.Position = value; }
    public override void Flush() => this._inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => this._inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => this._inner.Seek(offset, origin);
    public override void SetLength(long value) => this._inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => this._inner.Write(buffer, offset, count);
    protected override void Dispose(bool disposing) {
      // Do NOT dispose the underlying host stream — its lifetime is owned by
      // the caller of OpenGuestDisk.
    }
  }
}
