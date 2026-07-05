using System.Buffers.Binary;

namespace Compression.Core.DiskImage;

/// <summary>
/// Parses the 4.4BSD <c>disklabel</c> that FreeBSD/NetBSD/OpenBSD write into a
/// BSD slice (an MBR partition of type <c>0xA5</c>/<c>0xA6</c>/<c>0xA9</c>, or a
/// GPT FreeBSD partition). The label is a native little-endian structure — the
/// on-disk byte order on the overwhelmingly common x86/amd64 media.
///
/// <para>The label is located at a fixed offset inside the slice: sector 1
/// (byte offset 512) on i386/amd64, or byte offset 64 within sector 0 on some
/// other ports. It opens with <c>d_magic</c> (<c>0x82564557</c>), repeats the
/// magic at <c>d_magic2</c>, records <c>d_npartitions</c>, and carries an array
/// of 16-byte partition records (<c>p_size</c>, <c>p_offset</c> in sectors,
/// <c>p_fsize</c>, <c>p_fstype</c>, …).</para>
///
/// <para>Partition offsets follow the convention the Linux kernel BSD parser
/// relies on: when the whole-slice <c>'c'</c> partition (slot 2) has
/// <c>p_offset == 0</c> the offsets are slice-relative and the slice's own start
/// sector is added; otherwise they are absolute disk sectors. The <c>'c'</c>
/// whole-slice slot and zero-length slots are not enumerated.</para>
///
/// References:
/// <list type="bullet">
///   <item><description>4.4BSD, <c>sys/sys/disklabel.h</c> — <c>struct disklabel</c> / <c>struct partition</c> layout and <c>FS_*</c> filesystem-type constants.</description></item>
///   <item><description>Linux kernel, <c>block/partitions/bsd.c</c> — the slice-relative-vs-absolute offset heuristic keyed on the <c>'c'</c> partition.</description></item>
/// </list>
/// </summary>
public static class BsdDisklabelParser {

  /// <summary>Disklabel magic: <c>DISKMAGIC</c> (<c>0x82564557</c>).</summary>
  private const uint DiskMagic = 0x82564557;

  /// <summary>Candidate label offsets inside a slice, in probe order.</summary>
  private static readonly int[] LabelOffsets = [512, 64];

  /// <summary>Offset of <c>d_magic2</c> within the label.</summary>
  private const int Magic2Offset = 132;

  /// <summary>Offset of <c>d_npartitions</c> within the label.</summary>
  private const int NPartitionsOffset = 138;

  /// <summary>Offset of the partition array within the label.</summary>
  private const int PartitionsOffset = 148;

  /// <summary>Size of one <c>struct partition</c> record.</summary>
  private const int PartitionRecordSize = 16;

  /// <summary>Conventional whole-slice partition slot ('c').</summary>
  private const int WholeSliceSlot = 2;

  /// <summary>Hard cap on partitions to guard against corrupt labels (MAXPARTITIONS is 8/16/22 across BSDs).</summary>
  private const int MaxPartitions = 22;

  /// <summary>
  /// Reports whether a BSD disklabel magic is present at either candidate offset
  /// within the given slice stream.
  /// </summary>
  /// <param name="slice">A seekable stream over the BSD slice (position 0 = slice start).</param>
  /// <returns><c>true</c> if a disklabel is present.</returns>
  public static bool IsDisklabel(Stream slice) => FindLabelOffset(slice) >= 0;

  /// <summary>
  /// Parses the BSD disklabel contained in <paramref name="slice"/> and returns
  /// its filesystem partitions.
  /// </summary>
  /// <param name="slice">A seekable, readable stream over the BSD slice (position 0 = slice start).</param>
  /// <param name="sliceStartByteOffset">
  /// Byte offset of the slice on the parent disk. Used to translate slice-relative
  /// partition offsets into absolute parent-disk offsets and to bound-check the
  /// results. Pass 0 when the label describes a whole disk.
  /// </param>
  /// <returns>
  /// Partition entries whose <see cref="PartitionEntry.StartOffset"/> is expressed
  /// on the same coordinate system as <paramref name="sliceStartByteOffset"/>
  /// (i.e. absolute parent-disk byte offsets). Excludes the whole-slice
  /// <c>'c'</c> partition and zero-length slots.
  /// </returns>
  public static List<PartitionEntry> Parse(Stream slice, long sliceStartByteOffset = 0) {
    var labelOffset = FindLabelOffset(slice);
    if (labelOffset < 0)
      throw new InvalidDataException("No BSD disklabel magic (0x82564557) found in the slice.");

    var label = new byte[PartitionsOffset + MaxPartitions * PartitionRecordSize];
    slice.Position = labelOffset;
    var read = 0;
    while (read < label.Length) {
      var n = slice.Read(label, read, label.Length - read);
      if (n <= 0) break;
      read += n;
    }

    var span = label.AsSpan(0, read);

    var secSize = ReadU32(span, 40);
    if (secSize is < 512 or > 8192 || (secSize & (secSize - 1)) != 0)
      secSize = 512; // sanitise implausible sector sizes
    var sliceStartSector = secSize == 0 ? 0 : sliceStartByteOffset / secSize;

    var npartitions = span.Length >= NPartitionsOffset + 2
      ? BinaryPrimitives.ReadUInt16LittleEndian(span[NPartitionsOffset..])
      : 0;
    if (npartitions is 0 or > MaxPartitions)
      npartitions = MaxPartitions;

    // Slice-relative offsets are signalled by a zero-offset whole-slice ('c') entry.
    var relative = false;
    if (npartitions > WholeSliceSlot) {
      var cOffset = ReadPartitionField(span, WholeSliceSlot, 4);
      relative = cOffset == 0;
    }

    var result = new List<PartitionEntry>();
    var index = 0;
    for (var i = 0; i < npartitions; ++i) {
      var recStart = PartitionsOffset + i * PartitionRecordSize;
      if (recStart + PartitionRecordSize > span.Length) break;

      var pSize = ReadU32(span, recStart + 0);
      var pOffset = ReadU32(span, recStart + 4);
      var pFsType = span[recStart + 12];

      if (i == WholeSliceSlot) continue; // whole-slice 'c' — not a real filesystem
      if (pSize == 0) continue;          // unused slot

      var startSector = relative ? pOffset + sliceStartSector : pOffset;
      result.Add(new PartitionEntry {
        Index = index++,
        StartOffset = startSector * secSize,
        Size = (long)pSize * secSize,
        TypeName = FsTypeName(pFsType),
        TypeCode = $"0x{pFsType:X2}",
        Name = ((char)('a' + i)).ToString(),
        Source = "BSD"
      });
    }

    return result;
  }

  private static int FindLabelOffset(Stream slice) {
    if (!slice.CanSeek) return -1;
    Span<byte> magic = stackalloc byte[4];
    foreach (var offset in LabelOffsets) {
      if (offset + 4 > slice.Length) continue;
      slice.Position = offset;
      if (!ReadExact(slice, magic)) continue;
      if (BinaryPrimitives.ReadUInt32LittleEndian(magic) == DiskMagic)
        return offset;
    }
    return -1;
  }

  private static uint ReadU32(ReadOnlySpan<byte> span, int offset)
    => offset + 4 <= span.Length ? BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]) : 0;

  private static uint ReadPartitionField(ReadOnlySpan<byte> span, int partitionIndex, int fieldOffset)
    => ReadU32(span, PartitionsOffset + partitionIndex * PartitionRecordSize + fieldOffset);

  /// <summary>
  /// Maps a BSD <c>p_fstype</c> code to a human-readable filesystem name
  /// (<c>FS_*</c> constants from <c>sys/disklabel.h</c>).
  /// </summary>
  private static string FsTypeName(byte fsType) => fsType switch {
    0 => "unused",
    1 => "swap",
    2 => "Version 6",
    3 => "Version 7",
    4 => "System V",
    5 => "4.1BSD",
    6 => "Eighth Edition",
    7 => "4.2BSD",
    8 => "MSDOS",
    9 => "4.4LFS",
    10 => "unknown",
    11 => "HPFS",
    12 => "ISO9660",
    13 => "boot",
    14 => "ADOS",
    15 => "HFS",
    16 => "ADFS",
    17 => "ext2fs",
    18 => "ccd",
    19 => "RAID",
    20 => "NTFS",
    24 => "UDF",
    27 => "FFS",
    _ => $"BSD type 0x{fsType:X2}"
  };

  private static bool ReadExact(Stream stream, Span<byte> buffer) {
    var read = 0;
    while (read < buffer.Length) {
      var n = stream.Read(buffer[read..]);
      if (n <= 0) return false;
      read += n;
    }
    return true;
  }
}
