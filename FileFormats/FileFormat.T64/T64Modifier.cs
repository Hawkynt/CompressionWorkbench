#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.T64;

/// <summary>
/// In-place T64 modifier — performs O(touched bytes) random-access I/O against a
/// T64 tape image. T64 has a 64-byte header followed by a fixed-size directory
/// table of N×32-byte slots, then concatenated file data.
///
/// <para><b>AddFile</b>: finds an empty slot (entryType=0) in the directory,
/// appends file data at EOF, and fills in the slot.</para>
/// <para><b>RemoveFile</b>: sets the slot's entryType to 0 (marks it free).
/// Data is left in place (no compaction).</para>
/// </summary>
public static class T64Modifier {

  private const int HeaderSize = 64;
  private const int EntrySize = 32;

  /// <summary>
  /// Adds a file to an existing T64 tape image. Finds the first free slot
  /// (entryType=0) in the directory, appends the file data at the end of the
  /// image, and writes the directory entry.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data, ushort startAddress = 0x0801) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    // Read header to get maxEntries.
    image.Position = 34;
    Span<byte> hdrBuf = stackalloc byte[4];
    image.ReadExactly(hdrBuf);
    var maxEntries = BinaryPrimitives.ReadUInt16LittleEndian(hdrBuf);
    var usedEntries = BinaryPrimitives.ReadUInt16LittleEndian(hdrBuf.Slice(2));

    // Scan the directory for a free slot.
    var freeSlot = -1;
    for (var i = 0; i < maxEntries; i++) {
      var slotOff = HeaderSize + i * EntrySize;
      image.Position = slotOff;
      var typeByte = image.ReadByte();
      if (typeByte <= 0) { // 0 or EOF
        freeSlot = i;
        break;
      }
    }

    if (freeSlot < 0)
      throw new IOException($"T64: no free directory slot (max {maxEntries}).");

    // Append data at EOF.
    var dataOffset = (int)image.Length;
    image.Position = dataOffset;
    image.Write(data);

    // Build directory entry (32 bytes).
    var entry = new byte[EntrySize];
    entry[0] = 1; // entryType = normal
    entry[1] = 0x82; // C64 file type = PRG

    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2), startAddress);
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4), (ushort)(startAddress + data.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(8), (uint)dataOffset);

    // Filename: 16 bytes, space-padded.
    var nameBytes = Encoding.ASCII.GetBytes(name.Length > 16 ? name[..16] : name);
    nameBytes.CopyTo(entry, 16);
    for (var j = nameBytes.Length; j < 16; j++)
      entry[16 + j] = 0x20;

    // Write the directory entry.
    image.Position = HeaderSize + freeSlot * EntrySize;
    image.Write(entry);

    // Update usedEntries count.
    usedEntries++;
    image.Position = 36;
    Span<byte> usedBuf = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(usedBuf, usedEntries);
    image.Write(usedBuf);
  }

  /// <summary>
  /// Removes a named file from the T64 image by zeroing its directory entry type.
  /// Returns false if not found.
  /// </summary>
  public static bool RemoveFile(Stream image, string name) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    image.Position = 34;
    Span<byte> hdrBuf = stackalloc byte[4];
    image.ReadExactly(hdrBuf);
    var maxEntries = BinaryPrimitives.ReadUInt16LittleEndian(hdrBuf);
    var usedEntries = BinaryPrimitives.ReadUInt16LittleEndian(hdrBuf.Slice(2));

    for (var i = 0; i < maxEntries; i++) {
      var slotOff = HeaderSize + i * EntrySize;
      var entry = new byte[EntrySize];
      image.Position = slotOff;
      image.ReadExactly(entry);

      if (entry[0] == 0) continue; // free slot

      // Read filename (16 bytes at offset 16).
      var entryName = Encoding.ASCII.GetString(entry, 16, 16).TrimEnd('\0', ' ');
      if (!entryName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;

      // Mark slot as free.
      entry[0] = 0;
      image.Position = slotOff;
      image.Write(entry);

      // Decrement used count.
      if (usedEntries > 0) usedEntries--;
      image.Position = 36;
      Span<byte> usedBuf = stackalloc byte[2];
      BinaryPrimitives.WriteUInt16LittleEndian(usedBuf, usedEntries);
      image.Write(usedBuf);

      return true;
    }

    return false;
  }
}
