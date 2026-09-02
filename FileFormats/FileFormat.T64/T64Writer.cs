#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.T64;

/// <summary>
/// Writes t 64 data.
/// </summary>
public sealed class T64Writer {
  private readonly List<(string Name, ushort StartAddress, byte[] Data)> _files = [];

    /// <summary>
  /// Performs the add file operation.
  /// </summary>
public void AddFile(string name, ushort startAddress, byte[] data) =>
    _files.Add((name, startAddress, data));

    /// <summary>
  /// Performs the add file operation.
  /// </summary>
public void AddFile(string name, byte[] data) =>
    _files.Add((name, 0x0801, data)); // default BASIC start address

    /// <summary>
  /// Performs the build operation.
  /// </summary>
public byte[] Build(string tapeName = "TAPE") {
    var headerSize = 64;
    var dirSize = _files.Count * 32;
    var dataStart = headerSize + dirSize;
    // A tape entry addresses its file inside the C64's 64 KB memory map, so no
    // entry can be larger than that — and the archive is built in memory.
    // Summing the sizes in an int overflowed before either limit was reported.
    var totalBytes = (long)dataStart;
    foreach (var (name, startAddr, data) in _files) {
      if (startAddr + (long)data.Length > 0x10000)
        throw new InvalidOperationException(
          $"T64: '{name}' is {data.Length:N0} bytes and loads at ${startAddr:X4}, past the end of " +
          "the C64's 64 KB address space that a tape entry's start/end addresses describe.");
      totalBytes += data.Length;
    }

    var totalSize = (int)totalBytes;
    var output = new byte[totalSize];

    // Header
    var sig = "C64S tape image file\0\0\0\0\0\0\0\0\0\0\0\0"u8;
    sig[..32].CopyTo(output);
    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(32), 0x0100); // version
    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(34), (ushort)_files.Count); // max entries
    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(36), (ushort)_files.Count); // used entries

    // Tape name (24 bytes at offset 40)
    var nameBytes = Encoding.ASCII.GetBytes(tapeName.Length > 24 ? tapeName[..24] : tapeName);
    nameBytes.CopyTo(output, 40);
    for (var j = nameBytes.Length; j < 24; j++)
      output[40 + j] = 0x20;

    // Directory entries + data
    var dataOffset = dataStart;
    for (var i = 0; i < _files.Count; i++) {
      var (name, startAddr, data) = _files[i];
      var entryOff = headerSize + i * 32;

      output[entryOff] = 1; // normal entry
      output[entryOff + 1] = 0x82; // PRG

      BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(entryOff + 2), startAddr);
      BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(entryOff + 4), (ushort)(startAddr + data.Length));
      BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(entryOff + 8), (uint)dataOffset);

      // Filename
      var fnameBytes = Encoding.ASCII.GetBytes(name.Length > 16 ? name[..16] : name);
      fnameBytes.CopyTo(output, entryOff + 16);
      for (var j = fnameBytes.Length; j < 16; j++)
        output[entryOff + 16 + j] = 0x20;

      // Copy data
      data.CopyTo(output, dataOffset);
      dataOffset += data.Length;
    }

    return output;
  }
}
