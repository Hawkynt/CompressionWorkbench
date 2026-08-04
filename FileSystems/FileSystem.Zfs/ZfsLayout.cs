#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Zfs;

/// <summary>
/// Walks a pool the way <see cref="ZfsReader" /> does, but writes down where it
/// has been: the byte offset of every block pointer on the path to a file's
/// data, and of every block those pointers name.
/// </summary>
/// <remarks>
/// <para>A block pointer carries a Fletcher-4 over what it points at, so moving
/// a block leaves its own check good and every check above it stale. Putting
/// that right means knowing the path — which the reader traverses but never
/// records, because reading only ever needs the block in front of it.</para>
///
/// <para>The path is long: the uberblock, the meta object set, a dnode array,
/// the dataset's own object set, another dnode array, and then the file's
/// indirect blocks. Each step is written down here once, and the checks are
/// taken again from the bottom up after a layout pass.</para>
/// </remarks>
internal static class ZfsLayout {

  /// <summary>A block pointer, and the block it names.</summary>
  /// <param name="PointerOffset">Where the 128-byte block pointer sits.</param>
  /// <param name="BlockOffset">Where the block it names sits.</param>
  /// <param name="BlockLength">How long that block is on disk.</param>
  internal readonly record struct Pointer(long PointerOffset, long BlockOffset, int BlockLength);

  /// <summary>One block of file data, and the pointer that names it.</summary>
  internal readonly record struct DataBlock(
    long Offset, int Length, string Owner, long PointerOffset);

  /// <summary>What a pool is made of.</summary>
  internal sealed class Layout {
    /// <summary>Blocks the pool's own structure occupies, including the labels.</summary>
    public List<(long Offset, int Length)> Structure { get; } = [];

    /// <summary>Every block of file data.</summary>
    public List<DataBlock> DataBlocks { get; } = [];

    /// <summary>
    /// Every pointer on the path to a file's data, deepest last. Taking the
    /// checks again in reverse is what settles the pool.
    /// </summary>
    public List<Pointer> Pointers { get; } = [];
  }

  /// <summary>Walks the pool, or returns null when it is not one this reads.</summary>
  public static Layout? Read(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek || image.Length < ZfsConstants.LabelSize) return null;

    var layout = new Layout();

    // The four labels are the pool's own, wherever the rest of it ends up.
    layout.Structure.Add((0, ZfsConstants.LabelSize));
    layout.Structure.Add((ZfsConstants.LabelSize, ZfsConstants.LabelSize));
    if (image.Length >= 4L * ZfsConstants.LabelSize) {
      layout.Structure.Add((image.Length - 2L * ZfsConstants.LabelSize, ZfsConstants.LabelSize));
      layout.Structure.Add((image.Length - ZfsConstants.LabelSize, ZfsConstants.LabelSize));
    }

    var rootPointer = FindRootPointer(image);
    if (rootPointer < 0) return null;

    try {
      WalkPool(image, layout, rootPointer);
    } catch (InvalidDataException) {
      return null;
    } catch (ArgumentOutOfRangeException) {
      return null;
    }

    return layout;
  }

  /// <summary>Where the newest uberblock's root pointer sits in the first label.</summary>
  private static long FindRootPointer(Stream image) {
    long best = -1;
    ulong bestTxg = 0;
    var slot = new byte[ZfsConstants.UberblockSize];

    for (var at = (long)ZfsConstants.UberblockArrayOffset;
         at + ZfsConstants.UberblockSize <= ZfsConstants.LabelSize;
         at += ZfsConstants.UberblockSize) {
      image.Position = at;
      image.ReadExactly(slot);
      if (BinaryPrimitives.ReadUInt64LittleEndian(slot) != ZfsConstants.UberblockMagic) continue;

      var txg = BinaryPrimitives.ReadUInt64LittleEndian(slot.AsSpan(0x10));
      if (best >= 0 && txg <= bestTxg) continue;
      best = at;
      bestTxg = txg;
    }

    return best < 0 ? -1 : best + Uberblock.RootBpOffset;
  }

  /// <summary>Follows the path from the root pointer down to every file's data.</summary>
  private static void WalkPool(Stream image, Layout layout, long rootPointer) {
    var mos = Follow(image, layout, rootPointer);
    if (mos == null) return;

    var mosDnodes = ReadDnodeArray(image, layout, mos.Value);
    if (mosDnodes == null || mosDnodes.Count <= 1) return;

    // The object directory names the root dataset, and the DSL dir names the
    // dataset whose own pointer leads to the files.
    var objectDirectory = ReadZapEntries(image, layout, mosDnodes, 1);
    if (!objectDirectory.TryGetValue("root_dataset", out var rootDatasetId)) return;
    if (rootDatasetId >= (ulong)mosDnodes.Count) return;

    var dslDir = ReadBonus(image, mosDnodes[(int)rootDatasetId], DslDirPhys.Size);
    if (dslDir == null) return;

    var headDataset = DslDirPhys.Decode(dslDir).HeadDatasetObj;
    if (headDataset == 0 || headDataset >= (ulong)mosDnodes.Count) return;

    var datasetDnode = mosDnodes[(int)headDataset];
    var bonusAt = datasetDnode.Offset + Dnode.BonusOffset;
    var datasetPointer = bonusAt + DslDatasetPhys.BpOffset;

    var dataset = Follow(image, layout, datasetPointer);
    if (dataset == null) return;

    var datasetDnodes = ReadDnodeArray(image, layout, dataset.Value);
    if (datasetDnodes == null || datasetDnodes.Count <= 1) return;

    var master = ReadZapEntries(image, layout, datasetDnodes, 1);
    if (!master.TryGetValue("ROOT", out var rootDirectory)) return;

    CollectDirectory(image, layout, datasetDnodes, rootDirectory, "", 0);
  }

  /// <summary>Reads a directory's entries and follows each file's dnode to its data.</summary>
  private static void CollectDirectory(Stream image, Layout layout, List<DnodeAt> dnodes,
      ulong directoryId, string path, int depth) {
    if (depth > 32 || directoryId >= (ulong)dnodes.Count) return;

    foreach (var (name, value) in ReadZapEntries(image, layout, dnodes, (int)directoryId)) {
      var childId = value & 0x0000FFFFFFFFFFFFUL;
      if (childId == 0 || childId >= (ulong)dnodes.Count) continue;

      var full = path.Length == 0 ? name : $"{path}/{name}";
      var child = dnodes[(int)childId];
      if (child.Type == ZfsConstants.DmuOtDirectoryContents) {
        CollectDirectory(image, layout, dnodes, childId, full, depth + 1);
        continue;
      }

      if (child.Type != ZfsConstants.DmuOtPlainFileContents) continue;
      CollectFileBlocks(image, layout, child, full);
    }
  }

  /// <summary>Notes every data block a file's dnode reaches, through its indirect blocks.</summary>
  private static void CollectFileBlocks(Stream image, Layout layout, DnodeAt dnode, string owner) {
    var pointerAt = dnode.Offset + Dnode.BlkPtrOffset;
    if (dnode.Levels <= 1) {
      Note(image, layout, pointerAt, owner);
      return;
    }

    Descend(image, layout, pointerAt, dnode.Levels, owner);
  }

  /// <summary>Follows one level of indirect blocks, noting the data at the bottom.</summary>
  private static void Descend(Stream image, Layout layout, long pointerAt, int levels, string owner) {
    if (levels <= 1) {
      Note(image, layout, pointerAt, owner);
      return;
    }

    var indirect = Follow(image, layout, pointerAt);
    if (indirect == null) return;

    layout.Structure.Add((indirect.Value.BlockOffset, indirect.Value.BlockLength));
    for (var at = 0; at + BlockPointer.Size <= indirect.Value.BlockLength; at += BlockPointer.Size) {
      var childAt = indirect.Value.BlockOffset + at;
      if (ReadPointer(image, childAt) is not { } child || child.BlockLength <= 0) continue;
      Descend(image, layout, childAt, levels - 1, owner);
    }
  }

  /// <summary>Records a data block and the pointer that names it.</summary>
  private static void Note(Stream image, Layout layout, long pointerAt, string owner) {
    if (ReadPointer(image, pointerAt) is not { } pointer) return;
    if (pointer.BlockLength <= 0 || pointer.BlockOffset <= 0) return;
    if (pointer.BlockOffset + pointer.BlockLength > image.Length) return;

    layout.DataBlocks.Add(new DataBlock(pointer.BlockOffset, pointer.BlockLength, owner, pointerAt));
    layout.Pointers.Add(pointer);
  }

  /// <summary>Follows a pointer, recording it and the block it names as structure.</summary>
  private static Pointer? Follow(Stream image, Layout layout, long pointerAt) {
    if (ReadPointer(image, pointerAt) is not { } pointer) return null;
    if (pointer.BlockOffset <= 0 || pointer.BlockLength <= 0) return null;
    if (pointer.BlockOffset + pointer.BlockLength > image.Length) return null;

    layout.Pointers.Add(pointer);
    layout.Structure.Add((pointer.BlockOffset, pointer.BlockLength));
    return pointer;
  }

  /// <summary>A dnode and where it sits.</summary>
  internal readonly record struct DnodeAt(long Offset, byte Type, byte Levels, ulong BonusLength);

  /// <summary>Reads the dnode array an object set's meta-dnode names.</summary>
  private static List<DnodeAt>? ReadDnodeArray(Stream image, Layout layout, Pointer objset) {
    var metaAt = objset.BlockOffset + ObjsetPhys.MetaDnodeOffset + Dnode.BlkPtrOffset;
    var array = Follow(image, layout, metaAt);
    if (array == null) return null;

    var dnodes = new List<DnodeAt>();
    var block = ReadBytes(image, array.Value.BlockOffset, array.Value.BlockLength);
    if (block == null) return null;

    for (var at = 0; at + Dnode.Size <= block.Length; at += Dnode.Size)
      dnodes.Add(new DnodeAt(array.Value.BlockOffset + at, block[at], block[at + 0x02],
        BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(at + 0x04))));

    return dnodes;
  }

  /// <summary>Reads a dnode's bonus area, which carries the DSL structures.</summary>
  private static byte[]? ReadBonus(Stream image, DnodeAt dnode, int length) =>
    ReadBytes(image, dnode.Offset + Dnode.BonusOffset, length);

  /// <summary>Reads a ZAP object's entries, following the pointer to its block.</summary>
  private static Dictionary<string, ulong> ReadZapEntries(Stream image, Layout layout,
      List<DnodeAt> dnodes, int objectId) {
    var entries = new Dictionary<string, ulong>(StringComparer.Ordinal);
    if (objectId < 0 || objectId >= dnodes.Count) return entries;

    var pointer = Follow(image, layout, dnodes[objectId].Offset + Dnode.BlkPtrOffset);
    if (pointer == null) return entries;

    var block = ReadBytes(image, pointer.Value.BlockOffset, pointer.Value.BlockLength);
    if (block == null) return entries;

    foreach (var (name, value) in MicroZap.Decode(block))
      entries[name] = value;
    return entries;
  }

  /// <summary>Reads a block pointer's address and on-disk size.</summary>
  private static Pointer? ReadPointer(Stream image, long at) {
    var bytes = ReadBytes(image, at, BlockPointer.Size);
    if (bytes == null) return null;

    var word = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8));
    var sectors = word & 0x7FFFFFFFFFFFFFFFUL;
    if (sectors == 0) return null;

    var props = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x30));
    var psize = (int)(((props >> 16) & 0xFFFF) + 1) * (int)ZfsConstants.SectorSize;
    return new Pointer(at, (long)sectors * ZfsConstants.SectorSize, psize);
  }

  private static byte[]? ReadBytes(Stream image, long at, int length) {
    if (at < 0 || length <= 0 || at + length > image.Length) return null;

    var bytes = new byte[length];
    image.Position = at;
    image.ReadExactly(bytes);
    return bytes;
  }
}
