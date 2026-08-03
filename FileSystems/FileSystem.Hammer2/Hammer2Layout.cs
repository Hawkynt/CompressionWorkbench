#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Hammer2;

/// <summary>
/// Walks a HAMMER2 volume's blockref tree and notes where every block sits,
/// together with the chain of checks that has to be taken again when one of
/// them moves.
/// </summary>
/// <remarks>
/// <para>A blockref names its block by a device offset with the block's radix
/// in the low bits, and carries a check over the bytes it points at. Moving
/// those bytes leaves that check good — the bytes do not change — but the
/// blockref itself lives inside its parent block, and the parent's check lives
/// in the blockref that names the parent, and so on up to the volume header,
/// which carries CRCs over its own sectors.</para>
///
/// <para>So a move is one field plus a chain of checks, and the chain is what
/// this records: for each block, every enclosing block in turn and the field
/// its check belongs in.</para>
/// </remarks>
internal static class Hammer2Layout {

  internal const int VolumeBytes = 65536;
  internal const int NumVolumeHeaders = 4;
  internal const int BlockrefBytes = 128;
  internal const int SetCount = 4;
  internal const int InodeBytes = 1024;
  internal const int BlocksetOffset = 0x200;

  internal const byte BrefTypeInode = 1;
  internal const byte BrefTypeIndirect = 2;
  internal const byte BrefTypeData = 3;

  /// <summary>A block whose check belongs in a field somewhere else.</summary>
  /// <param name="BlockOffset">Where the block sits.</param>
  /// <param name="BlockLength">How long it is.</param>
  /// <param name="CheckFieldOffset">Where the check over it belongs.</param>
  internal readonly record struct CheckLink(long BlockOffset, int BlockLength, long CheckFieldOffset);

  /// <summary>One block of file data, and everything a move of it has to touch.</summary>
  /// <param name="Offset">Where the data sits.</param>
  /// <param name="Length">How long it is, which its radix fixes.</param>
  /// <param name="Owner">The file it belongs to.</param>
  /// <param name="DataOffsetField">Where the blockref's device offset sits.</param>
  /// <param name="Radix">The radix the offset is encoded with.</param>
  /// <param name="Chain">Enclosing blocks, innermost first.</param>
  internal readonly record struct DataBlock(
    long Offset, int Length, string Owner, long DataOffsetField, int Radix,
    IReadOnlyList<CheckLink> Chain);

  /// <summary>What a volume is made of.</summary>
  internal sealed class Layout {
    /// <summary>Every volume header the image carries, in image order.</summary>
    public List<long> VolumeHeaders { get; } = [];

    /// <summary>Blocks the volume's own structure occupies.</summary>
    public List<(long Offset, int Length)> Structure { get; } = [];

    /// <summary>Every block of file data.</summary>
    public List<DataBlock> DataBlocks { get; } = [];
  }

  /// <summary>Walks the volume, or returns null when it is not one this reads.</summary>
  public static Layout? Read(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek || image.Length < VolumeBytes) return null;

    var layout = new Layout();
    long best = -1;
    ulong bestTid = 0;
    for (var slot = 0; slot < NumVolumeHeaders; ++slot) {
      var at = (long)slot * VolumeBytes;
      if (at + VolumeBytes > image.Length) break;

      var magic = ReadUInt64(image, at);
      if (magic != 0x48414D3205172011UL && magic != 0x11201705324D4148UL) continue;

      layout.VolumeHeaders.Add(at);
      layout.Structure.Add((at, VolumeBytes));
      var mirrorTid = ReadUInt64(image, at + 0x78);
      if (best < 0 || mirrorTid >= bestTid) { best = at; bestTid = mirrorTid; }
    }

    if (best < 0) return null;

    // The super-root's blockref lives in the header, so the header is where the
    // chain of checks ends.
    var srootBref = best + BlocksetOffset;
    var bref = ReadBlockref(image, srootBref);
    if (bref.Type != BrefTypeInode) return null;

    var sroot = DecodeOffset(bref.DataOff);
    layout.Structure.Add((sroot, InodeBytes));
    WalkInode(image, layout, sroot, "", [new CheckLink(sroot, InodeBytes, srootBref + 64)], 0);
    return layout;
  }

  /// <summary>Reads one inode's blockset, following whatever it names.</summary>
  private static void WalkInode(Stream image, Layout layout, long inodeOffset, string path,
      List<CheckLink> chain, int depth) {
    if (depth > 16) return;

    var inode = ReadBytes(image, inodeOffset, InodeBytes);
    if (inode == null) return;

    var name = ReadInodeName(inode);
    var full = path.Length == 0 ? name : $"{path}/{name}";

    // A file small enough to live in the inode has no block of its own.
    if ((inode[0x51] & 0x01) != 0) return;

    for (var i = 0; i < SetCount; ++i) {
      var brefAt = inodeOffset + BlocksetOffset + i * BlockrefBytes;
      var bref = ReadBlockref(image, brefAt);
      if (bref.Type == 0) continue;

      var target = DecodeOffset(bref.DataOff);
      var radix = RadixOf(bref.DataOff);
      var length = 1 << radix;
      if (target <= 0 || target + length > image.Length) continue;

      switch (bref.Type) {
        case BrefTypeInode: {
          layout.Structure.Add((target, InodeBytes));
          var deeper = new List<CheckLink> { new(target, InodeBytes, brefAt + 64) };
          deeper.AddRange(chain);
          WalkInode(image, layout, target, full, deeper, depth + 1);
          break;
        }

        case BrefTypeIndirect: {
          layout.Structure.Add((target, length));
          var deeper = new List<CheckLink> { new(target, length, brefAt + 64) };
          deeper.AddRange(chain);
          WalkIndirect(image, layout, target, length, full, deeper, depth + 1);
          break;
        }

        case BrefTypeData:
          layout.DataBlocks.Add(new DataBlock(target, length, full, brefAt + 32, radix, chain));
          break;

        default:
          break;   // a dirent carries its name inline and points at no block
      }
    }
  }

  /// <summary>Reads a block of blockrefs, following whatever it names.</summary>
  private static void WalkIndirect(Stream image, Layout layout, long blockOffset, int blockLength,
      string owner, List<CheckLink> chain, int depth) {
    if (depth > 16) return;

    for (var at = 0; at + BlockrefBytes <= blockLength; at += BlockrefBytes) {
      var brefAt = blockOffset + at;
      var bref = ReadBlockref(image, brefAt);
      if (bref.Type == 0) continue;

      var target = DecodeOffset(bref.DataOff);
      var radix = RadixOf(bref.DataOff);
      var length = 1 << radix;
      if (target <= 0 || target + length > image.Length) continue;

      switch (bref.Type) {
        case BrefTypeData:
          layout.DataBlocks.Add(new DataBlock(target, length, owner, brefAt + 32, radix, chain));
          break;

        // A directory's children hang off an indirect block once there are more
        // than the four a blockset holds, so this is where the files are.
        case BrefTypeInode: {
          layout.Structure.Add((target, InodeBytes));
          var child = new List<CheckLink> { new(target, InodeBytes, brefAt + 64) };
          child.AddRange(chain);
          WalkInode(image, layout, target, owner, child, depth + 1);
          break;
        }

        case BrefTypeIndirect: {
          layout.Structure.Add((target, length));
          var deeper = new List<CheckLink> { new(target, length, brefAt + 64) };
          deeper.AddRange(chain);
          WalkIndirect(image, layout, target, length, owner, deeper, depth + 1);
          break;
        }

        default:
          break;
      }
    }
  }

  /// <summary>
  /// The inode's own name, which HAMMER2 keeps inline: a length at 0x80 and the
  /// characters at 0x100. A file's on-disk name is the hex of its inode number,
  /// which is unique, and unique is what a layout pass needs of an owner.
  /// </summary>
  private static string ReadInodeName(byte[] inode) {
    var length = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(0x80));
    if (length == 0 || 0x100 + length > inode.Length) return "";
    return Encoding.UTF8.GetString(inode, 0x100, Math.Min((int)length, 255)).TrimEnd('\0');
  }

  internal readonly record struct Blockref(byte Type, long DataOff);

  private static Blockref ReadBlockref(Stream image, long at) {
    var bytes = ReadBytes(image, at, BlockrefBytes);
    if (bytes == null) return default;
    return new Blockref(bytes[0], BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(32)));
  }

  internal static long DecodeOffset(long dataOff) => dataOff & ~0x3FL;

  internal static int RadixOf(long dataOff) => (int)(dataOff & 0x3F);

  internal static long EncodeDataOff(long deviceOffset, int radix) => deviceOffset | (long)(radix & 0x3F);

  private static byte[]? ReadBytes(Stream image, long at, int length) {
    if (at < 0 || length <= 0 || at + length > image.Length) return null;

    var bytes = new byte[length];
    image.Position = at;
    image.ReadExactly(bytes);
    return bytes;
  }

  private static ulong ReadUInt64(Stream image, long at) {
    var bytes = ReadBytes(image, at, 8);
    return bytes == null ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(bytes);
  }
}
