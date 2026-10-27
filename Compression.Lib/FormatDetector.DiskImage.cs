using System.Collections.Concurrent;
using Compression.Core.DiskImage;
using Compression.Registry;
using Reg = Compression.Registry.FormatRegistry;

namespace Compression.Lib;

/// <summary>
///   Content-first identification of disk images. A name like <c>.img</c>, <c>.raw</c>, <c>.dd</c>
///   or <c>.bin</c> — or no extension at all — says nothing about whether the bytes are a
///   partitioned whole disk, a bare filesystem or an optical image, so for those the bytes decide.
/// </summary>
public static partial class FormatDetector {

  /// <summary>
  ///   Extensions that name "some disk image" without saying which. For these the content is
  ///   consulted before any extension claimant; a claimant only gets the file when the content
  ///   proves nothing.
  /// </summary>
  private static readonly HashSet<string> GenericDiskImageExtensions = new(StringComparer.OrdinalIgnoreCase) {
    ".img", ".ima", ".image", ".bin", ".raw", ".dd", ".dsk", ".hdd", ".hda", ".disk", ".iso", ".vfd", ".flp",
  };

  /// <summary>A signature this confident, this long, inside sector 0, outranks any structural reading.</summary>
  private const double StrongSignatureConfidence = 0.9;

  /// <summary>Filesystem signatures below this confidence are too weak to claim a disk image on their own.</summary>
  private const double FilesystemSignatureConfidence = 0.75;

  private const int SectorSize = 512;

  /// <summary>Bytes the structural stages need: ISO 9660/UDF volume descriptors end before 40 KiB.</summary>
  private const int StructuralProbeBytes = 40 * 1024;

  private static int _maxFilesystemMagicExtent;
  private static IFormatDescriptor[]? _filesystemDescriptors;
  private static IFormatDescriptor[]? _opticalDescriptors;

  private static IFormatDescriptor[] FilesystemDescriptors {
    get {
      if (FormatDetector._filesystemDescriptors != null)
        return FormatDetector._filesystemDescriptors;

      EnsureRegistryMapped();
      var list = new List<IFormatDescriptor>();
      var deepest = StructuralProbeBytes;
      foreach (var id in Reg.FilesystemFormatIds) {
        if (Reg.GetById(id) is not { } desc || !FormatDetector._idToFormat!.ContainsKey(desc.Id))
          continue;
        list.Add(desc);
        foreach (var sig in desc.MagicSignatures)
          deepest = Math.Max(deepest, sig.Offset + sig.Bytes.Length);
      }
      FormatDetector._maxFilesystemMagicExtent = Math.Min(deepest, MaxProbeBytes);
      FormatDetector._opticalDescriptors = list.Where(d => d.Id is "Iso" or "Udf").ToArray();
      return FormatDetector._filesystemDescriptors = list.ToArray();
    }
  }

  /// <summary>
  ///   Identifies a disk image by its structure. Stages, first hit wins:
  ///   <list type="number">
  ///     <item><description>a strong signature (≥ 0.9 confidence, ≥ 4 bytes) inside sector 0 —
  ///       virtual-disk containers, compressed streams, filesystems that start at byte 0;</description></item>
  ///     <item><description>ISO 9660 / UDF volume descriptors at 32 KiB — ahead of any partition
  ///       table, so a hybrid ISO whose system area carries an MBR or GPT browses as the ISO;</description></item>
  ///     <item><description>a GPT header whose CRC32 matches → <see cref="Format.PartitionedDisk"/>;</description></item>
  ///     <item><description>sector 0 ending in 0x55AA: a validated MBR table versus a volume boot record
  ///       (FAT BPB, NTFS/exFAT/… OEM signature). A valid table with no boot record is the disk; a boot
  ///       record with no valid table is the filesystem; when both parse, the disk wins only if one of
  ///       its partitions holds a recognisable filesystem;</description></item>
  ///     <item><description>an Apple Partition Map → <see cref="Format.PartitionedDisk"/>;</description></item>
  ///     <item><description>when <paramref name="includeSuperblocks"/> is set, a filesystem superblock
  ///       signature (≥ 0.75 confidence) anywhere up to the deepest registered one (ZFS at 128 KiB).</description></item>
  ///   </list>
  ///   Reads at most the first 40 KiB, and only for the last stage out to the deepest filesystem
  ///   signature; never scans the image.
  /// </summary>
  internal static Format DetectDiskImageByContent(Stream stream, bool includeSuperblocks) {
    if (!stream.CanSeek || stream.Length < SectorSize) return Format.Unknown;

    var filesystems = FilesystemDescriptors;
    var length = stream.Length;
    var head = ReadPrefix(stream, (int)Math.Min(StructuralProbeBytes, length));

    var strong = BestMatch(head.AsSpan(0, Math.Min(SectorSize, head.Length)), Reg.All,
      StrongSignatureConfidence, minLength: 4, maxExtent: SectorSize);
    if (strong != Format.Unknown) return strong;

    var optical = BestMatch(head, FormatDetector._opticalDescriptors!, 0, minLength: 1, maxExtent: int.MaxValue);
    if (optical != Format.Unknown) return optical;

    if (GptParser.HasValidHeader(head)) return Format.PartitionedDisk;

    if (MbrParser.IsMbr(head)) {
      var sector0 = head.AsSpan(0, SectorSize);
      var bootRecord = BestMatch(sector0, filesystems, FilesystemSignatureConfidence, minLength: 4, maxExtent: SectorSize);
      if (bootRecord == Format.Unknown && IsFatBootSector(sector0)) bootRecord = Format.Fat;

      if (MbrParser.HasValidPartitionTable(sector0, length)
          && (bootRecord == Format.Unknown || AnyPartitionHoldsFilesystem(stream)))
        return Format.PartitionedDisk;
      if (bootRecord != Format.Unknown) return bootRecord;
    }

    if (ApmParser.IsApm(head)) return Format.PartitionedDisk;

    if (!includeSuperblocks) return Format.Unknown;

    var deep = (int)Math.Min(FormatDetector._maxFilesystemMagicExtent, length);
    var probe = deep > head.Length ? ReadPrefix(stream, deep) : head;
    return BestSuperblockMatch(probe, filesystems);
  }

  /// <summary>
  ///   Identifies a seekable stream by content alone — for an entry opened inside a container
  ///   (a partition window, a file inside a filesystem image) where there is no file to name
  ///   and copying the entry out just to look at its header would cost its full size. Runs the
  ///   disk-image probe first, then the registry's confidence-ranked signatures, reading only
  ///   as far as the deepest registered signature. The stream position is restored.
  /// </summary>
  public static Format DetectByContent(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("Content detection needs a seekable stream.", nameof(stream));

    EnsureRegistryMapped();
    var saved = stream.Position;
    try {
      var disk = DetectDiskImageByContent(stream, includeSuperblocks: true);
      if (disk != Format.Unknown) return disk;
      var header = ReadPrefix(stream, (int)Math.Min(FormatDetector._maxMagicExtent, stream.Length));
      return DetectByMagic(header);
    } finally {
      stream.Position = saved;
    }
  }

  /// <summary>Path overload of <see cref="DetectDiskImageByContent(Stream, bool)"/>; unreadable files are Unknown.</summary>
  private static Format DetectDiskImageByContent(string path, bool includeSuperblocks) {
    try {
      if (!File.Exists(path)) return Format.Unknown;
      using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
      return DetectDiskImageByContent(fs, includeSuperblocks);
    } catch (IOException) {
      return Format.Unknown;
    } catch (UnauthorizedAccessException) {
      return Format.Unknown;
    }
  }

  /// <summary>
  ///   Lenient BIOS Parameter Block check — no jump opcode or 0x55AA required — for FAT volumes
  ///   written by systems that skip them. Every field still has to hold a legal value, including
  ///   the media descriptor (0xF0 or 0xF8–0xFF) and a non-zero sector count, so arbitrary bytes do
  ///   not pass.
  /// </summary>
  private static bool HasPlausibleFatBpb(ReadOnlySpan<byte> header) {
    if (header.Length < SectorSize) return false;
    var bytesPerSector = header[11] | (header[12] << 8);
    if (bytesPerSector is not (512 or 1024 or 2048 or 4096)) return false;
    var sectorsPerCluster = header[13];
    if (sectorsPerCluster == 0 || (sectorsPerCluster & (sectorsPerCluster - 1)) != 0 || sectorsPerCluster > 128) return false;
    if ((header[14] | (header[15] << 8)) == 0) return false;
    if (header[16] is not (1 or 2)) return false;
    if (header[21] is not (0xF0 or >= 0xF8)) return false;
    var totalSectors16 = header[19] | (header[20] << 8);
    var totalSectors32 = header[32] | (header[33] << 8) | (header[34] << 16) | (header[35] << 24);
    return totalSectors16 != 0 || totalSectors32 != 0;
  }

  private static bool AnyPartitionHoldsFilesystem(Stream disk) {
    try {
      foreach (var part in PartitionTableDetector.Detect(disk).Partitions) {
        var size = Math.Min(part.Size, disk.Length - part.StartOffset);
        if (size < SectorSize) continue;
        using var window = new PartitionWindowStream(disk, part.StartOffset, size);
        if (InnerFsDetector.Detect(window) != null) return true;
      }
    } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) {
      /* an unreadable table holds nothing */
    }
    return false;
  }

  /// <summary>
  ///   Best filesystem superblock match. Two-byte signatures at the very start of the image are
  ///   left out: with nothing but a byte pair to go on they claim arbitrary data, and the
  ///   formats that use them (Atari 8-bit, MSA, JFFS2) are still reached by extension.
  /// </summary>
  private static Format BestSuperblockMatch(ReadOnlySpan<byte> header, IFormatDescriptor[] filesystems) {
    var best = Format.Unknown;
    var bestConfidence = 0.0;
    foreach (var desc in filesystems) {
      foreach (var sig in desc.MagicSignatures) {
        if (sig.Confidence < FilesystemSignatureConfidence || sig.Confidence <= bestConfidence) continue;
        if (sig.Bytes.Length < 4 && sig.Offset < SectorSize) continue;
        if (!MatchesMagic(header, sig)) continue;
        bestConfidence = sig.Confidence;
        best = FormatDetector._idToFormat![desc.Id];
      }
    }
    return best;
  }

  private static Format BestMatch(ReadOnlySpan<byte> header, IEnumerable<IFormatDescriptor> descriptors,
      double minConfidence, int minLength, int maxExtent) {
    var best = Format.Unknown;
    var bestConfidence = 0.0;
    foreach (var desc in descriptors) {
      if (!FormatDetector._idToFormat!.TryGetValue(desc.Id, out var format)) continue;
      foreach (var sig in desc.MagicSignatures) {
        if (sig.Confidence < minConfidence || sig.Confidence <= bestConfidence) continue;
        if (sig.Bytes.Length < minLength || sig.Offset + sig.Bytes.Length > maxExtent) continue;
        if (!MatchesMagic(header, sig)) continue;
        bestConfidence = sig.Confidence;
        best = format;
      }
    }
    return best;
  }

  private static byte[] ReadPrefix(Stream stream, int length) {
    var buffer = new byte[length];
    stream.Position = 0;
    var read = stream.ReadAtLeast(buffer, length, throwOnEndOfStream: false);
    return read == length ? buffer : buffer[..read];
  }

  // ── cached detection for callers that ask repeatedly ─────────────

  private static readonly ConcurrentDictionary<string, (long Length, DateTime LastWriteUtc, Format Format)> _detectCache
    = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

  private const int DetectCacheCapacity = 512;

  /// <summary>
  ///   <see cref="Detect(string)"/> with a small cache keyed on the full path, the file's length
  ///   and its last-write time, so UI code that re-evaluates a command's state on every repaint
  ///   does not re-read headers each time. A file that changes on disk is detected afresh.
  /// </summary>
  public static Format DetectCached(string path) {
    ArgumentException.ThrowIfNullOrEmpty(path);
    string full;
    FileInfo info;
    try {
      full = Path.GetFullPath(path);
      info = new FileInfo(full);
    } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) {
      return Detect(path);
    }
    if (!info.Exists) return Detect(full);

    var length = info.Length;
    var stamp = info.LastWriteTimeUtc;
    if (FormatDetector._detectCache.TryGetValue(full, out var hit) && hit.Length == length && hit.LastWriteUtc == stamp)
      return hit.Format;

    var format = Detect(full);
    if (FormatDetector._detectCache.Count >= DetectCacheCapacity) FormatDetector._detectCache.Clear();
    FormatDetector._detectCache[full] = (length, stamp, format);
    return format;
  }
}
