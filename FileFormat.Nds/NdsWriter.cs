#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Nds;

/// <summary>
/// Writes a minimal Nintendo DS ROM image with NitroFS containing the input
/// files. No ARM9/ARM7 code is emitted — the ROM is structurally valid for
/// file extraction but won't boot on hardware/emulators. Roundtrips through
/// <see cref="NdsReader"/>.
/// </summary>
public sealed class NdsWriter {
  private const int HeaderSize = 0x1000; // 4KB
  private const int FatEntrySize = 8;
  private const int Alignment = 512;

  private readonly List<(string name, byte[] data)> _files = [];

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (_files.Count >= 255)
      throw new InvalidOperationException("NdsWriter supports at most 255 files (single-directory limit).");
    var leaf = Path.GetFileName(name);
    if (leaf.Length > 127) leaf = leaf[..127];
    _files.Add((leaf, data));
  }

  public void WriteTo(Stream output) {
    var n = _files.Count;

    // FAT: n * 8 bytes
    var fatSize = n * FatEntrySize;

    // FNT: root directory entry (8 bytes) + subtable (sum of per-entry size + terminator byte)
    // Each subtable entry: 1 byte (type+namelen) + name bytes (no subdir suffix for files)
    var subtableSize = 1; // terminator 0x00
    foreach (var (name, _) in _files) {
      var nameLen = Encoding.ASCII.GetByteCount(name);
      subtableSize += 1 + nameLen;
    }
    var fntSize = 8 + subtableSize;

    // Layout: header, FAT, FNT, file data (sector-aligned per file).
    var fatOff = (long)HeaderSize;
    var fntOff = AlignUp(fatOff + fatSize);
    var dataOff = AlignUp(fntOff + fntSize);

    var fileOffsets = new long[n];
    var pos = dataOff;
    for (var i = 0; i < n; i++) {
      fileOffsets[i] = pos;
      pos = AlignUp(pos + _files[i].data.Length);
    }
    var totalSize = pos;
    var image = new byte[totalSize];

    // ── Header ──
    Encoding.ASCII.GetBytes("WORM_IMAGE").CopyTo(image, 0);
    Encoding.ASCII.GetBytes("WORM").CopyTo(image, 0x0C);
    Encoding.ASCII.GetBytes("WM").CopyTo(image, 0x10);
    image[0x12] = 0; // unit code
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x20), (uint)HeaderSize); // arm9 offset (placeholder)
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x2C), 0); // arm9 size
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x30), (uint)HeaderSize); // arm7 offset
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x3C), 0); // arm7 size
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x40), (uint)fntOff);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x44), (uint)fntSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x48), (uint)fatOff);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x4C), (uint)fatSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x80), (uint)totalSize); // ROM size

    // ── FAT ──
    for (var i = 0; i < n; i++) {
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)fatOff + i * 8), (uint)fileOffsets[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)fatOff + i * 8 + 4), (uint)(fileOffsets[i] + _files[i].data.Length));
    }

    // ── FNT ──
    // Root directory entry at FNT[0]:
    //   uint32 subtableOffset (relative to FNT start) = 8
    //   uint16 firstFileId = 0
    //   uint16 dirCount = 1 (just root)
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)fntOff), 8);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)fntOff + 4), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)fntOff + 6), 1);

    // Root subtable entries
    var subPos = (int)fntOff + 8;
    foreach (var (name, _) in _files) {
      var nameBytes = Encoding.ASCII.GetBytes(name);
      image[subPos++] = (byte)nameBytes.Length; // type=file, len=nameLen
      nameBytes.CopyTo(image, subPos);
      subPos += nameBytes.Length;
    }
    image[subPos] = 0; // terminator

    // ── File data ──
    for (var i = 0; i < n; i++)
      _files[i].data.CopyTo(image, fileOffsets[i]);

    output.Write(image);
  }

  private static long AlignUp(long v) => (v + Alignment - 1) & ~(Alignment - 1L);
}
