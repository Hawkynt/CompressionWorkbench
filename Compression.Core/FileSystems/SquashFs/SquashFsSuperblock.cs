using System.Buffers.Binary;

namespace FileSystem.SquashFs;

internal sealed class SquashFsSuperblock {
  public uint Magic { get; private init; }
  public uint InodeCount { get; private init; }
  public uint ModificationTime { get; private init; }
  public uint BlockSize { get; private init; }
  public uint FragmentCount { get; private init; }
  public ushort CompressionType { get; private init; }
  public ushort BlockLog { get; private init; }
  public ushort Flags { get; private init; }
  public ushort IdCount { get; private init; }
  public ushort VersionMajor { get; private init; }
  public ushort VersionMinor { get; private init; }
  public ulong RootInode { get; private init; }
  public ulong BytesUsed { get; private init; }
  public ulong IdTableStart { get; private init; }
  public ulong XattrIdTableStart { get; private init; }
  public ulong InodeTableStart { get; private init; }
  public ulong DirectoryTableStart { get; private init; }
  public ulong FragmentTableStart { get; private init; }
  public ulong LookupTableStart { get; private init; }

  // Derived helpers
  public bool UncompressedInodes   => (Flags & SquashFsConstants.FlagUncInode) != 0;
  public bool UncompressedData     => (Flags & SquashFsConstants.FlagUncData) != 0;
  public bool UncompressedFragments => (Flags & SquashFsConstants.FlagUncFragments) != 0;
  public bool NoFragments          => (Flags & SquashFsConstants.FlagNoFragments) != 0;

  public static SquashFsSuperblock Read(Stream stream) {
    Span<byte> buf = stackalloc byte[SquashFsConstants.SuperblockSize];
    var read = 0;
    while (read < buf.Length) {
      var n = stream.Read(buf[read..]);
      if (n == 0) throw new EndOfStreamException("Truncated SquashFS superblock.");
      read += n;
    }

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(buf);
    if (magic != SquashFsConstants.Magic)
      throw new InvalidDataException($"Invalid SquashFS magic: 0x{magic:X8}");

    var sb = new SquashFsSuperblock {
      Magic              = magic,
      InodeCount         = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..]),
      ModificationTime   = BinaryPrimitives.ReadUInt32LittleEndian(buf[8..]),
      BlockSize          = BinaryPrimitives.ReadUInt32LittleEndian(buf[12..]),
      FragmentCount      = BinaryPrimitives.ReadUInt32LittleEndian(buf[16..]),
      CompressionType    = BinaryPrimitives.ReadUInt16LittleEndian(buf[20..]),
      BlockLog           = BinaryPrimitives.ReadUInt16LittleEndian(buf[22..]),
      Flags              = BinaryPrimitives.ReadUInt16LittleEndian(buf[24..]),
      IdCount            = BinaryPrimitives.ReadUInt16LittleEndian(buf[26..]),
      VersionMajor       = BinaryPrimitives.ReadUInt16LittleEndian(buf[28..]),
      VersionMinor       = BinaryPrimitives.ReadUInt16LittleEndian(buf[30..]),
      RootInode          = BinaryPrimitives.ReadUInt64LittleEndian(buf[32..]),
      BytesUsed          = BinaryPrimitives.ReadUInt64LittleEndian(buf[40..]),
      IdTableStart       = BinaryPrimitives.ReadUInt64LittleEndian(buf[48..]),
      XattrIdTableStart  = BinaryPrimitives.ReadUInt64LittleEndian(buf[56..]),
      InodeTableStart    = BinaryPrimitives.ReadUInt64LittleEndian(buf[64..]),
      DirectoryTableStart = BinaryPrimitives.ReadUInt64LittleEndian(buf[72..]),
      FragmentTableStart = BinaryPrimitives.ReadUInt64LittleEndian(buf[80..]),
      LookupTableStart   = BinaryPrimitives.ReadUInt64LittleEndian(buf[88..]),
    };

    if (sb.VersionMajor != 4)
      throw new NotSupportedException($"Only SquashFS version 4 is supported; got {sb.VersionMajor}.{sb.VersionMinor}.");

    return sb;
  }
}
