#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Vdi;

/// <summary>
/// Writes a dynamic VirtualBox Disk Image (VDI) file. Non-zero blocks are
/// allocated; all-zero blocks use VDI_DISCARDED (0xFFFFFFFE), whose semantics
/// are explicitly zero-filled rather than merely never-allocated/undefined.
/// </summary>
public sealed class VdiWriter : IDisposable {
  private const uint VdiSignature = 0xBEDA107F;
  private const uint VdiVersion = 0x00010001;
  private const uint VdiHeaderSize = 400u;
  private const uint VdiTypeDynamic = 1u;
  private const uint SectorSize = 512u;
  internal const uint DiscardedBlock = 0xFFFF_FFFEu;
  internal const uint UnallocatedBlock = 0xFFFF_FFFFu;
  private const uint DefaultBlockSize = 1u << 20;

  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly long _virtualSize;
  private readonly uint _blockSize;

  public VdiWriter(Stream output, bool leaveOpen = false, long virtualSize = 0, uint blockSize = DefaultBlockSize) {
    ArgumentNullException.ThrowIfNull(output);
    this._output = output;
    this._leaveOpen = leaveOpen;
    this._virtualSize = virtualSize;
    this._blockSize = blockSize > 0 ? blockSize : DefaultBlockSize;
  }

  public void Write(byte[] diskData) {
    ArgumentNullException.ThrowIfNull(diskData);

    var virtualSize = this._virtualSize > 0 ? this._virtualSize : diskData.LongLength;
    if (virtualSize <= 0) {
      WriteEmpty();
      return;
    }
    if (virtualSize > int.MaxValue)
      throw new NotSupportedException("VDI writer currently requires a virtual disk smaller than 2 GiB.");

    var blockCountLong = (virtualSize + this._blockSize - 1) / this._blockSize;
    if (blockCountLong > uint.MaxValue)
      throw new NotSupportedException("VDI block map exceeds the supported entry count.");
    var blockCount = (uint)blockCountLong;

    var blockMap = new uint[blockCount];
    uint allocatedCount = 0;
    for (uint i = 0; i < blockCount; ++i) {
      var sourceOffset = (long)i * this._blockSize;
      var available = Math.Max(0, Math.Min((long)this._blockSize, diskData.LongLength - sourceOffset));
      var length = checked((int)available);
      var allZero = true;
      if (length > 0) {
        foreach (var value in diskData.AsSpan(checked((int)sourceOffset), length)) {
          if (value == 0) continue;
          allZero = false;
          break;
        }
      }

      blockMap[i] = length == 0 || allZero ? DiscardedBlock : allocatedCount++;
    }

    const uint offsetBlocks = 512u;
    var mapSize = checked(blockCount * 4u);
    var offsetData = checked((offsetBlocks + mapSize + 511u) / 512u * 512u);
    var totalSize = checked((long)offsetData + (long)allocatedCount * this._blockSize);
    if (totalSize > int.MaxValue)
      throw new NotSupportedException("VDI container exceeds the current buffered writer limit.");
    var buffer = new byte[checked((int)totalSize)];

    Encoding.ASCII.GetBytes("<<< Oracle VM VirtualBox Disk Image >>>\n").CopyTo(buffer, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(64), VdiSignature);
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(68), VdiVersion);
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(72), VdiHeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(76), VdiTypeDynamic);
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(80), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(340), offsetBlocks);
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(344), offsetData);
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(360), SectorSize);
    BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(368), checked((ulong)virtualSize));
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(376), this._blockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(384), blockCount);
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(388), allocatedCount);
    WriteGuid(buffer, 392);
    WriteGuid(buffer, 408);

    for (uint i = 0; i < blockCount; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(checked((int)(offsetBlocks + i * 4))), blockMap[i]);

    for (uint i = 0; i < blockCount; ++i) {
      var physicalBlock = blockMap[i];
      if (physicalBlock >= DiscardedBlock) continue;
      var sourceOffset = (long)i * this._blockSize;
      var copyLength = checked((int)Math.Max(0, Math.Min((long)this._blockSize, diskData.LongLength - sourceOffset)));
      if (copyLength <= 0) continue;
      var destinationOffset = checked((long)offsetData + (long)physicalBlock * this._blockSize);
      diskData.AsSpan(checked((int)sourceOffset), copyLength)
        .CopyTo(buffer.AsSpan(checked((int)destinationOffset), copyLength));
    }

    this._output.Write(buffer);
  }

  private static void WriteEmpty() { }

  private static void WriteGuid(byte[] buffer, int offset)
    => Guid.NewGuid().ToByteArray().CopyTo(buffer, offset);

  public void Dispose() {
    if (!this._leaveOpen) this._output.Dispose();
  }
}
