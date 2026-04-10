#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Tap;

/// <summary>
/// Writes ZX Spectrum TAP tape image files.
/// Each file is stored as a paired header block + data block.
/// Checksum = XOR of all bytes in the block (including the flag byte).
/// </summary>
public sealed class TapWriter : IDisposable {
  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly List<(string Name, byte[] Data, byte FileType)> _files = [];

  public TapWriter(Stream output, bool leaveOpen = false) {
    _output = output;
    _leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Queues a file to be written as a header+data block pair.
  /// </summary>
  /// <param name="name">Filename, padded/truncated to 10 characters.</param>
  /// <param name="data">File content.</param>
  /// <param name="fileType">0=Program, 1=NumArray, 2=CharArray, 3=Code (default).</param>
  public void AddFile(string name, byte[] data, byte fileType = 3) {
    _files.Add((name, data, fileType));
  }

  /// <summary>
  /// Writes all queued files to the output stream as sequential block pairs.
  /// </summary>
  public void Finish() {
    foreach (var (name, data, fileType) in _files) {
      WriteHeaderBlock(name, data, fileType);
      WriteDataBlock(data);
    }
  }

  private void WriteHeaderBlock(string name, byte[] data, byte fileType) {
    // Header block body: flag(1) + fileType(1) + name(10) + dataLen(2) + param1(2) + param2(2) + checksum(1) = 19 bytes
    // Block length word = 19
    var block = new byte[19];
    block[0] = 0x00; // flag: header
    block[1] = fileType;

    // Name: 10 bytes, space-padded
    var nameBytes = Encoding.ASCII.GetBytes(name.Length > 10 ? name[..10] : name);
    nameBytes.CopyTo(block, 2);
    for (var i = nameBytes.Length; i < 10; i++)
      block[2 + i] = 0x20;

    // dataLength (uint16 LE)
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(12), (ushort)data.Length);
    // param1 and param2 left as zero

    // Checksum: XOR of all bytes in the block except the checksum byte itself
    byte cs = 0;
    for (var i = 0; i < 18; i++)
      cs ^= block[i];
    block[18] = cs;

    // Write block length then block
    WriteUInt16LE((ushort)block.Length);
    _output.Write(block);
  }

  private void WriteDataBlock(byte[] data) {
    // Data block body: flag(1) + data(n) + checksum(1) = n+2 bytes
    var blockLength = data.Length + 2;
    WriteUInt16LE((ushort)blockLength);

    byte cs = 0xFF; // flag byte contributes to checksum
    _output.WriteByte(0xFF);

    foreach (var b in data) {
      cs ^= b;
      _output.WriteByte(b);
    }

    _output.WriteByte(cs);
  }

  private void WriteUInt16LE(ushort value) {
    Span<byte> buf = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(buf, value);
    _output.Write(buf);
  }

  public void Dispose() {
    Finish();
    if (!_leaveOpen)
      _output.Dispose();
  }
}
