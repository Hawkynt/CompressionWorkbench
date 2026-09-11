#pragma warning disable CS1591

namespace FileFormat.UefiFv;

internal static class UefiFvConstants {
  internal const uint ErasePolarityMask = 0x00000800;
  internal const byte LargeFile = 0x01;
  internal const byte Alignment2 = 0x02;
  internal const byte Fixed = 0x04;
  internal const byte Alignment = 0x38;
  internal const byte HeaderConstruction = 0x01;
  internal const byte HeaderValid = 0x02;
  internal const byte DataValid = 0x04;
  internal const byte MarkedForUpdate = 0x08;
  internal const byte Deleted = 0x10;
  internal const byte HeaderInvalid = 0x20;
  internal static readonly Guid Ffs2Guid = Guid.Parse("8C8CE578-8A3D-4F1C-9935-896185C32DD3");
  internal static readonly Guid Ffs3Guid = Guid.Parse("5473C07A-3DCB-4DCA-BD6F-1E9689E7349A");
  internal static readonly Guid VolumeTopGuid = Guid.Parse("1BA0062E-C779-4582-8566-336AE8F78F09");
  internal static readonly Guid SignedContentsGuid = Guid.Parse("0F9D89E8-9259-4F76-A5AF-0C89E34023DF");

  internal static byte EffectiveState(byte raw, bool erasePolarity) {
    var value = (byte)((erasePolarity ? ~raw : raw) & 0x3F);
    if ((value & HeaderInvalid) != 0) return HeaderInvalid;
    if ((value & Deleted) != 0) return Deleted;
    if ((value & MarkedForUpdate) != 0) return MarkedForUpdate;
    if ((value & DataValid) != 0) return DataValid;
    if ((value & HeaderValid) != 0) return HeaderValid;
    return (value & HeaderConstruction) != 0 ? HeaderConstruction : (byte)0;
  }

  internal static byte EncodeState(byte cumulativeState, bool erasePolarity)
    => erasePolarity ? unchecked((byte)~cumulativeState) : cumulativeState;

  internal static int DataAlignment(byte attributes) {
    var code = (attributes & Alignment) >> 3;
    if ((attributes & Alignment2) != 0) return 1 << (17 + code);
    return code switch { 0 => 1, 1 => 16, 2 => 128, 3 => 512, 4 => 1024, 5 => 4096, 6 => 32768, _ => 65536 };
  }

  internal static int Align8(int value) => checked((int)(((long)value + 7) & ~7L));
  internal static int AlignOffset(int start, int absolute) => checked(start + Align8(checked(absolute - start)));
}

internal sealed record UefiFvSlot(int Offset, int HeaderSize, int Size, int Footprint, Guid Name,
  byte Type, byte Attributes, byte RawState, byte State) {
  public int DataOffset => checked(this.Offset + this.HeaderSize);
  public int DataLength => checked(this.Size - this.HeaderSize);
  public int DataEnd => checked(this.Offset + this.Size);
  public int End => checked(this.Offset + this.Footprint);
  public bool IsPad => this.Type == 0xF0;
  public bool IsFixed => (this.Attributes & UefiFvConstants.Fixed) != 0;
  public int DataAlignment => UefiFvConstants.DataAlignment(this.Attributes);
}

internal sealed record UefiFvLayout(int Start, int End, int DataStart, Guid FileSystemGuid, uint Attributes,
  bool ErasePolarity, byte EraseByte, int? UsedSizeOffset, bool IsSigned, IReadOnlyList<UefiFvSlot> Slots) {
  public bool SupportsLargeFiles => this.FileSystemGuid == UefiFvConstants.Ffs3Guid;
}
