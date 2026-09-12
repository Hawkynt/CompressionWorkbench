#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Rt11;

/// <summary>
/// In-place RT-11 block mover. Moves block-aligned extents within an RT-11
/// image and patches the directory segment entry's start-block position so the
/// file remains reachable at its new location.
///
/// <para>RT-11 files are stored contiguously in 512-byte blocks. The directory
/// segment chain contains entries whose start-block is implicit: it's computed
/// as a running sum of prior entry sizes from the segment's StartDataBlock.
/// Moving a file requires rewriting the directory entries so the E_MPTY gap
/// before the file absorbs the old location and a new E_MPTY gap replaces the
/// space freed at the old position.</para>
///
/// <para>Because RT-11's start-block is implicit (not stored in the entry),
/// we accomplish the "metadata patch" by rebuilding the directory segment
/// with adjusted E_MPTY entries around the moved file.</para>
/// </summary>
public sealed class Rt11BlockMover : IFilesystemBlockMover {

  /// <summary>Byte offset where data begins (past boot + home + dir).</summary>
  public long DataOrigin => (long)(Rt11Layout.FirstDirSegment + Rt11Layout.DirSegmentBlocks) * Rt11Layout.BlockSize;

  /// <summary>Allocation unit size (512-byte block).</summary>
  public int UnitSize => Rt11Layout.BlockSize;

  /// <summary>Converts a byte offset to a block number.</summary>
  public int OffsetToBlock(long offset) => (int)(offset / Rt11Layout.BlockSize);

  /// <summary>Converts a block number to a byte offset.</summary>
  public long BlockToOffset(int block) => (long)block * Rt11Layout.BlockSize;

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var oldBlock = OffsetToBlock(oldOffset);
    var newBlock = OffsetToBlock(newOffset);
    var sizeBlocks = (int)((length + Rt11Layout.BlockSize - 1) / Rt11Layout.BlockSize);

    // Parse target filename into RAD-50.
    var (stem, ext) = Rt11Modifier.SplitName(fileName);
    if (!Rad50.IsValid(stem) || !Rad50.IsValid(ext)) return;
    var (targetHigh, targetLow) = Rad50.EncodeName6(stem);
    var targetType = Rad50.EncodeType3(ext);

    // Walk the directory segment chain.
    var segNum = 1;
    while (segNum > 0) {
      var segByteOff = (Rt11Layout.FirstDirSegment + (segNum - 1) * Rt11Layout.DirSegmentBlocks) * Rt11Layout.BlockSize;
      var segBuf = new byte[Rt11Layout.DirSegmentBytes];
      image.Position = segByteOff;
      image.ReadExactly(segBuf);

      var seg = segBuf.AsSpan();
      var segCount = BinaryPrimitives.ReadUInt16LittleEndian(seg);
      var nextSeg = BinaryPrimitives.ReadUInt16LittleEndian(seg[2..]);
      var highestSeg = BinaryPrimitives.ReadUInt16LittleEndian(seg[4..]);
      var extraBytes = BinaryPrimitives.ReadUInt16LittleEndian(seg[6..]);
      var startDataBlock = BinaryPrimitives.ReadUInt16LittleEndian(seg[8..]);

      var stride = Rt11Layout.DirEntryBytes + extraBytes;
      var off = Rt11Layout.DirSegmentHeaderBytes;
      var blockCursor = (int)startDataBlock;

      // Parse all entries.
      var entries = new List<(ushort Status, ushort NH, ushort NL, ushort TW, ushort Size, byte Ch, byte Job, ushort Date, int StartBlock)>();
      while (off + stride <= seg.Length) {
        var e = seg.Slice(off, Rt11Layout.DirEntryBytes);
        var status = BinaryPrimitives.ReadUInt16LittleEndian(e);
        if ((status & Rt11Layout.E_EOS) != 0) break;
        entries.Add((
          status,
          BinaryPrimitives.ReadUInt16LittleEndian(e[2..]),
          BinaryPrimitives.ReadUInt16LittleEndian(e[4..]),
          BinaryPrimitives.ReadUInt16LittleEndian(e[6..]),
          BinaryPrimitives.ReadUInt16LittleEndian(e[8..]),
          e[10],
          e[11],
          BinaryPrimitives.ReadUInt16LittleEndian(e[12..]),
          blockCursor));
        blockCursor += BinaryPrimitives.ReadUInt16LittleEndian(e[8..]);
        off += stride;
      }

      // Find the matching file entry.
      var matchIdx = -1;
      for (var i = 0; i < entries.Count; i++) {
        var ent = entries[i];
        var isPerm = (ent.Status & (Rt11Layout.E_PERM | Rt11Layout.E_PRE)) != 0;
        var isEmpty = (ent.Status & Rt11Layout.E_MPTY) != 0;
        if (isPerm && !isEmpty &&
            ent.NH == targetHigh && ent.NL == targetLow && ent.TW == targetType &&
            ent.StartBlock == oldBlock) {
          matchIdx = i;
          break;
        }
      }

      if (matchIdx >= 0) {
        // The file was found. We need to rebuild the directory to reflect the move.
        // Since MoveExtent already moved the raw bytes, we need to:
        // 1. Turn the old location into E_MPTY.
        // 2. Place the file entry at the new location (which requires inserting/adjusting E_MPTY gaps).
        //
        // Strategy: remove the file entry from its current position (leaving E_MPTY),
        // then insert it at the right position for newBlock (splitting an E_MPTY entry).

        var matched = entries[matchIdx];
        var newEntries = new List<(ushort Status, ushort NH, ushort NL, ushort TW, ushort Size, byte Ch, byte Job, ushort Date)>();

        // Compute running block positions first.
        var cursor = (int)startDataBlock;
        for (var i = 0; i < entries.Count; i++) {
          if (i == matchIdx) {
            // Replace file with E_MPTY at old location.
            newEntries.Add((Rt11Layout.E_MPTY, 0, 0, 0, matched.Size, 0, 0, 0));
          } else {
            var e = entries[i];
            newEntries.Add((e.Status, e.NH, e.NL, e.TW, e.Size, e.Ch, e.Job, e.Date));
          }
        }

        // Merge adjacent E_MPTY entries.
        MergeEmpty(newEntries);

        // Now insert the file at the position corresponding to newBlock.
        // Find the E_MPTY entry that covers newBlock.
        cursor = (int)startDataBlock;
        var inserted = false;
        for (var i = 0; i < newEntries.Count; i++) {
          var e = newEntries[i];
          if ((e.Status & Rt11Layout.E_MPTY) != 0 && cursor <= newBlock && cursor + e.Size > newBlock) {
            var gapBefore = newBlock - cursor;
            var gapAfter = (cursor + e.Size) - (newBlock + sizeBlocks);

            var replacement = new List<(ushort Status, ushort NH, ushort NL, ushort TW, ushort Size, byte Ch, byte Job, ushort Date)>();
            if (gapBefore > 0)
              replacement.Add((Rt11Layout.E_MPTY, 0, 0, 0, (ushort)gapBefore, 0, 0, 0));
            replacement.Add((matched.Status, matched.NH, matched.NL, matched.TW, matched.Size, matched.Ch, matched.Job, matched.Date));
            if (gapAfter > 0)
              replacement.Add((Rt11Layout.E_MPTY, 0, 0, 0, (ushort)gapAfter, 0, 0, 0));

            newEntries.RemoveAt(i);
            newEntries.InsertRange(i, replacement);
            inserted = true;
            break;
          }
          cursor += e.Size;
        }

        if (!inserted) return; // safety: can't place file

        // Merge again to clean up.
        MergeEmpty(newEntries);

        // Check capacity.
        var maxEntries = (Rt11Layout.DirSegmentBytes - Rt11Layout.DirSegmentHeaderBytes) / stride;
        if (newEntries.Count + 1 > maxEntries) return; // can't fit

        // Rewrite the segment.
        var newSeg = new byte[Rt11Layout.DirSegmentBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(newSeg.AsSpan(0), segCount);
        BinaryPrimitives.WriteUInt16LittleEndian(newSeg.AsSpan(2), nextSeg);
        BinaryPrimitives.WriteUInt16LittleEndian(newSeg.AsSpan(4), highestSeg);
        BinaryPrimitives.WriteUInt16LittleEndian(newSeg.AsSpan(6), extraBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(newSeg.AsSpan(8), startDataBlock);

        var wOff = Rt11Layout.DirSegmentHeaderBytes;
        foreach (var e in newEntries) {
          BinaryPrimitives.WriteUInt16LittleEndian(newSeg.AsSpan(wOff + 0), e.Status);
          BinaryPrimitives.WriteUInt16LittleEndian(newSeg.AsSpan(wOff + 2), e.NH);
          BinaryPrimitives.WriteUInt16LittleEndian(newSeg.AsSpan(wOff + 4), e.NL);
          BinaryPrimitives.WriteUInt16LittleEndian(newSeg.AsSpan(wOff + 6), e.TW);
          BinaryPrimitives.WriteUInt16LittleEndian(newSeg.AsSpan(wOff + 8), e.Size);
          newSeg[wOff + 10] = e.Ch;
          newSeg[wOff + 11] = e.Job;
          BinaryPrimitives.WriteUInt16LittleEndian(newSeg.AsSpan(wOff + 12), e.Date);
          wOff += stride;
        }
        // EOS terminator.
        BinaryPrimitives.WriteUInt16LittleEndian(newSeg.AsSpan(wOff + 0), Rt11Layout.E_EOS);

        image.Position = segByteOff;
        image.Write(newSeg);
        // Crash barrier: metadata commit durable before return.
        image.Flush();
        return;
      }

      segNum = nextSeg;
    }
  }

  private static void MergeEmpty(List<(ushort Status, ushort NH, ushort NL, ushort TW, ushort Size, byte Ch, byte Job, ushort Date)> entries) {
    for (var i = entries.Count - 2; i >= 0; i--) {
      if ((entries[i].Status & Rt11Layout.E_MPTY) != 0 &&
          (entries[i + 1].Status & Rt11Layout.E_MPTY) != 0) {
        var combined = entries[i].Size + entries[i + 1].Size;
        if (combined <= ushort.MaxValue) {
          entries[i] = entries[i] with { Size = (ushort)combined };
          entries.RemoveAt(i + 1);
        }
      }
    }
  }
}
