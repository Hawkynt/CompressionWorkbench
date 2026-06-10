#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Pfs0;

/// <summary>
/// In-place modifier for Nintendo Switch PartitionFS (PFS0) archives.
/// PFS0 has a flat layout — header + entry table + string table + data
/// region — that lends itself to shift-in-place mutation.
///
/// <para>
/// On <see cref="AddFiles"/> the existing header / entry table / string table /
/// data region are read into RAM, the new file is appended, the in-memory
/// layout is rewritten with the new alphabetically-sorted entry table +
/// string table + data region, and finally the buffer is written back to
/// the underlying stream. The stream's length is set to the new length.
/// </para>
/// <para>
/// On <see cref="RemoveFiles"/> the chosen entries are dropped from the entry
/// table, the string table is rebuilt without their names, and the data
/// region is re-laid-out so the remaining payloads stay contiguous.
/// Removed payload bytes never appear in the new buffer — no forensic
/// trace of the deleted entry remains.
/// </para>
/// <para>
/// Layout (little-endian):
///   0x00 char[4] "PFS0"
///   0x04 u32     file_count
///   0x08 u32     string_table_size
///   0x0C u32     reserved
///   0x10..       file_count × 24-byte entries (data_offset, data_size, name_offset, reserved)
///   then         string_table_size bytes of NUL-terminated UTF-8 names
///   then         the data region (concatenated payloads).
/// </para>
/// </summary>
public static class Pfs0InPlaceModifier {

  private const int HeaderSize = 16;
  private const int EntrySize = 24;
  private static readonly byte[] Magic = "PFS0"u8.ToArray();

  /// <summary>
  /// Adds — or replaces by name — files in an existing PFS0 archive. The
  /// archive is rewritten in place at the underlying stream.
  /// </summary>
  public static void AddFiles(Stream archive, IReadOnlyList<(string Name, byte[] Data)> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    var entries = ReadAll(archive);
    foreach (var (name, data) in inputs) {
      ArgumentNullException.ThrowIfNull(name);
      ArgumentNullException.ThrowIfNull(data);
      // Replace-by-name semantics: drop any existing entry with the same name first.
      entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.Ordinal));
      entries.Add((name, data));
    }
    WriteAll(archive, entries);
  }

  /// <summary>
  /// Removes the named entries from an existing PFS0 archive. Names that
  /// don't exist are silently ignored. Returns the number of entries
  /// actually removed.
  /// </summary>
  public static int RemoveFiles(Stream archive, IReadOnlyList<string> names) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(names);

    var entries = ReadAll(archive);
    var before = entries.Count;
    foreach (var name in names) {
      ArgumentNullException.ThrowIfNull(name);
      entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.Ordinal));
    }
    var removed = before - entries.Count;
    if (removed > 0)
      WriteAll(archive, entries);
    return removed;
  }

  // ── Internals ───────────────────────────────────────────────────────────

  private static List<(string Name, byte[] Data)> ReadAll(Stream archive) {
    archive.Position = 0;
    if (archive.Length < HeaderSize)
      throw new InvalidDataException("PFS0: stream is too small to contain the header.");

    Span<byte> header = stackalloc byte[HeaderSize];
    ReadExact(archive, header);
    if (!header[..4].SequenceEqual(Magic))
      throw new InvalidDataException("PFS0: missing magic.");

    var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
    var stringTableSize = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
    if (fileCount > int.MaxValue)
      throw new InvalidDataException("PFS0: implausible file count.");

    var count = (int)fileCount;
    var entriesSize = (long)count * EntrySize;
    var dataRegionStart = HeaderSize + entriesSize + stringTableSize;
    if (dataRegionStart > archive.Length)
      throw new InvalidDataException("PFS0: header declares a region beyond the stream.");

    var rawEntries = new (ulong DataOffset, ulong DataSize, uint NameOffset)[count];
    Span<byte> entryBuf = stackalloc byte[EntrySize];
    for (var i = 0; i < count; ++i) {
      ReadExact(archive, entryBuf);
      rawEntries[i] = (
        BinaryPrimitives.ReadUInt64LittleEndian(entryBuf[0..8]),
        BinaryPrimitives.ReadUInt64LittleEndian(entryBuf[8..16]),
        BinaryPrimitives.ReadUInt32LittleEndian(entryBuf[16..20])
      );
    }

    var stringTable = new byte[stringTableSize];
    if (stringTableSize > 0)
      ReadExact(archive, stringTable);

    var entries = new List<(string Name, byte[] Data)>(count);
    for (var i = 0; i < count; ++i) {
      var (relOffset, size, nameOffset) = rawEntries[i];
      if (nameOffset > stringTable.Length)
        throw new InvalidDataException($"PFS0: entry #{i} name offset out of range.");
      var name = ReadCStringUtf8(stringTable, (int)nameOffset);
      var absoluteOffset = dataRegionStart + (long)relOffset;
      if (absoluteOffset < 0 || absoluteOffset + (long)size > archive.Length)
        throw new InvalidDataException($"PFS0: entry '{name}' data range exceeds stream length.");
      if (size > int.MaxValue)
        throw new InvalidDataException($"PFS0: entry '{name}' too large for in-memory modify.");
      var data = new byte[(int)size];
      archive.Position = absoluteOffset;
      ReadExact(archive, data);
      entries.Add((name, data));
    }
    return entries;
  }

  private static void WriteAll(Stream archive, List<(string Name, byte[] Data)> entries) {
    // Switch SDK convention: sort entries alphabetically by name.
    entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

    var encodedNames = new byte[entries.Count][];
    var nameOffsets = new uint[entries.Count];
    var stringTableSize = 0u;
    for (var i = 0; i < entries.Count; ++i) {
      encodedNames[i] = Encoding.UTF8.GetBytes(entries[i].Name);
      nameOffsets[i] = stringTableSize;
      stringTableSize += (uint)encodedNames[i].Length + 1;
    }

    archive.Position = 0;
    Span<byte> header = stackalloc byte[HeaderSize];
    Encoding.ASCII.GetBytes("PFS0", header[..4]);
    BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], (uint)entries.Count);
    BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], stringTableSize);
    BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], 0);
    archive.Write(header);

    Span<byte> entryBuf = stackalloc byte[EntrySize];
    var relOffset = 0UL;
    for (var i = 0; i < entries.Count; ++i) {
      var data = entries[i].Data;
      BinaryPrimitives.WriteUInt64LittleEndian(entryBuf[0..8], relOffset);
      BinaryPrimitives.WriteUInt64LittleEndian(entryBuf[8..16], (ulong)data.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[16..20], nameOffsets[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[20..24], 0);
      archive.Write(entryBuf);
      relOffset += (ulong)data.Length;
    }

    for (var i = 0; i < entries.Count; ++i) {
      archive.Write(encodedNames[i]);
      archive.WriteByte(0);
    }

    for (var i = 0; i < entries.Count; ++i) {
      var data = entries[i].Data;
      if (data.Length > 0)
        archive.Write(data);
    }

    archive.SetLength(archive.Position);
  }

  private static string ReadCStringUtf8(ReadOnlySpan<byte> table, int offset) {
    var slice = table[offset..];
    var nul = slice.IndexOf((byte)0);
    if (nul < 0) nul = slice.Length;
    return Encoding.UTF8.GetString(slice[..nul]);
  }

  private static void ReadExact(Stream s, Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = s.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("PFS0: unexpected end of stream.");
      totalRead += read;
    }
  }
}
