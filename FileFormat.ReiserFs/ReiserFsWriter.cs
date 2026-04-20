#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.ReiserFs;

/// <summary>
/// Writes a minimal ReiserFS v3 filesystem image. Single leaf block containing
/// directory entries + direct file data items. Roundtrips through
/// <see cref="ReiserFsReader"/>.
/// </summary>
public sealed class ReiserFsWriter {
  private const int BlockSize = 4096;
  private const int SuperblockOff = 65536; // byte offset
  private const int LeafLevel = 1;
  private const int ItemHeaderSize = 24;
  private const int DehSize = 16;

  private readonly List<(string name, byte[] data)> _files = [];

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var leaf = Path.GetFileName(name);
    if (leaf.Length > 200) leaf = leaf[..200];
    _files.Add((leaf, data));
  }

  public void WriteTo(Stream output) {
    // Root block = superblock block + 1 = (65536/4096)+1 = 17
    var rootBlockNum = SuperblockOff / BlockSize + 1;
    var totalBlocks = rootBlockNum + 1;
    var image = new byte[totalBlocks * BlockSize];

    // ── Superblock ──
    Encoding.ASCII.GetBytes("ReIsErFs").CopyTo(image, SuperblockOff + 52);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(SuperblockOff + 44), (ushort)BlockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SuperblockOff + 20), (uint)rootBlockNum);

    // ── Root leaf block ──
    var boff = rootBlockNum * BlockSize;
    var n = _files.Count;
    var nrItems = 1 + n; // 1 dir item + N data items

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(boff), LeafLevel);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(boff + 2), (ushort)nrItems);

    // Build item data from the END of the block backward.
    var dataEnd = boff + BlockSize;

    // Item 0: directory with N deh_t entries + inline name strings
    var dirDehSize = n * DehSize;
    var nameBlob = new MemoryStream();
    var nameOffsets = new int[n];
    for (var i = 0; i < n; i++) {
      nameOffsets[i] = (int)nameBlob.Position;
      nameBlob.Write(Encoding.UTF8.GetBytes(_files[i].name));
      nameBlob.WriteByte(0); // null terminator
    }
    var nameBlobBytes = nameBlob.ToArray();
    var dirDataLen = dirDehSize + nameBlobBytes.Length;
    dataEnd -= dirDataLen;
    var dirDataOff = dataEnd;

    // Write deh_t entries
    for (var i = 0; i < n; i++) {
      var dehOff = dirDataOff + i * DehSize;
      // offset (uint32 LE) = unused
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(dehOff + 4), 1); // dir_id = 1
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(dehOff + 8), (uint)(100 + i)); // objectid
      var nameLocInItem = dirDehSize + nameOffsets[i];
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(dehOff + 12), (ushort)nameLocInItem);
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(dehOff + 14), 4); // state: visible
    }
    // Write name strings after deh_t entries
    nameBlobBytes.CopyTo(image, dirDataOff + dirDehSize);

    // Dir item header at boff+24
    var ihOff = boff + 24;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ihOff), 1); // dirId = 1 (root parent)
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ihOff + 4), 2); // objId = 2 (root dir)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ihOff + 16), (ushort)n); // count = N entries
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ihOff + 18), (ushort)dirDataLen); // length
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ihOff + 20), (ushort)(dirDataOff - boff)); // location

    // Items 1..N: direct data items
    for (var i = 0; i < n; i++) {
      var data = _files[i].data;
      dataEnd -= data.Length;
      data.CopyTo(image, dataEnd);

      var fihOff = boff + 24 + (1 + i) * ItemHeaderSize;
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(fihOff), 1); // dirId
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(fihOff + 4), (uint)(100 + i)); // objId
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(fihOff + 16), 0xFFFF); // count = 0xFFFF (direct)
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(fihOff + 18), (ushort)data.Length); // length
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(fihOff + 20), (ushort)(dataEnd - boff)); // location
    }

    output.Write(image);
  }
}
