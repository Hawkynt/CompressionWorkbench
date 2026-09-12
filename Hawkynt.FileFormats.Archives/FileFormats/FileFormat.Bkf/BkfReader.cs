#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Bkf;

/// <summary>
/// Reads Microsoft NTBackup (<c>.bkf</c>) files written in the Microsoft Tape
/// Format (MTF) v1.0 — the public spec used by <c>ntbackup.exe</c> on Windows
/// 95 through Windows Server 2003. Read-only: lists and extracts FILE entries
/// with their <c>STAN</c> (Standard) data streams.
/// </summary>
/// <remarks>
/// <para>
/// MTF structure (simplified):
/// </para>
/// <code>
///   TAPE ─ SSET ─ VOLB ─ DIRB ─ FILE ─ DATA … ─ ESET ─ … ─ EOTM
/// </code>
/// <para>
/// Each Descriptor Block (DBLK) begins with a 52-byte Common Block Header
/// (CBH) followed by type-specific fixed fields and an OSDATA section that
/// holds strings (FNAM/PNAM). After the DBLK come zero or more data streams
/// (e.g. <c>STAN</c> Standard data, <c>PNAM</c> path name, <c>FNAM</c> file
/// name, <c>SPAD</c> padding). Every DBLK starts on a Format Logical Block
/// (FLB, typically 1024 bytes) boundary.
/// </para>
/// <para>
/// This reader walks DBLKs by jumping to the next FLB boundary after the
/// current block's data streams. Compression streams are not decoded — the
/// MTF spec does not name a compression algorithm and ntbackup.exe wrote
/// uncompressed data in practice. Compressed payloads are surfaced but
/// reported as such; the reader falls back to "stored" for uncompressed
/// <c>STAN</c> streams.
/// </para>
/// </remarks>
public sealed class BkfReader : IDisposable {

  /// <summary>4-char DBLK type identifiers as little-endian uint32 for fast comparison.</summary>
  public static readonly uint TapeType = AsUInt32("TAPE");
  /// <summary>
  /// Provides the sset type value.
  /// </summary>
  public static readonly uint SsetType = AsUInt32("SSET");
  /// <summary>
  /// Provides the volb type value.
  /// </summary>
  public static readonly uint VolbType = AsUInt32("VOLB");
  /// <summary>
  /// Provides the dirb type value.
  /// </summary>
  public static readonly uint DirbType = AsUInt32("DIRB");
  /// <summary>
  /// Provides the file type value.
  /// </summary>
  public static readonly uint FileType = AsUInt32("FILE");
  /// <summary>
  /// Provides the eset type value.
  /// </summary>
  public static readonly uint EsetType = AsUInt32("ESET");
  /// <summary>
  /// Provides the eotm type value.
  /// </summary>
  public static readonly uint EotmType = AsUInt32("EOTM");
  /// <summary>
  /// Provides the espb type value.
  /// </summary>
  public static readonly uint EspbType = AsUInt32("ESPB");
  /// <summary>
  /// Provides the sfmb type value.
  /// </summary>
  public static readonly uint SfmbType = AsUInt32("SFMB");

  /// <summary>Stream IDs.</summary>
  private static readonly uint StanStreamId = AsUInt32("STAN"); // Standard file data
  private static readonly uint PnamStreamId = AsUInt32("PNAM"); // Path name
  private static readonly uint FnamStreamId = AsUInt32("FNAM"); // File name
  private static readonly uint SpadStreamId = AsUInt32("SPAD"); // Padding
  private static readonly uint CsumStreamId = AsUInt32("CSUM"); // Checksum
  private static readonly uint TsmpStreamId = AsUInt32("TSMP"); // Tape sparse map
  private static readonly uint MqciStreamId = AsUInt32("MQCI"); // Media quality control info

  private const int CommonBlockHeaderSize = 52;
  private const int DefaultLogicalBlockSize = 1024;
  private const int StreamHeaderSize = 22;

  private readonly byte[] _data;
  private readonly List<BkfEntry> _entries = [];
  private int _logicalBlockSize = DefaultLogicalBlockSize;
  private string _currentDirectoryPath = "";

  /// <summary>All FILE and DIRB entries surfaced from the .bkf stream.</summary>
  public IReadOnlyList<BkfEntry> Entries => _entries;

  /// <summary>Format Logical Block size detected from the TAPE DBLK.</summary>
  public int LogicalBlockSize => _logicalBlockSize;

  /// <summary>Constructs a reader and parses the entire stream into <see cref="Entries"/>.</summary>
  public BkfReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    this.Parse();
  }

  /// <summary>Returns the raw STAN data for an entry. Empty for directories.</summary>
  public byte[] Extract(BkfEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.DataLength <= 0) return [];
    if (entry.DataOffset < 0 || entry.DataOffset + entry.DataLength > _data.Length) return [];
    var result = new byte[entry.DataLength];
    Array.Copy(_data, entry.DataOffset, result, 0, (int)entry.DataLength);
    return result;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { /* no unmanaged resources */ }

  // ── parsing ─────────────────────────────────────────────────────────────

  private void Parse() {
    if (_data.Length < CommonBlockHeaderSize) throw new InvalidDataException("BKF: file too small for MTF Common Block Header.");

    var firstType = ReadUInt32LE(_data, 0);
    if (firstType != TapeType) throw new InvalidDataException("BKF: missing TAPE DBLK at offset 0.");

    // Read TAPE DBLK to discover the Format Logical Block Size.
    // In MTF the FLB size is stored in the TAPE DBLK at offset 52 (4 bytes).
    if (_data.Length >= 52 + 4) {
      var flb = (int)ReadUInt32LE(_data, 52);
      if (flb is > 0 and <= 65536 && (flb & (flb - 1)) == 0) _logicalBlockSize = flb;
    }

    var pos = 0;
    var sanity = 0;
    while (pos + CommonBlockHeaderSize <= _data.Length) {
      ++sanity;
      if (sanity > 1_000_000) break; // hard cap against malformed inputs

      var blockType = ReadUInt32LE(_data, pos);

      if (blockType == EotmType) break;

      if (blockType == TapeType || blockType == SsetType || blockType == VolbType ||
          blockType == EsetType || blockType == EspbType || blockType == SfmbType) {
        // Containers — advance past their data streams to next FLB boundary.
        // Reset directory tracking when we see a fresh set boundary.
        if (blockType == SsetType || blockType == VolbType) _currentDirectoryPath = "";

        var next = this.AdvancePastDblk(pos, blockType);
        if (next <= pos) break;
        pos = next;
        continue;
      }

      if (blockType == DirbType) {
        var dirName = this.ReadDirectoryName(pos);
        if (!string.IsNullOrEmpty(dirName))
          _currentDirectoryPath = NormalizePath(dirName);

        _entries.Add(new BkfEntry {
          Name = string.IsNullOrEmpty(_currentDirectoryPath) ? "(root)/" : _currentDirectoryPath + "/",
          Size = 0,
          IsDirectory = true,
          DataOffset = -1,
          DataLength = 0,
          IsCompressed = false,
        });

        var next = this.AdvancePastDblk(pos, blockType);
        if (next <= pos) break;
        pos = next;
        continue;
      }

      if (blockType == FileType) {
        var fileName = this.ReadFileName(pos);
        var (dataOffset, dataLength, isCompressed) = this.FindStanStream(pos);
        var combined = string.IsNullOrEmpty(_currentDirectoryPath)
          ? fileName
          : _currentDirectoryPath + "/" + fileName;
        _entries.Add(new BkfEntry {
          Name = combined,
          Size = dataLength,
          IsDirectory = false,
          DataOffset = dataOffset,
          DataLength = dataLength,
          IsCompressed = isCompressed,
        });

        var next = this.AdvancePastDblk(pos, blockType);
        if (next <= pos) break;
        pos = next;
        continue;
      }

      // Unknown DBLK — skip to next logical block boundary.
      pos = this.NextLogicalBlockBoundary(pos + 1);
    }
  }

  /// <summary>
  /// Walks past all data streams attached to the DBLK at <paramref name="dblkPos"/>
  /// and returns the file offset of the next DBLK (rounded up to the next FLB
  /// boundary). Falls back to a single-FLB advance if stream parsing fails.
  /// </summary>
  private int AdvancePastDblk(int dblkPos, uint blockType) {
    // Container blocks like ESET/EOTM may not have data streams — advance one FLB.
    if (blockType is var t && (t == EotmType)) return _data.Length;

    // Data streams start after the DBLK's type-specific fields and OSDATA.
    // The "Offset to first event" in the CBH gives a tape offset, not a file
    // offset — unreliable across formats. Use the safer approach: find the
    // first stream header (4-char known stream ID) after the CBH end, then
    // walk streams until we hit SPAD or run off the end. After streams, round
    // up to the next FLB boundary.
    var streamStart = this.FindFirstStreamHeader(dblkPos);
    if (streamStart < 0) return dblkPos + _logicalBlockSize;

    var cursor = streamStart;
    while (cursor + StreamHeaderSize <= _data.Length) {
      var streamId = ReadUInt32LE(_data, cursor);
      if (!IsKnownStreamId(streamId)) break;
      var streamLength = (long)ReadUInt64LE(_data, cursor + 8);
      if (streamLength < 0) break;
      var payloadEnd = (long)cursor + StreamHeaderSize + streamLength;
      // Round payload end up to 4-byte boundary (MTF stream alignment).
      payloadEnd = (payloadEnd + 3) & ~3L;
      if (payloadEnd > _data.Length) {
        cursor = _data.Length;
        break;
      }
      cursor = (int)payloadEnd;
      if (streamId == SpadStreamId) break;
    }
    return this.NextLogicalBlockBoundary(cursor);
  }

  /// <summary>
  /// Locates the first stream header following the DBLK header by scanning
  /// 4-byte-aligned positions for a known stream ID. Returns -1 if none found
  /// inside the search window.
  /// </summary>
  private int FindFirstStreamHeader(int dblkPos) {
    var minStart = dblkPos + CommonBlockHeaderSize;
    var maxStart = Math.Min(_data.Length - StreamHeaderSize, dblkPos + _logicalBlockSize);
    for (var scan = (minStart + 3) & ~3; scan <= maxStart; scan += 4) {
      if (IsKnownStreamId(ReadUInt32LE(_data, scan))) return scan;
    }
    return -1;
  }

  /// <summary>
  /// Finds the first <c>STAN</c> data stream attached to the FILE DBLK at
  /// <paramref name="filePos"/>. Returns (offset, length, isCompressed). All
  /// zero/false when not found.
  /// </summary>
  private (long Offset, long Length, bool IsCompressed) FindStanStream(int filePos) {
    var streamStart = this.FindFirstStreamHeader(filePos);
    if (streamStart < 0) return (0, 0, false);

    var cursor = streamStart;
    while (cursor + StreamHeaderSize <= _data.Length) {
      var streamId = ReadUInt32LE(_data, cursor);
      if (!IsKnownStreamId(streamId)) break;
      var streamLength = (long)ReadUInt64LE(_data, cursor + 8);
      if (streamLength < 0) break;
      var compressionAlgo = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(cursor + 18));
      var dataStart = cursor + StreamHeaderSize;
      var payloadEnd = (long)dataStart + streamLength;
      payloadEnd = (payloadEnd + 3) & ~3L;
      if (payloadEnd > _data.Length) break;

      if (streamId == StanStreamId)
        return (dataStart, streamLength, compressionAlgo != 0);

      cursor = (int)payloadEnd;
      if (streamId == SpadStreamId) break;
    }
    return (0, 0, false);
  }

  /// <summary>
  /// Reads the FILE name. Prefers the FNAM data stream when present; otherwise
  /// falls back to the file-name pointer in the FILE DBLK (Tape Address at
  /// offset 104, 4-byte size + 4-byte offset relative to the DBLK start).
  /// </summary>
  private string ReadFileName(int filePos) {
    var stringType = this.ReadStringType(filePos);
    var fromStream = this.ReadStreamString(filePos, FnamStreamId, stringType);
    if (!string.IsNullOrEmpty(fromStream)) return SanitizeFileName(fromStream);

    var fromTapeAddr = this.ReadTapeAddressString(filePos, addrFieldOffset: 104, stringType);
    return SanitizeFileName(string.IsNullOrEmpty(fromTapeAddr) ? $"file_{_entries.Count:D6}" : fromTapeAddr);
  }

  /// <summary>
  /// Reads the DIRB path name. Prefers the PNAM data stream when present;
  /// otherwise falls back to the directory-name Tape Address at DIRB offset
  /// 80 (per the MTF DIRB DBLK layout).
  /// </summary>
  private string ReadDirectoryName(int dirbPos) {
    var stringType = this.ReadStringType(dirbPos);
    var fromStream = this.ReadStreamString(dirbPos, PnamStreamId, stringType);
    if (!string.IsNullOrEmpty(fromStream)) return fromStream;
    var fromTapeAddr = this.ReadTapeAddressString(dirbPos, addrFieldOffset: 80, stringType);
    return fromTapeAddr;
  }

  /// <summary>Reads the CBH string type field at offset 46.</summary>
  private ushort ReadStringType(int dblkPos)
    => (dblkPos + 48 <= _data.Length)
       ? BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dblkPos + 46))
       : (ushort)1;

  /// <summary>
  /// Scans the DBLK's attached data streams for one matching <paramref name="streamId"/>
  /// and decodes the payload as a string per the given <paramref name="stringType"/>.
  /// </summary>
  private string ReadStreamString(int dblkPos, uint streamId, ushort stringType) {
    var streamStart = this.FindFirstStreamHeader(dblkPos);
    if (streamStart < 0) return "";

    var cursor = streamStart;
    while (cursor + StreamHeaderSize <= _data.Length) {
      var id = ReadUInt32LE(_data, cursor);
      if (!IsKnownStreamId(id)) break;
      var streamLength = (long)ReadUInt64LE(_data, cursor + 8);
      if (streamLength < 0) break;
      var dataStart = cursor + StreamHeaderSize;
      var payloadEnd = (long)dataStart + streamLength;
      payloadEnd = (payloadEnd + 3) & ~3L;
      if (payloadEnd > _data.Length) break;

      if (id == streamId && streamLength > 0 && dataStart + streamLength <= _data.Length)
        return DecodeString(_data.AsSpan(dataStart, (int)streamLength), stringType);

      cursor = (int)payloadEnd;
      if (id == SpadStreamId) break;
    }
    return "";
  }

  /// <summary>
  /// Reads an MTF Tape Address — a 4-byte size + 2-byte offset pair at
  /// <paramref name="addrFieldOffset"/> within the DBLK — and returns the
  /// referenced string. Offset is relative to the DBLK start.
  /// </summary>
  private string ReadTapeAddressString(int dblkPos, int addrFieldOffset, ushort stringType) {
    if (dblkPos + addrFieldOffset + 6 > _data.Length) return "";
    var size = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dblkPos + addrFieldOffset));
    var offset = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dblkPos + addrFieldOffset + 2));
    if (size == 0) return "";
    var start = dblkPos + offset;
    if (start < 0 || start + size > _data.Length) return "";
    return DecodeString(_data.AsSpan(start, size), stringType);
  }

  private static string DecodeString(ReadOnlySpan<byte> bytes, ushort stringType) {
    // 2 = Unicode (UTF-16LE), 1 = ANSI, 0 = no string.
    string raw;
    if (stringType == 2) {
      // Trim trailing nulls.
      var end = bytes.Length;
      while (end >= 2 && bytes[end - 1] == 0 && bytes[end - 2] == 0) end -= 2;
      raw = Encoding.Unicode.GetString(bytes[..end]);
    } else {
      var end = bytes.Length;
      while (end > 0 && bytes[end - 1] == 0) --end;
      raw = Encoding.Latin1.GetString(bytes[..end]);
    }
    return raw.TrimEnd('\0');
  }

  private int NextLogicalBlockBoundary(int pos) {
    if (_logicalBlockSize <= 0) return pos;
    return ((pos + _logicalBlockSize - 1) / _logicalBlockSize) * _logicalBlockSize;
  }

  private static bool IsKnownStreamId(uint id)
    => id == StanStreamId || id == PnamStreamId || id == FnamStreamId ||
       id == SpadStreamId || id == CsumStreamId || id == TsmpStreamId ||
       id == MqciStreamId;

  private static uint AsUInt32(string four) {
    if (four.Length != 4) throw new ArgumentException("four-char code required.", nameof(four));
    return ((uint)four[0]) | ((uint)four[1] << 8) | ((uint)four[2] << 16) | ((uint)four[3] << 24);
  }

  private static uint ReadUInt32LE(byte[] data, int offset)
    => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));

  private static ulong ReadUInt64LE(byte[] data, int offset)
    => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));

  private static string NormalizePath(string raw)
    => raw.Replace('\\', '/').Trim('/');

  private static string SanitizeFileName(string raw) {
    var trimmed = raw.Trim('\0', ' ');
    foreach (var c in Path.GetInvalidFileNameChars())
      trimmed = trimmed.Replace(c, '_');
    return trimmed;
  }
}
