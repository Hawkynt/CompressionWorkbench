#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.ProDos;

/// <summary>
/// In-place ProDOS block mover. Moves block-aligned extents within a
/// ProDOS disk image and patches index block pointers (seedling/sapling/tree),
/// directory entry key-pointers, and the volume bitmap so the file remains
/// reachable at its new location.
/// </summary>
public sealed class ProDosBlockMover : IFilesystemBlockMover {

  private const int BlockSize = 512;
  private const int VolumeDirStartBlock = 2;
  private const int EntriesPerBlock = 13;
  private const int EntrySize = 39;
  private const int BitmapStartBlock = 6;

  private static readonly byte[] TwoImgMagic = "2IMG"u8.ToArray();

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;
    var buffer = ArrayPool<byte>.Shared.Rent(Math.Min((int)Math.Min(length, 64 * 1024), int.MaxValue));
    try {
      var remaining = length; var src = srcOffset; var dst = dstOffset;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buffer.Length);
        image.Position = src; image.ReadExactly(buffer, 0, chunk);
        image.Position = dst; image.Write(buffer, 0, chunk);
        src += chunk; dst += chunk; remaining -= chunk;
      }
      // Crash barrier: data must land on disk before metadata references it.
      image.Flush();
      if (zeroSource) {
        Array.Clear(buffer, 0, buffer.Length);
        remaining = length; src = srcOffset;
        while (remaining > 0) {
          var chunk = (int)Math.Min(remaining, buffer.Length);
          image.Position = src; image.Write(buffer, 0, chunk);
          src += chunk; remaining -= chunk;
        }
        // Crash barrier: data must land on disk before metadata references it.
        image.Flush();
      }
    } finally { ArrayPool<byte>.Shared.Return(buffer); }
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();

    var imageStart = DetectImageStart(data);
    var blockCount = (int)((length + BlockSize - 1) / BlockSize);
    var oldBlocks = new List<int>(blockCount);
    var newBlocks = new List<int>(blockCount);
    for (var i = 0; i < blockCount; i++) {
      oldBlocks.Add(OffsetToBlock(imageStart, oldOffset + (long)i * BlockSize));
      newBlocks.Add(OffsetToBlock(imageStart, newOffset + (long)i * BlockSize));
    }

    var remap = new Dictionary<int, int>(blockCount);
    for (var i = 0; i < blockCount; i++)
      remap[oldBlocks[i]] = newBlocks[i];

    // 1. Walk directory to find the file and patch its key-pointer + index blocks.
    PatchFileAllocation(data, imageStart, fileName, remap);

    // 2. Update volume bitmap: free old blocks, allocate new blocks.
    var volHeaderOff = imageStart + VolumeDirStartBlock * BlockSize;
    var totalBlocks = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(volHeaderOff + 4 + 0x28));
    var bitmapPtr = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(volHeaderOff + 4 + 0x26));
    if (bitmapPtr == 0) bitmapPtr = BitmapStartBlock;

    foreach (var b in oldBlocks) SetBitmapBit(data, imageStart, bitmapPtr, b, free: true);
    foreach (var b in newBlocks) SetBitmapBit(data, imageStart, bitmapPtr, b, free: false);

    image.Position = 0;
    image.Write(data, 0, data.Length);
    // Crash barrier: metadata commit durable before return.
    image.Flush();
  }

  // ── Directory + index block patching ──────────────────────────────

  private static void PatchFileAllocation(byte[] data, int imageStart, string fileName,
      Dictionary<int, int> remap) {
    var block = VolumeDirStartBlock;
    var visited = new HashSet<int>();
    var firstBlock = true;
    while (block != 0 && visited.Add(block)) {
      var blockOff = imageStart + block * BlockSize;
      if (blockOff + BlockSize > data.Length) break;
      var nextBlock = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(blockOff + 2));
      var slotsHere = ProDosReader.SlotsInBlock(firstBlock);
      for (var i = 0; i < slotsHere; i++) {
        if (firstBlock && i == 0) continue; // volume header
        var eo = blockOff + ProDosReader.EntryOffsetInBlock(firstBlock, i);
        var storage = (data[eo] >> 4) & 0x0F;
        var nameLen = data[eo] & 0x0F;
        if (storage == 0 || nameLen == 0) continue;
        var entryName = Encoding.ASCII.GetString(data, eo + 1, nameLen);
        if (!string.Equals(entryName, fileName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fileName, "*", StringComparison.Ordinal)) continue;

        int keyPointer = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(eo + 0x11));

        // Patch key pointer if it was remapped.
        if (remap.TryGetValue(keyPointer, out var newKey)) {
          BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(eo + 0x11), (ushort)newKey);
          keyPointer = newKey;
        }

        // Patch index blocks based on storage type.
        switch (storage) {
          case 1: // Seedling — key IS the data block, no index to patch.
            break;
          case 2: // Sapling — key is index block with lo[256]+hi[256] pointers.
            PatchIndexBlock(data, imageStart, keyPointer, remap);
            break;
          case 3: { // Tree — key is master index, sub-indices point to data.
            var masterOff = imageStart + keyPointer * BlockSize;
            if (masterOff + BlockSize > data.Length) break;
            // Patch master index entries (sub-index block pointers).
            for (var si = 0; si < 256; si++) {
              var sub = data[masterOff + si] | (data[masterOff + 256 + si] << 8);
              if (sub == 0) continue;
              if (remap.TryGetValue(sub, out var newSub)) {
                data[masterOff + si] = (byte)(newSub & 0xFF);
                data[masterOff + 256 + si] = (byte)((newSub >> 8) & 0xFF);
                sub = newSub;
              }
              PatchIndexBlock(data, imageStart, sub, remap);
            }
            break;
          }
        }
      }
      firstBlock = false;
      block = nextBlock;
    }
  }

  private static void PatchIndexBlock(byte[] data, int imageStart, int indexBlock,
      Dictionary<int, int> remap) {
    var off = imageStart + indexBlock * BlockSize;
    if (off + BlockSize > data.Length) return;
    for (var i = 0; i < 256; i++) {
      var ptr = data[off + i] | (data[off + 256 + i] << 8);
      if (ptr == 0) continue;
      if (remap.TryGetValue(ptr, out var newPtr)) {
        data[off + i] = (byte)(newPtr & 0xFF);
        data[off + 256 + i] = (byte)((newPtr >> 8) & 0xFF);
      }
    }
  }

  // ── Bitmap helpers ────────────────────────────────────────────────
  // ProDOS bitmap: bit 7 of byte 0 = block 0. Bit SET = free.

  private static void SetBitmapBit(byte[] data, int imageStart, int bitmapStart, int block, bool free) {
    var bitmapByteOff = imageStart + bitmapStart * BlockSize + block / 8;
    if (bitmapByteOff >= data.Length) return;
    var bitMask = (byte)(0x80 >> (block % 8));
    if (free)
      data[bitmapByteOff] |= bitMask;
    else
      data[bitmapByteOff] &= (byte)~bitMask;
  }

  // ── Helpers ───────────────────────────────────────────────────────

  private static int DetectImageStart(byte[] data) {
    if (data.Length < 64) return 0;
    return data[0] == '2' && data[1] == 'I' && data[2] == 'M' && data[3] == 'G' ? 64 : 0;
  }

  private static int OffsetToBlock(int imageStart, long byteOffset)
    => (int)((byteOffset - imageStart) / BlockSize);
}
