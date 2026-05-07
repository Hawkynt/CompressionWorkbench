#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Rt11;

/// <summary>
/// Random-access in-place modifier for DEC RT-11 disk images. Files are stored
/// contiguously in 512-byte blocks; free space is tracked by E_MPTY ("empty
/// area") entries within the directory segment chain itself (no separate
/// bitmap). Add operations split an empty-area entry; remove operations turn
/// the file's slot back into an empty-area entry and merge with neighbours.
/// </summary>
public static class Rt11Modifier {

  /// <summary>
  /// Adds a file to an existing RT-11 image. Caller is responsible for ensuring
  /// the name does not already exist (use <see cref="RemoveFile"/> first for
  /// replace-by-name semantics). The file is placed in the lowest empty-area
  /// run large enough to hold its contiguous data.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var (stem, ext) = SplitName(name);
    if (!Rad50.IsValid(stem) || !Rad50.IsValid(ext))
      throw new InvalidOperationException(
        $"RT-11: filename \"{name}\" contains characters outside the RAD-50 alphabet (A-Z, 0-9, $, .).");

    var sizeBlocks = (data.Length + Rt11Layout.BlockSize - 1) / Rt11Layout.BlockSize;
    if (sizeBlocks > ushort.MaxValue)
      throw new ArgumentException(
        $"RT-11: file size ({data.Length} bytes) exceeds 16-bit block count.", nameof(data));

    // Walk the segment chain, finding the first empty-area entry big enough.
    var seg = ReadFirstSegment(image);
    var found = false;
    while (true) {
      var entries = ParseSegment(seg.Bytes, out var header);

      // Find an empty-area entry with enough blocks.
      var emptyIdx = -1;
      var blockCursor = (int)header.StartDataBlock;
      var entryBlocks = new int[entries.Count]; // computed running positions

      for (var i = 0; i < entries.Count; i++) {
        entryBlocks[i] = blockCursor;
        if ((entries[i].Status & Rt11Layout.E_MPTY) != 0 && entries[i].SizeBlocks >= sizeBlocks && emptyIdx < 0)
          emptyIdx = i;
        blockCursor += entries[i].SizeBlocks;
      }

      if (emptyIdx >= 0) {
        // Split the empty area: write the new file entry, then a smaller empty entry
        // for any leftover blocks. If exact fit, just replace the empty entry.
        var hostStart = entryBlocks[emptyIdx];
        var hostBlocks = entries[emptyIdx].SizeBlocks;
        var leftoverBlocks = hostBlocks - sizeBlocks;

        var (nh, nl) = Rad50.EncodeName6(stem);
        var tw = Rad50.EncodeType3(ext);
        var dateWord = EncodeDate(DateTime.Today);

        var newEntries = new List<DirEntry>(entries);
        var newFileEntry = new DirEntry(
          Status: Rt11Layout.E_PERM,
          NameHigh: nh,
          NameLow: nl,
          TypeWord: tw,
          SizeBlocks: (ushort)sizeBlocks,
          ChannelByte: 0,
          JobByte: 0,
          DateWord: dateWord);

        if (leftoverBlocks > 0) {
          var leftoverEntry = new DirEntry(
            Status: Rt11Layout.E_MPTY,
            NameHigh: 0, NameLow: 0, TypeWord: 0,
            SizeBlocks: (ushort)leftoverBlocks,
            ChannelByte: 0, JobByte: 0, DateWord: 0);
          newEntries[emptyIdx] = newFileEntry;
          newEntries.Insert(emptyIdx + 1, leftoverEntry);
        } else {
          // Exact fit: just replace the empty area entry.
          newEntries[emptyIdx] = newFileEntry;
        }

        // Check segment capacity (slot count).
        var maxEntries = MaxEntriesForExtra(header.ExtraBytes);
        if (newEntries.Count + 1 > maxEntries) // +1 for E_EOS
          throw new InvalidOperationException(
            $"RT-11: directory segment full (cannot fit {newEntries.Count} entries; max {maxEntries - 1}).");

        // Write the file payload.
        if (data.Length > 0)
          WriteData(image, hostStart, data, sizeBlocks);

        // Write back the segment.
        WriteSegment(image, seg.SegmentNumber, header, newEntries);
        found = true;
        break;
      }

      if (header.NextSegment == 0) break;
      seg = ReadSegment(image, header.NextSegment);
    }

    if (!found)
      throw new InvalidOperationException(
        $"RT-11: no contiguous empty area large enough for {sizeBlocks} blocks.");
  }

  /// <summary>
  /// Removes a named file from the image. Returns true if found and removed.
  /// When <paramref name="wipeData"/> is true, the data blocks are zeroed.
  /// Adjacent empty-area entries are merged with the freed slot.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var (stem, ext) = SplitName(name);
    if (!Rad50.IsValid(stem) || !Rad50.IsValid(ext)) return false;

    var (targetHigh, targetLow) = Rad50.EncodeName6(stem);
    var targetType = Rad50.EncodeType3(ext);

    var seg = ReadFirstSegment(image);
    while (true) {
      var entries = ParseSegment(seg.Bytes, out var header);

      // Compute running block positions and locate matching permanent file.
      var blockCursor = (int)header.StartDataBlock;
      var entryBlocks = new int[entries.Count];
      var matchIdx = -1;
      for (var i = 0; i < entries.Count; i++) {
        entryBlocks[i] = blockCursor;
        var e = entries[i];
        var isPermanent = (e.Status & (Rt11Layout.E_PERM | Rt11Layout.E_PRE)) != 0;
        var isEmpty = (e.Status & Rt11Layout.E_MPTY) != 0;
        if (isPermanent && !isEmpty &&
            e.NameHigh == targetHigh && e.NameLow == targetLow && e.TypeWord == targetType) {
          matchIdx = i;
        }
        blockCursor += e.SizeBlocks;
      }

      if (matchIdx >= 0) {
        var matched = entries[matchIdx];

        if (wipeData && matched.SizeBlocks > 0) {
          var startByte = (long)entryBlocks[matchIdx] * Rt11Layout.BlockSize;
          var lenBytes = matched.SizeBlocks * Rt11Layout.BlockSize;
          if (startByte + lenBytes > GetImageLength(image))
            lenBytes = (int)Math.Max(0, GetImageLength(image) - startByte);
          if (lenBytes > 0)
            ZeroRun(image, startByte, lenBytes);
        }

        // Convert the entry to an empty-area entry, then merge with
        // adjacent empty-area entries (RT-11 keeps free space coalesced).
        var newEntries = new List<DirEntry>(entries);
        newEntries[matchIdx] = new DirEntry(
          Status: Rt11Layout.E_MPTY,
          NameHigh: 0, NameLow: 0, TypeWord: 0,
          SizeBlocks: matched.SizeBlocks,
          ChannelByte: 0, JobByte: 0, DateWord: 0);

        // Merge with following empty entry.
        if (matchIdx + 1 < newEntries.Count && (newEntries[matchIdx + 1].Status & Rt11Layout.E_MPTY) != 0) {
          var combined = newEntries[matchIdx].SizeBlocks + newEntries[matchIdx + 1].SizeBlocks;
          if (combined <= ushort.MaxValue) {
            newEntries[matchIdx] = newEntries[matchIdx] with { SizeBlocks = (ushort)combined };
            newEntries.RemoveAt(matchIdx + 1);
          }
        }
        // Merge with preceding empty entry.
        if (matchIdx > 0 && (newEntries[matchIdx - 1].Status & Rt11Layout.E_MPTY) != 0) {
          var combined = newEntries[matchIdx - 1].SizeBlocks + newEntries[matchIdx].SizeBlocks;
          if (combined <= ushort.MaxValue) {
            newEntries[matchIdx - 1] = newEntries[matchIdx - 1] with { SizeBlocks = (ushort)combined };
            newEntries.RemoveAt(matchIdx);
          }
        }

        WriteSegment(image, seg.SegmentNumber, header, newEntries);
        return true;
      }

      if (header.NextSegment == 0) return false;
      seg = ReadSegment(image, header.NextSegment);
    }
  }

  // ── Segment I/O ──────────────────────────────────────────────────────────

  private readonly record struct DirEntry(
    ushort Status,
    ushort NameHigh,
    ushort NameLow,
    ushort TypeWord,
    ushort SizeBlocks,
    byte ChannelByte,
    byte JobByte,
    ushort DateWord);

  private readonly record struct SegHeader(
    ushort SegCount,
    ushort NextSegment,
    ushort HighestSeg,
    ushort ExtraBytes,
    ushort StartDataBlock);

  private sealed record SegmentInfo(int SegmentNumber, byte[] Bytes);

  private static SegmentInfo ReadFirstSegment(Stream image) => ReadSegment(image, 1);

  private static SegmentInfo ReadSegment(Stream image, int segmentNumber) {
    var byteOff = (Rt11Layout.FirstDirSegment + (segmentNumber - 1) * Rt11Layout.DirSegmentBlocks) * Rt11Layout.BlockSize;
    var buf = new byte[Rt11Layout.DirSegmentBytes];
    image.Position = byteOff;
    var read = 0;
    while (read < buf.Length) {
      var n = image.Read(buf, read, buf.Length - read);
      if (n <= 0) break;
      read += n;
    }
    return new SegmentInfo(segmentNumber, buf);
  }

  private static List<DirEntry> ParseSegment(byte[] segBytes, out SegHeader header) {
    var seg = segBytes.AsSpan();
    header = new SegHeader(
      SegCount: BinaryPrimitives.ReadUInt16LittleEndian(seg),
      NextSegment: BinaryPrimitives.ReadUInt16LittleEndian(seg[2..]),
      HighestSeg: BinaryPrimitives.ReadUInt16LittleEndian(seg[4..]),
      ExtraBytes: BinaryPrimitives.ReadUInt16LittleEndian(seg[6..]),
      StartDataBlock: BinaryPrimitives.ReadUInt16LittleEndian(seg[8..]));

    var entries = new List<DirEntry>();
    var stride = Rt11Layout.DirEntryBytes + header.ExtraBytes;
    var off = Rt11Layout.DirSegmentHeaderBytes;
    while (off + stride <= seg.Length) {
      var e = seg.Slice(off, Rt11Layout.DirEntryBytes);
      var status = BinaryPrimitives.ReadUInt16LittleEndian(e);
      if ((status & Rt11Layout.E_EOS) != 0) break;
      entries.Add(new DirEntry(
        Status: status,
        NameHigh: BinaryPrimitives.ReadUInt16LittleEndian(e[2..]),
        NameLow: BinaryPrimitives.ReadUInt16LittleEndian(e[4..]),
        TypeWord: BinaryPrimitives.ReadUInt16LittleEndian(e[6..]),
        SizeBlocks: BinaryPrimitives.ReadUInt16LittleEndian(e[8..]),
        ChannelByte: e[10],
        JobByte: e[11],
        DateWord: BinaryPrimitives.ReadUInt16LittleEndian(e[12..])));
      off += stride;
    }
    return entries;
  }

  private static void WriteSegment(Stream image, int segmentNumber, SegHeader header, List<DirEntry> entries) {
    var stride = Rt11Layout.DirEntryBytes + header.ExtraBytes;
    var maxEntries = MaxEntriesForExtra(header.ExtraBytes);
    if (entries.Count + 1 > maxEntries)
      throw new InvalidOperationException(
        $"RT-11: directory segment overflow (entries={entries.Count}, max usable={maxEntries - 1}).");

    var buf = new byte[Rt11Layout.DirSegmentBytes];
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), header.SegCount);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), header.NextSegment);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), header.HighestSeg);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6), header.ExtraBytes);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), header.StartDataBlock);

    var off = Rt11Layout.DirSegmentHeaderBytes;
    foreach (var e in entries) {
      BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 0), e.Status);
      BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 2), e.NameHigh);
      BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 4), e.NameLow);
      BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 6), e.TypeWord);
      BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 8), e.SizeBlocks);
      buf[off + 10] = e.ChannelByte;
      buf[off + 11] = e.JobByte;
      BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 12), e.DateWord);
      // Extra bytes (if any) remain zero.
      off += stride;
    }

    // EOS terminator.
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 0), Rt11Layout.E_EOS);

    var byteOff = (Rt11Layout.FirstDirSegment + (segmentNumber - 1) * Rt11Layout.DirSegmentBlocks) * Rt11Layout.BlockSize;
    image.Position = byteOff;
    image.Write(buf, 0, buf.Length);
  }

  private static int MaxEntriesForExtra(ushort extraBytes) {
    var stride = Rt11Layout.DirEntryBytes + extraBytes;
    return (Rt11Layout.DirSegmentBytes - Rt11Layout.DirSegmentHeaderBytes) / stride;
  }

  // ── Data I/O ─────────────────────────────────────────────────────────────

  private static void WriteData(Stream image, int startBlock, byte[] data, int sizeBlocks) {
    var startByte = (long)startBlock * Rt11Layout.BlockSize;
    image.Position = startByte;
    image.Write(data, 0, data.Length);
    // Zero-pad the tail of the last block so we don't leak previous bytes.
    var totalBytes = sizeBlocks * Rt11Layout.BlockSize;
    var tail = totalBytes - data.Length;
    if (tail > 0) {
      var pad = new byte[tail];
      image.Write(pad, 0, pad.Length);
    }
  }

  private static void ZeroRun(Stream image, long startByte, long length) {
    image.Position = startByte;
    var zero = new byte[Rt11Layout.BlockSize];
    var remaining = length;
    while (remaining > 0) {
      var chunk = (int)Math.Min(remaining, zero.Length);
      image.Write(zero, 0, chunk);
      remaining -= chunk;
    }
  }

  private static long GetImageLength(Stream image) => image.Length;

  // ── Helpers ──────────────────────────────────────────────────────────────

  internal static (string Stem, string Ext) SplitName(string fileName) {
    if (string.IsNullOrEmpty(fileName)) return ("", "");
    var dot = fileName.LastIndexOf('.');
    var stem = dot < 0 ? fileName : fileName[..dot];
    var ext = dot < 0 ? "" : fileName[(dot + 1)..];
    if (stem.Length > 6) stem = stem[..6];
    if (ext.Length > 3) ext = ext[..3];
    return (stem.ToUpperInvariant(), ext.ToUpperInvariant());
  }

  private static ushort EncodeDate(DateTime d) {
    var year = d.Year;
    if (year < 1972) year = 1972;
    if (year > 2027) year = 2027;
    var age = (year - 1972) / 32;
    var yearLow = (year - 1972) % 32;
    return (ushort)(((age & 0x3) << 14) | ((yearLow & 0x1F) << 9) | ((d.Day & 0x1F) << 5) | (d.Month & 0x1F));
  }
}
