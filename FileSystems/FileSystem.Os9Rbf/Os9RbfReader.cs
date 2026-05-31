#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Os9Rbf;

/// <summary>
/// Reader for Microware OS-9 RBF (Random-Block-File) disk images. The format
/// was used on the Tandy CoCo (OS-9 Level 1/2), Sharp MZ-2500, Atari MSX-OS-9
/// machines, and embedded systems running OS-9/68000 and OS-9000. Sector size
/// is 256 bytes; multi-byte fields are big-endian. The directory tree is walked
/// recursively: files in subdirectories surface with their full slash-separated
/// path and each intermediate directory is reported as its own entry.
/// </summary>
public sealed class Os9RbfReader {

  /// <summary>One file/directory entry parsed from a directory sector.</summary>
  public sealed record FileEntry(
    string Name,
    int FdLsn,
    long ByteLength,
    bool IsDirectory,
    DateTime? Created
  );

  /// <summary>Parsed OS-9 RBF volume.</summary>
  public sealed record Volume(
    string VolumeName,
    int TotalSectors,
    int RootDirLsn,
    IReadOnlyList<FileEntry> Files,
    byte[] Image
  );

  /// <summary>Parses an OS-9 RBF image (root directory only).</summary>
  public static Volume Read(ReadOnlySpan<byte> image) {
    if (image.Length < Os9Layout.SectorSize)
      throw new InvalidDataException("OS-9 RBF: image shorter than one 256-byte sector.");

    var totalSectors = ReadU24Be(image, Os9Layout.Pd_DD_TOT);
    var rootLsn = ReadU24Be(image, Os9Layout.Pd_DD_DIR);
    var clusterSize = BinaryPrimitives.ReadUInt16BigEndian(image[Os9Layout.Pd_DD_BIT..]);

    if (totalSectors < 4 || rootLsn < 1 || clusterSize == 0)
      throw new InvalidDataException("OS-9 RBF: identification sector fields are out of range — not a valid RBF image.");
    if (totalSectors * (long)Os9Layout.SectorSize > image.Length)
      throw new InvalidDataException(
        $"OS-9 RBF: image truncated ({image.Length} bytes, header claims {totalSectors * Os9Layout.SectorSize}).");

    var volName = ReadHighBitTerminatedAscii(image[Os9Layout.Pd_DD_NAM..],
                                             Os9Layout.SectorSize - Os9Layout.Pd_DD_NAM);

    var files = new List<FileEntry>();
    if (rootLsn * Os9Layout.SectorSize + Os9Layout.SectorSize <= image.Length) {
      var rootFd = image.Slice(rootLsn * Os9Layout.SectorSize, Os9Layout.SectorSize);
      var attrs = rootFd[Os9Layout.FD_ATT];
      if ((attrs & Os9Layout.FAttr_Directory) == 0)
        throw new InvalidDataException("OS-9 RBF: root descriptor is not flagged as a directory.");

      WalkDirectory(image, rootLsn, prefix: string.Empty, files, new HashSet<int> { rootLsn });
    }

    return new Volume(
      VolumeName: volName,
      TotalSectors: totalSectors,
      RootDirLsn: rootLsn,
      Files: files,
      Image: image.ToArray());
  }

  /// <summary>
  /// Recursively enumerates a directory descriptor's entries. Leaf files surface
  /// with their full slash-separated path; each subdirectory is reported as its
  /// own entry and then descended into. The <paramref name="visited"/> set of FD
  /// LSNs guards against cyclic or self-referential directory links.
  /// </summary>
  private static void WalkDirectory(
    ReadOnlySpan<byte> image, int dirFdLsn, string prefix, List<FileEntry> files, HashSet<int> visited) {

    if (dirFdLsn * Os9Layout.SectorSize + Os9Layout.SectorSize > image.Length) return;
    var dirFd = image.Slice(dirFdLsn * Os9Layout.SectorSize, Os9Layout.SectorSize);
    var dirData = ReadFileBytes(image, dirFd, out _);

    // Collect this level's children first so recursion isn't tangled with the
    // span-bound directory buffer.
    var subdirs = new List<(string Path, int FdLsn)>();

    for (var off = 0; off + Os9Layout.DirEntryBytes <= dirData.Length; off += Os9Layout.DirEntryBytes) {
      if (dirData[off] == 0) continue; // empty slot
      var name = ReadHighBitTerminatedAscii(dirData.AsSpan(off, Os9Layout.DirEntryNameMaxBytes),
                                            Os9Layout.DirEntryNameMaxBytes);
      if (name == "." || name == "..") continue; // self / parent links
      if (string.IsNullOrEmpty(name)) continue;

      var fdLsn = ReadU24Be(dirData.AsSpan(), off + Os9Layout.DirEntryFdLsnOffset);
      if (fdLsn == 0 || fdLsn * Os9Layout.SectorSize + Os9Layout.SectorSize > image.Length) continue;

      var fd = image.Slice(fdLsn * Os9Layout.SectorSize, Os9Layout.SectorSize);
      var entryAttrs = fd[Os9Layout.FD_ATT];
      var isDir = (entryAttrs & Os9Layout.FAttr_Directory) != 0;
      var size = (long)BinaryPrimitives.ReadUInt32BigEndian(fd[Os9Layout.FD_SIZ..]);
      var created = TryDate(fd[Os9Layout.FD_CRE + 0], fd[Os9Layout.FD_CRE + 1], fd[Os9Layout.FD_CRE + 2]);

      var fullPath = prefix.Length == 0 ? name : prefix + "/" + name;

      files.Add(new FileEntry(
        Name: fullPath,
        FdLsn: fdLsn,
        ByteLength: size,
        IsDirectory: isDir,
        Created: created));

      if (isDir && visited.Add(fdLsn))
        subdirs.Add((fullPath, fdLsn));
    }

    foreach (var (path, fdLsn) in subdirs)
      WalkDirectory(image, fdLsn, path, files, visited);
  }

  /// <summary>Returns the file's byte contents, honouring its FD.SIZ length.</summary>
  public static byte[] Extract(Volume v, FileEntry e) {
    if (e.IsDirectory) return [];
    var fd = v.Image.AsSpan(e.FdLsn * Os9Layout.SectorSize, Os9Layout.SectorSize);
    return ReadFileBytes(v.Image, fd, out _);
  }

  /// <summary>
  /// Walks a file descriptor's segment list and concatenates the referenced
  /// sectors. Trims to the FD.SIZ byte count so directories that store a
  /// non-zero terminator pad don't bleed into the result.
  /// </summary>
  internal static byte[] ReadFileBytes(ReadOnlySpan<byte> image, ReadOnlySpan<byte> fd, out long sizeOnDisk) {
    var size = (long)BinaryPrimitives.ReadUInt32BigEndian(fd[Os9Layout.FD_SIZ..]);
    var sb = new MemoryStream();
    var off = Os9Layout.FD_SEG;
    while (off + Os9Layout.SegmentBytes <= fd.Length) {
      var startLsn = ReadU24Be(fd, off);
      var sectors = BinaryPrimitives.ReadUInt16BigEndian(fd[(off + 3)..]);
      if (startLsn == 0) break;
      var byteOff = startLsn * Os9Layout.SectorSize;
      var bytes = sectors * Os9Layout.SectorSize;
      if (byteOff + bytes > image.Length) break;
      sb.Write(image.Slice(byteOff, bytes));
      off += Os9Layout.SegmentBytes;
    }
    sizeOnDisk = sb.Length;
    var buf = sb.ToArray();
    if (size <= 0 || size > buf.Length) return buf;
    var trimmed = new byte[size];
    Array.Copy(buf, trimmed, size);
    return trimmed;
  }

  private static int ReadU24Be(ReadOnlySpan<byte> span, int offset)
    => (span[offset] << 16) | (span[offset + 1] << 8) | span[offset + 2];

  private static string ReadHighBitTerminatedAscii(ReadOnlySpan<byte> span, int maxBytes) {
    var sb = new StringBuilder();
    var limit = Math.Min(maxBytes, span.Length);
    for (var i = 0; i < limit; i++) {
      var b = span[i];
      if (b == 0) break;
      var ch = (char)(b & 0x7F);
      sb.Append(ch);
      if ((b & 0x80) != 0) break; // last char is MSB-flagged
    }
    return sb.ToString();
  }

  private static DateTime? TryDate(byte yy, byte mm, byte dd) {
    if (yy == 0 && mm == 0 && dd == 0) return null;
    var year = yy < 70 ? 2000 + yy : 1900 + yy;
    if (mm is < 1 or > 12 || dd is < 1 or > 31) return null;
    try { return new DateTime(year, mm, dd); }
    catch { return null; }
  }
}
