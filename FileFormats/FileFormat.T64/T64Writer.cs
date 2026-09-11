#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.T64;

/// <summary>
/// Writes a Commodore 64 T64 tape container, building the tape record and the directory of program entries.
/// </summary>
public sealed class T64Writer {
  private readonly List<(string Name, ushort StartAddress, byte FileType, byte[] Data)> _files = [];

  /// <summary>
  /// Performs the add file operation.
  /// </summary>
  public void AddFile(string name, ushort startAddress, byte[] data)
    => AddFile(name, startAddress, 0x82, data);

  /// <summary>
  /// Adds a file while preserving its Commodore file-type byte.
  /// </summary>
  public void AddFile(string name, ushort startAddress, byte fileType, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    _files.Add((name, startAddress, fileType, data));
  }

  /// <summary>
  /// Performs the add file operation.
  /// </summary>
  public void AddFile(string name, byte[] data)
    => AddFile(name, 0x0801, 0x82, data); // default BASIC start address + PRG type

  /// <summary>
  /// Performs the build operation.
  /// </summary>
  public byte[] Build(string tapeName = "TAPE", ushort version = 0x0100) {
    ArgumentNullException.ThrowIfNull(tapeName);
    if (_files.Count > ushort.MaxValue)
      throw new InvalidOperationException("T64: directory entry count exceeds the 16-bit header field.");

    const int headerSize = 64;
    const int entrySize = 32;
    var dirSize = checked(_files.Count * entrySize);
    var dataStart = headerSize + dirSize;

    // A tape entry addresses its file inside the C64's 64 KB memory map, so no
    // entry can be larger than that. The archive itself is currently built in
    // memory, therefore it also has to fit a managed byte array.
    var totalBytes = (long)dataStart;
    foreach (var (name, startAddr, _, data) in _files) {
      if (startAddr + (long)data.Length > 0x10000)
        throw new InvalidOperationException(
          $"T64: '{name}' is {data.Length:N0} bytes and loads at ${startAddr:X4}, past the end of " +
          "the C64's 64 KB address space that a tape entry's start/end addresses describe.");
      totalBytes += data.Length;
      if (totalBytes > Array.MaxLength)
        throw new InvalidOperationException("T64: image is too large for the in-memory writer.");
    }

    var totalSize = checked((int)totalBytes);
    var output = new byte[totalSize];

    // Header
    var sig = "C64S tape image file\0\0\0\0\0\0\0\0\0\0\0\0"u8;
    sig[..32].CopyTo(output);
    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(32), version);
    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(34), (ushort)_files.Count);
    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(36), (ushort)_files.Count);

    // Tape name (24 bytes at offset 40)
    var tapeNameBytes = Encoding.ASCII.GetBytes(tapeName.Length > 24 ? tapeName[..24] : tapeName);
    tapeNameBytes.CopyTo(output, 40);
    output.AsSpan(40 + tapeNameBytes.Length, 24 - tapeNameBytes.Length).Fill(0x20);

    // Directory entries + data
    var dataOffset = dataStart;
    for (var i = 0; i < _files.Count; i++) {
      var (name, startAddr, fileType, data) = _files[i];
      var entryOff = headerSize + i * entrySize;

      output[entryOff] = 1; // normal entry
      output[entryOff + 1] = fileType;

      BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(entryOff + 2), startAddr);
      BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(entryOff + 4), unchecked((ushort)(startAddr + data.Length)));
      BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(entryOff + 8), (uint)dataOffset);

      // Filename
      var fnameBytes = Encoding.ASCII.GetBytes(name.Length > 16 ? name[..16] : name);
      fnameBytes.CopyTo(output, entryOff + 16);
      output.AsSpan(entryOff + 16 + fnameBytes.Length, 16 - fnameBytes.Length).Fill(0x20);

      data.CopyTo(output, dataOffset);
      dataOffset += data.Length;
    }

    return output;
  }
}
