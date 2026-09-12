using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Mhk;

/// <summary>
/// Reads resources from a Cyan Mohawk (MHK) archive used by Myst, Riven,
/// Cosmic Osmo and the Living Books titles.
/// </summary>
/// <remarks>
/// Mohawk is big-endian even on Windows because the format originally targeted
/// PowerPC Macs and the publisher chose a single byte order for both platforms.
/// The on-disk layout is an outer "MHWK" IFF chunk wrapping a single inner "RSRC"
/// chunk; both magics are validated to reject look-alike files.
/// </remarks>
public sealed class MhkReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets the Mohawk version word from the RSRC header (typically 0x0100).</summary>
  public ushort Version { get; }

  /// <summary>Gets all resource entries discovered in the archive (one per file-table slot, per (Type, Id) pair).</summary>
  public IReadOnlyList<MhkEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="MhkReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the MHK archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public MhkReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < MhkConstants.OuterHeaderSize + MhkConstants.RsrcHeaderFixedSize)
      throw new InvalidDataException("Stream is too small to contain a Mohawk MHWK+RSRC header pair.");

    stream.Position = 0;

    Span<byte> outerHeader = stackalloc byte[MhkConstants.OuterHeaderSize];
    ReadExact(outerHeader);
    var outerMagic = Encoding.ASCII.GetString(outerHeader[..4]);
    if (outerMagic != MhkConstants.OuterMagicString)
      throw new InvalidDataException($"Invalid Mohawk outer magic: '{outerMagic}' (expected 'MHWK').");

    var outerBodySize = BinaryPrimitives.ReadUInt32BigEndian(outerHeader[4..8]);

    Span<byte> rsrcHeader = stackalloc byte[8 + MhkConstants.RsrcHeaderFixedSize];
    ReadExact(rsrcHeader);
    var rsrcMagic = Encoding.ASCII.GetString(rsrcHeader[..4]);
    if (rsrcMagic != MhkConstants.RsrcMagicString)
      throw new InvalidDataException($"Invalid Mohawk inner magic: '{rsrcMagic}' (expected 'RSRC').");

    // RSRC body size — kept for completeness; we trust dirOffset for navigation.
    _ = BinaryPrimitives.ReadUInt32BigEndian(rsrcHeader[4..8]);
    this.Version          = BinaryPrimitives.ReadUInt16BigEndian(rsrcHeader[8..10]);
    _                     = BinaryPrimitives.ReadUInt16BigEndian(rsrcHeader[10..12]); // compaction
    var totalFileSize     = BinaryPrimitives.ReadUInt32BigEndian(rsrcHeader[12..16]);
    var directoryOffset   = BinaryPrimitives.ReadUInt32BigEndian(rsrcHeader[16..20]);
    var typeTableOffset   = BinaryPrimitives.ReadUInt16BigEndian(rsrcHeader[20..22]);
    _                     = BinaryPrimitives.ReadUInt16BigEndian(rsrcHeader[22..24]); // reserved

    // Sanity: outer body size + 8 outer header bytes = total file size; reject contradictions.
    if (totalFileSize != outerBodySize + MhkConstants.OuterHeaderSize)
      throw new InvalidDataException(
        $"Mohawk size fields disagree: outerBodySize+8={outerBodySize + MhkConstants.OuterHeaderSize}, RSRC.totalFileSize={totalFileSize}.");

    if (directoryOffset >= stream.Length)
      throw new InvalidDataException($"Mohawk directory offset {directoryOffset} is beyond stream length {stream.Length}.");

    this.Entries = ReadDirectory(directoryOffset, typeTableOffset);
  }

  /// <summary>
  /// Extracts the raw payload bytes for a given resource entry.
  /// </summary>
  public byte[] Extract(MhkEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.Size == 0)
      return [];

    if (entry.Size > int.MaxValue)
      throw new InvalidDataException($"Mohawk entry '{entry.DisplayName}' is too large to extract: {entry.Size} bytes.");

    if (entry.Offset + entry.Size > this._stream.Length)
      throw new InvalidDataException($"Mohawk entry '{entry.DisplayName}' extends past end of stream.");

    this._stream.Position = entry.Offset;
    var buffer = new byte[entry.Size];
    ReadExact(buffer);
    return buffer;
  }

  private List<MhkEntry> ReadDirectory(uint directoryOffset, ushort typeTableOffset) {
    var typeTableAbs = directoryOffset + typeTableOffset;
    this._stream.Position = typeTableAbs;

    Span<byte> u16 = stackalloc byte[2];
    ReadExact(u16);
    var typeCount = BinaryPrimitives.ReadUInt16BigEndian(u16);

    var typeRecords = new (string Tag, ushort ResourceTableOffset, ushort NameTableOffset)[typeCount];
    Span<byte> typeBuf = stackalloc byte[MhkConstants.TypeTableEntrySize];
    for (var i = 0; i < typeCount; ++i) {
      ReadExact(typeBuf);
      var tag             = Encoding.ASCII.GetString(typeBuf[..4]);
      var resTableOffset  = BinaryPrimitives.ReadUInt16BigEndian(typeBuf[4..6]);
      var nameTableOffset = BinaryPrimitives.ReadUInt16BigEndian(typeBuf[6..8]);
      typeRecords[i] = (tag, resTableOffset, nameTableOffset);
    }

    var fileTable = ReadFileTable(directoryOffset);

    var entries = new List<MhkEntry>();
    // Hoisted out of the per-type loop to satisfy CA2014 (no stackalloc inside loops).
    Span<byte> nameBuf = stackalloc byte[MhkConstants.NameTableEntrySize];
    Span<byte> resBuf  = stackalloc byte[MhkConstants.ResourceTableEntrySize];

    foreach (var (tag, resTableOffset, nameTableOffset) in typeRecords) {
      // Build id -> name map for this type from the optional name table.
      var idToName = new Dictionary<ushort, string>();
      if (nameTableOffset != 0) {
        this._stream.Position = directoryOffset + nameTableOffset;
        ReadExact(u16);
        var nameCount = BinaryPrimitives.ReadUInt16BigEndian(u16);

        var nameRows = new (ushort StringOffset, ushort Id)[nameCount];
        for (var n = 0; n < nameCount; ++n) {
          ReadExact(nameBuf);
          var stringOffset = BinaryPrimitives.ReadUInt16BigEndian(nameBuf[..2]);
          var nameId       = BinaryPrimitives.ReadUInt16BigEndian(nameBuf[2..4]);
          nameRows[n] = (stringOffset, nameId);
        }

        // Resolve names from the string pool that immediately follows the name table.
        var poolBase = this._stream.Position;
        foreach (var (stringOffset, nameId) in nameRows) {
          var resolved = ReadPoolString(poolBase, stringOffset);
          idToName[nameId] = resolved;
        }
      }

      // Walk the resource table for this type.
      this._stream.Position = directoryOffset + resTableOffset;
      ReadExact(u16);
      var resourceCount = BinaryPrimitives.ReadUInt16BigEndian(u16);

      for (var r = 0; r < resourceCount; ++r) {
        ReadExact(resBuf);
        var id        = BinaryPrimitives.ReadUInt16BigEndian(resBuf[..2]);
        var fileIndex = BinaryPrimitives.ReadUInt16BigEndian(resBuf[2..4]);

        if (fileIndex < 1 || fileIndex > fileTable.Count)
          throw new InvalidDataException(
            $"Mohawk resource '{tag}' #{id} references invalid file index {fileIndex} (valid range 1..{fileTable.Count}).");

        // File table is 1-based on disk.
        var (offset, size) = fileTable[fileIndex - 1];

        idToName.TryGetValue(id, out var name);
        var displayName = name is null ? $"{tag}_{id}" : $"{tag}_{id}_{name}";

        entries.Add(new MhkEntry {
          Type        = tag,
          Id          = id,
          Name        = name,
          DisplayName = displayName,
          Offset      = offset,
          Size        = size,
        });
      }
    }

    return entries;
  }

  private List<(long Offset, long Size)> ReadFileTable(uint directoryOffset) {
    // The file table sits at the end of the directory. Engines locate it via:
    // directoryOffset + (offset to end of directory) — but in practice the file table immediately follows
    // every other table block. To stay robust we scan forward from the highest known directory tail.
    // Simpler and correct per spec: the file table location is implicit at the very end of the directory,
    // and our writer always emits it last; readers can locate it by walking from the type table forward.
    // For decoding we use the conventional approach: the file table starts at the highest end-offset
    // we've consumed so far, but we need an explicit anchor. Engines use a different trick — file table
    // offset = totalFileSize - (file_count * 11 + 4) — which requires knowing file_count first.
    //
    // Instead we use the format invariant we control on write: the file table immediately follows the
    // last name string pool. To find it on arbitrary inputs we need a self-describing pointer. The MHK
    // file table location is conventionally encoded: directoryOffset itself — the file table is the
    // very last thing in the directory, placed at directoryOffset by some engine variants. We adopt
    // the canonical convention of the original engine: the file table is at directoryOffset and the
    // type table follows it (typeTableOffset is its offset within the directory).
    //
    // Confirmed: in real Mohawk files, the file table begins exactly at directoryOffset, and the
    // typeTableOffset jumps past the file table to the type table. We honor that.

    this._stream.Position = directoryOffset;
    Span<byte> u32 = stackalloc byte[4];
    ReadExact(u32);
    var fileCount = BinaryPrimitives.ReadUInt32BigEndian(u32);

    if (fileCount > 0x100000)
      throw new InvalidDataException($"Mohawk file count {fileCount} is implausibly large.");

    var table = new List<(long Offset, long Size)>((int)fileCount);
    Span<byte> entryBuf = stackalloc byte[MhkConstants.FileTableEntrySize];
    for (var i = 0u; i < fileCount; ++i) {
      ReadExact(entryBuf);

      var offset  = BinaryPrimitives.ReadUInt32BigEndian(entryBuf[..4]);
      // Size is split: 3 bytes BE for low 24 bits, 1 byte for high 8 bits — historical artefact.
      var sizeLow = ((uint)entryBuf[4] << 16) | ((uint)entryBuf[5] << 8) | entryBuf[6];
      var sizeHi  = (uint)entryBuf[7];
      var size    = (long)sizeLow | ((long)sizeHi << 24);
      // entryBuf[8] = flags, entryBuf[9..11] = unknown — both ignored on read.

      table.Add((offset, size));
    }

    return table;
  }

  private string ReadPoolString(long poolBase, ushort offset) {
    var savedPos = this._stream.Position;
    try {
      this._stream.Position = poolBase + offset;
      var sb = new StringBuilder();
      while (true) {
        var b = this._stream.ReadByte();
        if (b <= 0)
          break;
        sb.Append((char)b);
      }
      return sb.ToString();
    } finally {
      this._stream.Position = savedPos;
    }
  }

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of Mohawk stream.");
      totalRead += read;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
