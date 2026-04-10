#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Vdi;

/// <summary>
/// Writes a dynamic VirtualBox Disk Image (VDI) file.
/// Dynamic VDIs only allocate blocks for non-zero data; all-zero blocks
/// are represented by the sentinel value 0xFFFFFFFF in the block map.
/// </summary>
public sealed class VdiWriter : IDisposable {
  private const uint VdiSignature = 0xBEDA107F;
  private const uint VdiVersion = 0x00010001;
  private const uint VdiHeaderSize = 400u;
  private const uint VdiTypeDynamic = 1u;
  private const uint SectorSize = 512u;

  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly long _virtualSize;
  private readonly uint _blockSize;

  /// <param name="output">Output stream to write VDI into.</param>
  /// <param name="leaveOpen">If false, the stream is disposed when this writer is disposed.</param>
  /// <param name="virtualSize">Virtual disk size in bytes.</param>
  /// <param name="blockSize">Block size in bytes (default 65536).</param>
  public VdiWriter(Stream output, bool leaveOpen = false, long virtualSize = 0, uint blockSize = 65536) {
    _output = output;
    _leaveOpen = leaveOpen;
    _virtualSize = virtualSize;
    _blockSize = blockSize > 0 ? blockSize : 65536;
  }

  /// <summary>
  /// Writes a complete dynamic VDI image from the supplied raw disk data.
  /// </summary>
  public void Write(byte[] diskData) {
    ArgumentNullException.ThrowIfNull(diskData);

    var virtualSize = _virtualSize > 0 ? _virtualSize : diskData.Length;
    if (virtualSize == 0) {
      WriteEmpty();
      return;
    }

    var blockCount = (uint)((virtualSize + _blockSize - 1) / _blockSize);

    // Determine which blocks are non-zero
    var blockMap = new uint[blockCount]; // maps logical block → physical block index; 0xFFFFFFFF = unallocated
    uint allocatedCount = 0;

    for (uint i = 0; i < blockCount; i++) {
      var srcOff = (long)i * _blockSize;
      var blockLen = (int)Math.Min(_blockSize, diskData.Length - srcOff);

      bool allZero = true;
      if (blockLen > 0) {
        var slice = diskData.AsSpan((int)srcOff, blockLen);
        foreach (var b in slice) {
          if (b != 0) { allZero = false; break; }
        }
      }

      if (allZero || blockLen <= 0) {
        blockMap[i] = 0xFFFFFFFF; // unallocated
      } else {
        blockMap[i] = allocatedCount++;
      }
    }

    // Layout:
    //   0        : 64 bytes pre-header
    //   64       : 4 bytes signature
    //   68       : 400 bytes header (version through UUIDs)
    //   offsetBlocks = 512 (after pre-header + signature + header area)
    //   offsetData   = offsetBlocks + blockCount * 4, rounded up to 512
    uint offsetBlocks = 512u; // header occupies bytes 0..467 (64+4+400), we pad to 512
    uint mapSize = blockCount * 4;
    uint offsetData = ((offsetBlocks + mapSize + 511u) / 512u) * 512u; // align to 512

    // Build output
    var buf = new byte[offsetData + (long)allocatedCount * _blockSize];

    // --- Pre-header (64 bytes) ---
    var preHeader = Encoding.ASCII.GetBytes("<<< Oracle VM VirtualBox Disk Image >>>\n");
    preHeader.CopyTo(buf, 0);
    // remaining bytes stay zero (null-padding)

    // --- Signature (offset 64) ---
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(64), VdiSignature);

    // --- Header (starts at 68) ---
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(68), VdiVersion);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(72), VdiHeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(76), VdiTypeDynamic);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(80), 0u);            // fFlags
    // Description (256 bytes) @ 84 — leave as zeros
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(340), offsetBlocks);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(344), offsetData);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(348), 0u);           // cCylinders
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(352), 0u);           // cHeads
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(356), 0u);           // cSectors
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(360), SectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(364), 0u);           // unused
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(368), (ulong)virtualSize);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(376), _blockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(380), 0u);           // cbBlockExtra
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(384), blockCount);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(388), allocatedCount);

    // UUIDs (random) @ 392, 408, 424, 440
    WriteGuid(buf, 392);
    WriteGuid(buf, 408);
    WriteGuid(buf, 424);
    WriteGuid(buf, 440);

    // --- Block allocation map ---
    for (uint i = 0; i < blockCount; i++) {
      BinaryPrimitives.WriteUInt32LittleEndian(
        buf.AsSpan((int)(offsetBlocks + i * 4)), blockMap[i]);
    }

    // --- Data blocks ---
    for (uint i = 0; i < blockCount; i++) {
      if (blockMap[i] == 0xFFFFFFFF) continue;

      var srcOff = (long)i * _blockSize;
      var dstOff = (long)offsetData + (long)blockMap[i] * _blockSize;
      var copyLen = (int)Math.Min(_blockSize, diskData.Length - srcOff);
      if (copyLen > 0)
        diskData.AsSpan((int)srcOff, copyLen).CopyTo(buf.AsSpan((int)dstOff, copyLen));
    }

    _output.Write(buf);
  }

  private static void WriteEmpty() {
    // Nothing to write for a zero-size disk
  }

  private static void WriteGuid(byte[] buf, int offset) {
    var g = Guid.NewGuid().ToByteArray();
    g.CopyTo(buf, offset);
  }

  public void Dispose() {
    if (!_leaveOpen) _output.Dispose();
  }
}
