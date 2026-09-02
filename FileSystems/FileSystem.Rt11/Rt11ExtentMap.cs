#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Rt11;

/// <summary>
/// Walks a DEC RT-11 disk image (RX01 reference geometry — 256 256 bytes,
/// 512-byte blocks) and yields the actual on-disk byte layout — the boot
/// block + home block as <see cref="DefragBlockKind.MetadataReserved"/>, every
/// directory segment as <see cref="DefragBlockKind.MetadataReserved"/>, every
/// permanent file as a <see cref="DefragBlockKind.Used"/> contiguous run
/// (RT-11 stores files contiguously by design), and every E_MPTY directory
/// slot's referenced range as <see cref="DefragBlockKind.Free"/>.
/// </summary>
public static class Rt11ExtentMap {

  /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    if (data.Length < (Rt11Layout.HomeBlock + 1) * Rt11Layout.BlockSize) yield break;

    // Boot block + home block.
    yield return new DefragBlockInfo(0, Rt11Layout.BlockSize,
      DefragBlockKind.MetadataReserved, FileName: "RT-11 boot block");
    yield return new DefragBlockInfo(Rt11Layout.BlockSize, Rt11Layout.BlockSize,
      DefragBlockKind.MetadataReserved, FileName: "RT-11 home block");

    // Walk directory segments. Each segment is 2 blocks (1024 bytes); link
    // word at +2 gives the next segment number (0 = end). Within each segment
    // the entries describe contiguous runs starting at the segment header's
    // dataStart word, advancing by sizeBlocks per entry until E_EOS.
    var firstSegBlock = Rt11Layout.FirstDirSegment;
    var segNum = 1;
    var visited = new HashSet<int>();
    while (segNum != 0 && visited.Add(segNum)) {
      var segByteOff = (firstSegBlock + (segNum - 1) * Rt11Layout.DirSegmentBlocks) * Rt11Layout.BlockSize;
      if (segByteOff + Rt11Layout.DirSegmentBytes > data.Length) break;

      // The segment occupies 1024 bytes — emit it as metadata.
      yield return new DefragBlockInfo(segByteOff, Rt11Layout.DirSegmentBytes,
        DefragBlockKind.MetadataReserved, FileName: $"RT-11 directory segment {segNum}");

      // Copy segment into a managed array so we can yield mid-iteration.
      var seg = new byte[Rt11Layout.DirSegmentBytes];
      Array.Copy(data, segByteOff, seg, 0, Rt11Layout.DirSegmentBytes);
      var nextSeg = BinaryPrimitives.ReadUInt16LittleEndian(seg.AsSpan(2));
      var extraBytes = BinaryPrimitives.ReadUInt16LittleEndian(seg.AsSpan(6));
      var dataStart = BinaryPrimitives.ReadUInt16LittleEndian(seg.AsSpan(8));

      var entryStride = Rt11Layout.DirEntryBytes + extraBytes;
      var entryOff = Rt11Layout.DirSegmentHeaderBytes;
      var nextFileBlock = (int)dataStart;

      while (entryOff + entryStride <= seg.Length) {
        var status = BinaryPrimitives.ReadUInt16LittleEndian(seg.AsSpan(entryOff));
        if ((status & Rt11Layout.E_EOS) != 0) break;

        var nameHigh = BinaryPrimitives.ReadUInt16LittleEndian(seg.AsSpan(entryOff + 2));
        var nameLow = BinaryPrimitives.ReadUInt16LittleEndian(seg.AsSpan(entryOff + 4));
        var typeWord = BinaryPrimitives.ReadUInt16LittleEndian(seg.AsSpan(entryOff + 6));
        var sizeBlocks = BinaryPrimitives.ReadUInt16LittleEndian(seg.AsSpan(entryOff + 8));

        var isPermanent = (status & (Rt11Layout.E_PERM | Rt11Layout.E_PRE)) != 0;
        var isEmpty = (status & Rt11Layout.E_MPTY) != 0;

        if (sizeBlocks > 0) {
          var fileOff = (long)nextFileBlock * Rt11Layout.BlockSize;
          var fileLen = (long)sizeBlocks * Rt11Layout.BlockSize;
          if (fileOff < data.Length) {
            if (fileOff + fileLen > data.Length) fileLen = data.Length - fileOff;
            if (isEmpty) {
              // E_MPTY slot — these blocks are formally free in RT-11.
              if (fileLen > 0)
                yield return new DefragBlockInfo(fileOff, fileLen, DefragBlockKind.Free);
            } else if (isPermanent) {
              var stem = Rad50.DecodeName6(nameHigh, nameLow);
              var ext = Rad50.DecodeType3(typeWord);
              var fullName = string.IsNullOrEmpty(ext) ? stem : $"{stem}.{ext}";
              if (fileLen > 0)
                yield return new DefragBlockInfo(fileOff, fileLen, DefragBlockKind.Used, fullName);
            }
          }
        }

        nextFileBlock += sizeBlocks;
        entryOff += entryStride;
      }

      segNum = nextSeg;
      if (visited.Count > 31) break; // RT-11 caps segments at 31; safety
    }
  }
}
