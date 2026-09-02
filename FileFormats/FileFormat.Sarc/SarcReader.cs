using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Sarc;

/// <summary>
/// Reads a Nintendo SARC archive (Wii U / 3DS / Switch). Endianness is detected
/// via the BOM at offset 6: 0xFEFF = little-endian (Switch), 0xFFFE = big-endian
/// (Wii U / 3DS).
/// </summary>
public sealed class SarcReader : IDisposable {

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets whether the archive was stored little-endian.</summary>
  public bool IsLittleEndian { get; }

  /// <summary>Gets the hash multiplier (HashKey) declared in the SFAT header.</summary>
  public uint HashKey { get; }

  /// <summary>Gets the data-region start offset declared in the SARC header.</summary>
  public long DataOffset { get; }

  /// <summary>Gets all entries discovered in the archive, in SFAT order (sorted by NameHash).</summary>
  public IReadOnlyList<SarcEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="SarcReader"/> from a stream.
  /// </summary>
  public SarcReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < SarcConstants.SarcHeaderBytes + SarcConstants.SfatHeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid SARC archive.");

    Span<byte> sarcHdr = stackalloc byte[SarcConstants.SarcHeaderBytes];
    ReadExact(sarcHdr);

    if (Encoding.ASCII.GetString(sarcHdr[..4]) != SarcConstants.MagicSarc)
      throw new InvalidDataException("Invalid SARC magic.");

    // BOM is laid out as the literal bytes FE FF (LE) or FF FE (BE) regardless of the
    // archive's byte order, so we read it as raw bytes rather than via an endian-tagged read.
    var bomByte0 = sarcHdr[6];
    var bomByte1 = sarcHdr[7];
    this.IsLittleEndian = (bomByte0, bomByte1) switch {
      (0xFF, 0xFE) => true,
      (0xFE, 0xFF) => false,
      _ => throw new InvalidDataException($"Invalid SARC byte-order mark: 0x{bomByte0:X2}{bomByte1:X2}.")
    };

    var headerSize = ReadU16(sarcHdr[4..6]);
    if (headerSize == 0)
      throw new InvalidDataException("SARC header size is zero.");

    var fileSize = ReadU32(sarcHdr[8..12]);
    this.DataOffset = ReadU32(sarcHdr[12..16]);
    // sarcHdr[16..18] = Version, sarcHdr[18..20] = Reserved — both ignored.

    if (fileSize > (uint)stream.Length)
      throw new InvalidDataException($"SARC declares file size {fileSize} but stream is {stream.Length} bytes.");

    // SFAT header
    Span<byte> sfatHdr = stackalloc byte[SarcConstants.SfatHeaderSize];
    ReadExact(sfatHdr);
    if (Encoding.ASCII.GetString(sfatHdr[..4]) != SarcConstants.MagicSfat)
      throw new InvalidDataException("Invalid SFAT magic.");

    var nodeCount = ReadU16(sfatHdr[6..8]);
    this.HashKey = ReadU32(sfatHdr[8..12]);

    // Read all SFAT entries first, then SFNT header + name table, then resolve names.
    // The string table is variable length so we have to consume the entries before we
    // know where SFNT starts.
    Span<byte> entryBuf = stackalloc byte[SarcConstants.SfatEntrySize];
    var rawEntries = new (uint Hash, uint Attr, uint Begin, uint End)[nodeCount];
    for (var i = 0; i < nodeCount; ++i) {
      ReadExact(entryBuf);
      rawEntries[i] = (
        ReadU32(entryBuf[0..4]),
        ReadU32(entryBuf[4..8]),
        ReadU32(entryBuf[8..12]),
        ReadU32(entryBuf[12..16])
      );
    }

    // SFNT header
    Span<byte> sfntHdr = stackalloc byte[SarcConstants.SfntHeaderSize];
    ReadExact(sfntHdr);
    if (Encoding.ASCII.GetString(sfntHdr[..4]) != SarcConstants.MagicSfnt)
      throw new InvalidDataException("Invalid SFNT magic.");

    // String table runs from current position up to DataOffset
    var stringTableStart = stream.Position;
    var stringTableLen = checked((int)(this.DataOffset - stringTableStart));
    if (stringTableLen < 0)
      throw new InvalidDataException("DataOffset precedes SFNT string table.");

    var stringTable = new byte[stringTableLen];
    if (stringTableLen > 0)
      ReadExact(stringTable);

    var entries = new List<SarcEntry>(nodeCount);
    foreach (var (hash, attr, begin, end) in rawEntries) {
      string name = "";
      // Low 24 bits of attr is name_offset / 4 (units of 4 bytes); high 8 bits is the
      // attribute flag (typically 0x01 indicating "name present in SFNT").
      var hasName = (attr & 0xFF000000) != 0;
      if (hasName && stringTableLen > 0) {
        var nameByteOffset = (int)((attr & 0x00FFFFFFu) * 4u);
        if (nameByteOffset < stringTableLen)
          name = ReadCString(stringTable, nameByteOffset);
      }

      if (end < begin)
        throw new InvalidDataException($"SARC entry has end ({end}) before begin ({begin}).");

      entries.Add(new SarcEntry {
        Name = name,
        NameHash = hash,
        Offset = this.DataOffset + begin,
        Size = end - begin,
      });
    }

    this.Entries = entries;
  }

  /// <summary>
  /// Extracts the raw payload bytes for the given entry.
  /// </summary>
  public byte[] Extract(SarcEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size == 0)
      return [];

    this._stream.Position = entry.Offset;
    var data = new byte[entry.Size];
    ReadExact(data);
    return data;
  }

  private ushort ReadU16(ReadOnlySpan<byte> span)
    => this.IsLittleEndian
      ? BinaryPrimitives.ReadUInt16LittleEndian(span)
      : BinaryPrimitives.ReadUInt16BigEndian(span);

  private uint ReadU32(ReadOnlySpan<byte> span)
    => this.IsLittleEndian
      ? BinaryPrimitives.ReadUInt32LittleEndian(span)
      : BinaryPrimitives.ReadUInt32BigEndian(span);

  private static string ReadCString(byte[] buffer, int offset) {
    var end = offset;
    while (end < buffer.Length && buffer[end] != 0)
      ++end;
    return Encoding.UTF8.GetString(buffer, offset, end - offset);
  }

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of SARC stream.");
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
