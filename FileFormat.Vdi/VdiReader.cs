#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Vdi;

/// <summary>
/// Reads VirtualBox Disk Image (VDI) files.
/// Layout:
///   Offset   0: 64 bytes pre-header text (null-padded)
///   Offset  64: uint32 LE signature = 0xBEDA107F
///   Offset  68: uint32 version
///   Offset  72: uint32 cbHeader (size of header, usually 400)
///   Offset  76: uint32 uImageType (1=dynamic, 2=fixed)
///   Offset  80: uint32 fFlags
///   Offset  84: 256 bytes description (null-terminated)
///   Offset 340: uint32 offsetBlocks
///   Offset 344: uint32 offsetData
///   Offset 348: uint32 cCylinders
///   Offset 352: uint32 cHeads
///   Offset 356: uint32 cSectors
///   Offset 360: uint32 cbSector (512)
///   Offset 364: uint32 unused
///   Offset 368: uint64 cbDisk (virtual disk size in bytes)
///   Offset 376: uint32 cbBlock (block size, typically 1MB)
///   Offset 380: uint32 cbBlockExtra (usually 0)
///   Offset 384: uint32 cBlocks (total number of blocks)
///   Offset 388: uint32 cBlocksAllocated
///   Offset 392: 16 bytes UUID image
///   Offset 408: 16 bytes UUID last snapshot
///   Offset 424: 16 bytes UUID link
///   Offset 440: 16 bytes UUID parent
/// </summary>
public sealed class VdiReader : IDisposable {
  public const uint VdiSignature = 0xBEDA107F;
  private static readonly byte[] PreHeaderText =
    Encoding.ASCII.GetBytes("<<< Oracle VM VirtualBox Disk Image >>>\n");

  private readonly byte[] _data;

  /// <summary>Virtual disk size in bytes.</summary>
  public long VirtualSize { get; private set; }

  /// <summary>Block size in bytes.</summary>
  public uint BlockSize { get; private set; }

  /// <summary>Total number of blocks (including unallocated).</summary>
  public uint BlockCount { get; private set; }

  /// <summary>Number of allocated blocks.</summary>
  public uint AllocatedBlockCount { get; private set; }

  /// <summary>Offset of the block allocation map.</summary>
  public uint OffsetBlocks { get; private set; }

  /// <summary>Offset of the first data block.</summary>
  public uint OffsetData { get; private set; }

  /// <summary>Image type: 1 = dynamic, 2 = fixed.</summary>
  public uint ImageType { get; private set; }

  public VdiReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 512)
      throw new InvalidDataException("VDI: file too small.");

    // Verify signature at offset 64
    if (_data.Length < 68)
      throw new InvalidDataException("VDI: file too small for signature.");

    var sig = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(64));
    if (sig != VdiSignature)
      throw new InvalidDataException($"VDI: invalid signature 0x{sig:X8}, expected 0x{VdiSignature:X8}.");

    // Parse header fields
    // uint32 version          @ 68
    // uint32 cbHeader         @ 72
    // uint32 uImageType       @ 76
    // uint32 fFlags           @ 80
    // description[256]        @ 84
    // uint32 offsetBlocks     @ 340
    // uint32 offsetData       @ 344
    // uint32 cCylinders       @ 348
    // uint32 cHeads           @ 352
    // uint32 cSectors         @ 356
    // uint32 cbSector         @ 360
    // uint32 unused           @ 364
    // uint64 cbDisk           @ 368
    // uint32 cbBlock          @ 376
    // uint32 cbBlockExtra     @ 380
    // uint32 cBlocks          @ 384
    // uint32 cBlocksAllocated @ 388

    if (_data.Length < 392)
      throw new InvalidDataException("VDI: header truncated.");

    ImageType = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(76));
    OffsetBlocks = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(340));
    OffsetData = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(344));
    VirtualSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(368));
    BlockSize = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(376));
    BlockCount = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(384));
    AllocatedBlockCount = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(388));

    if (BlockSize == 0)
      throw new InvalidDataException("VDI: block size is zero.");
    if (VirtualSize < 0)
      throw new InvalidDataException("VDI: invalid virtual disk size.");
  }

  /// <summary>
  /// Reconstructs the full disk image by reading all blocks sequentially.
  /// Unallocated blocks (map entry = 0xFFFFFFFF) are returned as zeros.
  /// </summary>
  public byte[] ExtractDisk() {
    if (VirtualSize == 0) return [];

    var result = new byte[VirtualSize];
    var mapSpan = _data.AsSpan((int)OffsetBlocks, (int)BlockCount * 4);

    for (uint i = 0; i < BlockCount; i++) {
      var blockMapEntry = BinaryPrimitives.ReadUInt32LittleEndian(mapSpan.Slice((int)(i * 4), 4));
      if (blockMapEntry == 0xFFFFFFFF)
        continue; // unallocated — leave as zero

      var srcOff = (long)OffsetData + (long)blockMapEntry * BlockSize;
      var dstOff = (long)i * BlockSize;
      var copyLen = (int)Math.Min(BlockSize, VirtualSize - dstOff);

      if (srcOff + copyLen > _data.Length)
        copyLen = (int)Math.Max(0, _data.Length - srcOff);

      if (copyLen > 0)
        _data.AsSpan((int)srcOff, copyLen).CopyTo(result.AsSpan((int)dstOff, copyLen));
    }

    return result;
  }

  public void Dispose() { }
}
