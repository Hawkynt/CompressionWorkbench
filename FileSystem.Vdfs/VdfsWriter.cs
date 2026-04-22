#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Vdfs;

public sealed class VdfsWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  public byte[] Build() {
    var headerSize = 16;
    var fieldsSize = 20;
    var entrySize = 80;
    var entriesStart = headerSize + fieldsSize;
    var dataStart = entriesStart + _files.Count * entrySize;

    // Calculate total size
    var totalDataSize = 0;
    foreach (var (_, data) in _files)
      totalDataSize += data.Length;

    var totalSize = dataStart + totalDataSize;
    var result = new byte[totalSize];

    // Header
    "PSVDSC_V2.00\n\r\n\r"u8.CopyTo(result);

    // Fields
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), (uint)_files.Count); // entry count
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20), (uint)_files.Count); // file count
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24), 0); // timestamp
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28), (uint)totalDataSize); // data size
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(32), (uint)entriesStart); // root offset

    var currentDataOffset = dataStart;
    for (int i = 0; i < _files.Count; i++) {
      var (name, data) = _files[i];
      var entryOff = entriesStart + i * entrySize;

      // Name (64 bytes, space-padded)
      var nameBytes = Encoding.ASCII.GetBytes(name);
      Array.Fill(result, (byte)0x20, entryOff, 64);
      Array.Copy(nameBytes, 0, result, entryOff, Math.Min(nameBytes.Length, 64));
      result[entryOff + Math.Min(nameBytes.Length, 63)] = 0; // null terminate

      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(entryOff + 64), (uint)currentDataOffset);
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(entryOff + 68), (uint)data.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(entryOff + 72), 0x02); // type = file

      data.CopyTo(result, currentDataOffset);
      currentDataOffset += data.Length;
    }

    return result;
  }
}
