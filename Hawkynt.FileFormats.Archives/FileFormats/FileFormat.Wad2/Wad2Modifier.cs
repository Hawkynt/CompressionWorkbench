#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Wad2;

/// <summary>
/// In-place WAD2/WAD3 archive modifier. The Quake/Half-Life WAD container
/// places the 32-byte directory at the END of the file (after entry data),
/// with a pointer in the 12-byte header at offset 8. That makes Add a
/// localised mutation:
/// <list type="number">
///   <item>Truncate the file at the old <c>dirOffset</c> (drop the trailing directory).</item>
///   <item>Append the new entry's bytes at the new EOF.</item>
///   <item>Append the rebuilt directory (old entries + new entry) at the new EOF.</item>
///   <item>Patch the 12-byte header so <c>numEntries</c> and <c>dirOffset</c> reflect the new layout.</item>
/// </list>
///
/// <para><b>Byte-identity contract:</b> the magic at <c>[0, 4)</c> and the
/// data region <c>[12, oldDirOffset)</c> are byte-identical after Add (no
/// pre-existing entry's bytes move). Only the header's
/// <c>numEntries</c>/<c>dirOffset</c> fields and the directory itself are
/// re-emitted.</para>
/// </summary>
public static class Wad2Modifier {

  private const int HeaderSize = 12;
  private const int DirectoryEntrySize = 32;
  private const int MaxNameLength = 16;

  // Default entry type byte for Add (texture).
  private const byte DefaultEntryType = 0x43;

  /// <summary>
  /// Appends an entry to a WAD2/WAD3 archive in place. Preserves bytes
  /// <c>[0, 4)</c> and <c>[12, oldDirOffset)</c> byte-identical.
  /// </summary>
  public static void AddEntry(Stream archive, string name, byte[] data, byte entryType = DefaultEntryType) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var (numEntries, dirOffset, magicSurvives) = ReadHeader(archive);
    if (!magicSurvives)
      throw new InvalidDataException("Stream is not a WAD2/WAD3 archive (bad magic).");

    // Snapshot existing directory.
    var existingDir = ReadDirectory(archive, dirOffset, numEntries);

    // Truncate at old dirOffset so the trailing directory is dropped — every
    // pre-existing entry's data stays at its current offset.
    archive.SetLength(dirOffset);

    // Append new entry data at the new EOF and record its offset.
    archive.Position = archive.Length;
    var newEntryOffset = (uint)archive.Position;
    archive.Write(data);

    // Append the rebuilt directory at the new EOF.
    var newDirOffset = (uint)archive.Position;
    WriteDirectoryEntries(archive, existingDir);
    WriteDirectoryEntry(archive, newEntryOffset, (uint)data.Length, entryType, name);

    // Patch the header's numEntries and dirOffset fields.
    PatchHeader(archive, numEntries: numEntries + 1, dirOffset: newDirOffset);
  }

  /// <summary>
  /// Removes the named entry from a WAD2/WAD3 archive. Walks the directory
  /// to find the entry, then rebuilds the data region with that entry
  /// dropped (its bytes are removed and trailing data shifted forward),
  /// re-emits the directory with updated offsets, and patches the header.
  /// Returns false if no entry by that name exists.
  /// </summary>
  public static bool RemoveEntry(Stream archive, string name) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);

    var (numEntries, dirOffset, magicSurvives) = ReadHeader(archive);
    if (!magicSurvives)
      throw new InvalidDataException("Stream is not a WAD2/WAD3 archive (bad magic).");

    var entries = ReadDirectory(archive, dirOffset, numEntries);
    var idx = -1;
    for (var i = 0; i < entries.Count; i++) {
      if (entries[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) {
        idx = i;
        break;
      }
    }
    if (idx < 0) return false;

    // Read every surviving entry's data bytes into memory (WAD archives are
    // small — vanilla Quake's textures.wad is ≈4 MB). Drop the matched entry.
    var liveEntries = new List<(string Name, byte[] Data, byte Type)>(entries.Count - 1);
    for (var i = 0; i < entries.Count; i++) {
      if (i == idx) continue;
      archive.Position = entries[i].DataOffset;
      var buf = new byte[entries[i].DiskSize];
      archive.ReadExactly(buf);
      liveEntries.Add((entries[i].Name, buf, entries[i].Type));
    }

    // Rebuild: header bytes [0..4) survive byte-identical; data + directory
    // are re-emitted from offset HeaderSize.
    archive.Position = HeaderSize;
    var dataOffsets = new uint[liveEntries.Count];
    for (var i = 0; i < liveEntries.Count; i++) {
      dataOffsets[i] = (uint)archive.Position;
      archive.Write(liveEntries[i].Data);
    }
    var newDirOffset = (uint)archive.Position;
    for (var i = 0; i < liveEntries.Count; i++)
      WriteDirectoryEntry(archive, dataOffsets[i], (uint)liveEntries[i].Data.Length,
        liveEntries[i].Type, liveEntries[i].Name);

    archive.SetLength(archive.Position);
    PatchHeader(archive, numEntries: liveEntries.Count, dirOffset: newDirOffset);
    return true;
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private record struct DirEntry(string Name, uint DataOffset, uint DiskSize, uint Size, byte Type, byte Compression);

  private static (int NumEntries, uint DirOffset, bool MagicSurvives) ReadHeader(Stream archive) {
    if (archive.Length < HeaderSize) return (0, 0, false);
    archive.Position = 0;
    Span<byte> hdr = stackalloc byte[HeaderSize];
    archive.ReadExactly(hdr);
    var magic = Encoding.ASCII.GetString(hdr[..4]);
    if (magic != "WAD2" && magic != "WAD3") return (0, 0, false);
    var num = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..8]);
    var off = BinaryPrimitives.ReadUInt32LittleEndian(hdr[8..12]);
    return ((int)num, off, true);
  }

  private static List<DirEntry> ReadDirectory(Stream archive, uint dirOffset, int numEntries) {
    var result = new List<DirEntry>(numEntries);
    archive.Position = dirOffset;
    Span<byte> buf = stackalloc byte[DirectoryEntrySize];
    for (var i = 0; i < numEntries; i++) {
      archive.ReadExactly(buf);
      var dataOff = BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]);
      var disk = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..8]);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(buf[8..12]);
      var type = buf[12];
      var comp = buf[13];
      var nameSpan = buf[16..32];
      var nullIdx = nameSpan.IndexOf((byte)0);
      var nameLen = nullIdx < 0 ? nameSpan.Length : nullIdx;
      var name = Encoding.ASCII.GetString(nameSpan[..nameLen]);
      result.Add(new DirEntry(name, dataOff, disk, size, type, comp));
    }
    return result;
  }

  private static void WriteDirectoryEntries(Stream archive, IReadOnlyList<DirEntry> entries) {
    foreach (var e in entries)
      WriteDirectoryEntry(archive, e.DataOffset, e.DiskSize, e.Type, e.Name);
  }

  private static void WriteDirectoryEntry(Stream archive, uint dataOffset, uint dataLength, byte type, string name) {
    Span<byte> buf = stackalloc byte[DirectoryEntrySize];
    buf.Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(buf[0..4], dataOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(buf[4..8], dataLength); // diskSize
    BinaryPrimitives.WriteUInt32LittleEndian(buf[8..12], dataLength); // uncompressed size
    buf[12] = type;
    buf[13] = 0; // compression = none
    // buf[14..16] padding stays zero
    var truncated = name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    var nameBytes = Encoding.ASCII.GetBytes(truncated);
    nameBytes.AsSpan().CopyTo(buf[16..32]);
    archive.Write(buf);
  }

  private static void PatchHeader(Stream archive, int numEntries, uint dirOffset) {
    Span<byte> buf = stackalloc byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(buf[0..4], (uint)numEntries);
    BinaryPrimitives.WriteUInt32LittleEndian(buf[4..8], dirOffset);
    archive.Position = 4;
    archive.Write(buf);
    archive.Position = archive.Length;
  }
}
