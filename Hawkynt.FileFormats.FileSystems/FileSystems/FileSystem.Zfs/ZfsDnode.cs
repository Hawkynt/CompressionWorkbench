using System.Buffers.Binary;

namespace FileSystem.Zfs;

/// <summary>A parsed <c>dnode_phys_t</c>, large dnodes included (<c>docs/ZFS-ON-DISK.md</c>, "Dnodes").</summary>
internal sealed class ZfsDnode {
  private ZfsDnode() { }

  public byte Type { get; private init; }
  public int IndirectBlockShift { get; private init; }
  public int Levels { get; private init; }
  public byte BonusType { get; private init; }
  public int DataBlockSize { get; private init; }
  public ulong MaxBlockId { get; private init; }
  public ZfsBlockPointer[] BlockPointers { get; private init; } = [];
  public byte[] Bonus { get; private init; } = [];
  public ZfsBlockPointer? Spill { get; private init; }

  /// <summary>Parses the dnode at the start of <paramref name="slots"/>; returns null for a free slot.</summary>
  public static ZfsDnode? Read(ReadOnlySpan<byte> slots) {
    if (slots.Length < ZfsConstants.DnodeSlotSize)
      throw new InvalidDataException("ZFS: dnode truncated.");
    var type = slots[0];
    if (type == 0) return null;

    var extraSlots = slots[0x0C];
    var size = (1 + extraSlots) * ZfsConstants.DnodeSlotSize;
    if (size > slots.Length)
      throw new InvalidDataException("ZFS: large dnode runs past its block.");
    var dnode = slots[..size];
    var pointers = dnode[0x03];
    var flags = dnode[0x07];
    var hasSpill = (flags & ZfsConstants.DnodeFlagSpillBlkptr) != 0;
    if (pointers is < 1 or > 3)
      throw new InvalidDataException($"ZFS: dnode claims {pointers} block pointers.");

    var bonusAt = 0x40 + pointers * ZfsConstants.BlockPointerSize;
    var bonusLength = BinaryPrimitives.ReadUInt16LittleEndian(dnode[0x0A..]);
    var bonusEnd = size - (hasSpill ? ZfsConstants.BlockPointerSize : 0);
    if (bonusAt + bonusLength > bonusEnd)
      throw new InvalidDataException("ZFS: dnode bonus runs past the dnode.");

    var blockPointers = new ZfsBlockPointer[pointers];
    for (var i = 0; i < pointers; ++i)
      blockPointers[i] = ZfsBlockPointer.Read(dnode[(0x40 + i * ZfsConstants.BlockPointerSize)..]);

    return new ZfsDnode {
      Type = type,
      IndirectBlockShift = dnode[0x01],
      Levels = dnode[0x02],
      BonusType = dnode[0x04],
      DataBlockSize = BinaryPrimitives.ReadUInt16LittleEndian(dnode[0x08..]) << ZfsConstants.SectorShift,
      MaxBlockId = BinaryPrimitives.ReadUInt64LittleEndian(dnode[0x10..]),
      BlockPointers = blockPointers,
      Bonus = dnode.Slice(bonusAt, bonusLength).ToArray(),
      Spill = hasSpill ? ZfsBlockPointer.Read(dnode[(size - ZfsConstants.BlockPointerSize)..]) : null,
    };
  }
}
