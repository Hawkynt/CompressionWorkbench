#pragma warning disable CS1591
using System.Text;
using Compression.Core.Dictionary.Lzx;

namespace FileFormat.Chm;

/// <summary>
/// Read-only reader for Microsoft Compiled HTML Help (.chm) files.
/// Supports section 0 (uncompressed) and section 1 (LZX-compressed) entries.
/// </summary>
public sealed class ChmReader {
  // -------------------------------------------------------------------------
  // Constants
  // -------------------------------------------------------------------------

  private const uint ItsfMagic = 0x46535449; // "ITSF" LE
  private const uint ItspMagic = 0x50535449; // "ITSP" LE
  private const uint PmglMagic = 0x4C474D50; // "PMGL" LE
  private const uint PmgiMagic = 0x49474D50; // "PMGI" LE

  private const int ItsfHeaderMinSize      = 96;  // bytes before optional content offset
  private const int ItspHeaderSize         = 84;
  private const int PmglHeaderSize         = 20;  // "PMGL" + freeSpace + unknown + prevChunk + nextChunk
  private const int GuidSize               = 16;

  // -------------------------------------------------------------------------
  // Fields
  // -------------------------------------------------------------------------

  private readonly Stream _stream;

  // ITSF fields
  private readonly uint   _itsfVersion;
  private readonly long   _dirSectionOffset;
  private readonly long   _dirSectionLength;
  private readonly long   _contentOffset;

  // ITSP fields
  private readonly uint   _chunkSize;
  private readonly int    _firstPmglChunk;
  private readonly uint   _numDirChunks;
  private readonly long   _chunksStart;   // byte offset of first directory chunk in stream

  // LZX decompression state (lazily loaded)
  private bool   _lzxLoaded;
  private byte[]? _lzxUncompressed;
  private long    _lzxUncompressedSize;
  private int     _lzxWindowBits = 15;

  // -------------------------------------------------------------------------
  // Public API
  // -------------------------------------------------------------------------

  /// <summary>All directory entries found in the CHM.</summary>
  public IReadOnlyList<ChmEntry> Entries { get; }

  /// <summary>
  /// Opens and parses the CHM directory from <paramref name="stream"/>.
  /// The stream must remain open for the lifetime of this reader.
  /// </summary>
  /// <exception cref="InvalidDataException">Thrown when the CHM header is malformed.</exception>
  public ChmReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;

    // ---- Parse ITSF header ----
    stream.Position = 0;
    using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

    uint magic;
    try {
      magic = br.ReadUInt32();
    } catch (EndOfStreamException) {
      throw new InvalidDataException("Not a valid CHM file: stream too short to contain ITSF header.");
    }
    if (magic != ItsfMagic)
      throw new InvalidDataException($"Not a valid CHM file: expected ITSF magic, got 0x{magic:X8}.");

    this._itsfVersion  = br.ReadUInt32();  // version (3 or 4)
    var headerSize     = br.ReadUInt32();  // total ITSF header size
    /* unknown1 */       br.ReadUInt32();
    /* timestamp */      br.ReadUInt32();
    /* languageId */     br.ReadUInt32();
    // Two GUIDs (16 bytes each)
    br.ReadBytes(GuidSize);
    br.ReadBytes(GuidSize);
    // Directory section descriptor
    this._dirSectionOffset = (long)br.ReadUInt64();
    this._dirSectionLength = (long)br.ReadUInt64();
    // Content offset (version >= 3)
    this._contentOffset = this._itsfVersion >= 3 ? (long)br.ReadUInt64() : (long)headerSize;

    // ---- Parse ITSP header (at dirSectionOffset) ----
    stream.Position = this._dirSectionOffset;
    var itspMagic = br.ReadUInt32();
    if (itspMagic != ItspMagic)
      throw new InvalidDataException($"Expected ITSP header at offset {this._dirSectionOffset}, got 0x{itspMagic:X8}.");

    var itspVersion    = br.ReadUInt32();
    var dirHeaderSize  = br.ReadUInt32();
    /* unknown1 */       br.ReadUInt32();
    this._chunkSize    = br.ReadUInt32();  // typically 4096
    /* density */        br.ReadUInt32();
    /* indexTreeDepth */ br.ReadUInt32();
    /* rootIndex */      br.ReadInt32();
    this._firstPmglChunk = br.ReadInt32();
    /* lastPmgl */       br.ReadInt32();
    /* unknown2 */       br.ReadInt32();
    this._numDirChunks = br.ReadUInt32();
    // remaining ITSP fields (languageId, guid, chunkLength, ...) are not needed

    // Chunks start immediately after the ITSP directory header
    this._chunksStart = this._dirSectionOffset + dirHeaderSize;

    // ---- Walk PMGL chunks and collect entries ----
    this.Entries = this.ReadAllEntries(br);

    // ---- Pre-read LZX control data so window bits are known ----
    this.TryReadLzxControlData();
  }

  // -------------------------------------------------------------------------
  // Entry extraction
  // -------------------------------------------------------------------------

  /// <summary>
  /// Extracts the raw bytes for the given <paramref name="entry"/>.
  /// </summary>
  /// <exception cref="NotSupportedException">Thrown for unsupported content sections (&gt; 1).</exception>
  public byte[] Extract(ChmEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.Size == 0)
      return [];

    return entry.Section switch {
      0 => this.ExtractSection0(entry),
      1 => this.ExtractSection1(entry),
      _ => throw new NotSupportedException($"CHM content section {entry.Section} is not supported.")
    };
  }

  // -------------------------------------------------------------------------
  // Private: directory reading
  // -------------------------------------------------------------------------

  private List<ChmEntry> ReadAllEntries(BinaryReader br) {
    var entries = new List<ChmEntry>();

    // Walk the PMGL linked list starting at _firstPmglChunk
    var chunkIndex = this._firstPmglChunk;
    var visited    = new HashSet<int>();

    while (chunkIndex >= 0 && chunkIndex < (int)this._numDirChunks) {
      if (!visited.Add(chunkIndex))
        break; // cycle guard

      var chunkOffset = this._chunksStart + (long)chunkIndex * this._chunkSize;
      this._stream.Position = chunkOffset;

      var magic = br.ReadUInt32();
      if (magic != PmglMagic) {
        // Could be PMGI (index chunk) — skip it
        chunkIndex++;
        continue;
      }

      var freeSpace = br.ReadUInt32();
      /* unknown */   br.ReadUInt32();
      /* prevChunk */ br.ReadInt32();
      var nextChunk = br.ReadInt32();

      // Data runs from end of PMGL header to (chunkSize - freeSpace)
      var dataStart = chunkOffset + PmglHeaderSize;
      var dataEnd   = chunkOffset + this._chunkSize - freeSpace;

      // Current read position is already at dataStart (header was 20 bytes, header = PmglHeaderSize)
      while (this._stream.Position < dataEnd) {
        // Read entry name: ENCINT length + UTF-8 bytes
        var nameLen = ReadEncInt(br);
        if (nameLen < 0 || this._stream.Position + nameLen > dataEnd)
          break;

        var nameBytes = br.ReadBytes((int)nameLen);
        var name      = Encoding.UTF8.GetString(nameBytes);

        var section = (int)ReadEncInt(br);
        var offset  = ReadEncInt(br);
        var size    = ReadEncInt(br);

        entries.Add(new ChmEntry {
          Path    = name,
          Section = section,
          Offset  = offset,
          Size    = size,
        });
      }

      // Follow the PMGL next-chunk link
      chunkIndex = nextChunk;
    }

    return entries;
  }

  // -------------------------------------------------------------------------
  // Private: section 0 (uncompressed) extraction
  // -------------------------------------------------------------------------

  private byte[] ExtractSection0(ChmEntry entry) {
    var absOffset = this._contentOffset + entry.Offset;
    this._stream.Position = absOffset;

    var data    = new byte[entry.Size];
    var total   = 0;
    while (total < data.Length) {
      var read = this._stream.Read(data, total, data.Length - total);
      if (read == 0)
        throw new EndOfStreamException($"Unexpected end of stream reading CHM entry '{entry.Path}'.");
      total += read;
    }

    return data;
  }

  // -------------------------------------------------------------------------
  // Private: section 1 (LZX) extraction
  // -------------------------------------------------------------------------

  private void TryReadLzxControlData() {
    // "::DataSpace/Storage/MSCompressed/ControlData"
    var ctrl = this.Entries.FirstOrDefault(
      e => e.Path.Equals("::DataSpace/Storage/MSCompressed/ControlData",
                          StringComparison.OrdinalIgnoreCase));
    if (ctrl == null || ctrl.Size < 28 || ctrl.Section != 0)
      return;

    var data = this.ExtractSection0(ctrl);
    // ControlData is an array of uint32 LE words
    // word[0] = number of words following (should be 6)
    // word[6] = LZX window size (e.g. 2 = 2^(x+15) — actually stored as a value like 2 meaning window = 2^(2+15) = 2^17)
    //   Microsoft docs: the 7th uint32 (index 6) contains the window bits as: bits 0-4
    if (data.Length >= 28) {
      // word[6] (bytes 24-27)
      var word6 = BitConverter.ToUInt32(data, 24);
      // The window size is encoded as: 2^(word6 + 15) ... but many CHMs use word6 directly as window bits
      // In practice CHM files store the window size directly in the lower 5 bits, ranging 15-21
      var windowBits = (int)(word6 & 0x1F);
      if (windowBits is >= 15 and <= 21)
        this._lzxWindowBits = windowBits;
    }
  }

  private void EnsureLzxDecompressed() {
    if (this._lzxLoaded)
      return;
    this._lzxLoaded = true;

    // Find the reset table and compressed content entries
    var resetTableEntry = this.Entries.FirstOrDefault(
      e => e.Path.StartsWith("::DataSpace/Storage/MSCompressed/Transform/",
                              StringComparison.OrdinalIgnoreCase) &&
           e.Path.EndsWith("/InstanceData/ResetTable",
                            StringComparison.OrdinalIgnoreCase) &&
           e.Section == 0);

    var contentEntry = this.Entries.FirstOrDefault(
      e => e.Path.Equals("::DataSpace/Storage/MSCompressed/Content",
                          StringComparison.OrdinalIgnoreCase) &&
           e.Section == 0);

    if (contentEntry == null || contentEntry.Size == 0)
      return;

    // Read compressed content blob
    var compressedData = this.ExtractSection0(contentEntry);

    if (resetTableEntry != null && resetTableEntry.Size >= 32) {
      this.DecompressLzxWithResetTable(compressedData, resetTableEntry);
    } else {
      // No reset table — decompress entire blob as a single LZX stream
      // We need the uncompressed size; without reset table we attempt a best-effort decompression
      // using a generous output buffer estimate (4x compressed)
      var estimatedSize = (int)Math.Min(compressedData.Length * 4L, 64 * 1024 * 1024);
      try {
        using var ms   = new MemoryStream(compressedData);
        var decompressor = new LzxDecompressor(ms, this._lzxWindowBits);
        this._lzxUncompressed     = decompressor.Decompress(estimatedSize);
        this._lzxUncompressedSize = this._lzxUncompressed.Length;
      } catch {
        this._lzxUncompressed     = null;
        this._lzxUncompressedSize = 0;
      }
    }
  }

  private void DecompressLzxWithResetTable(byte[] compressedData, ChmEntry resetTableEntry) {
    // ResetTable layout (all LE):
    //   uint32 version
    //   uint32 numEntries
    //   uint32 entrySize (should be 8 — each entry is a uint64 offset)
    //   uint32 tableHeaderSize
    //   uint64 uncompressedSize
    //   uint64 compressedSize
    //   uint32 blockSize  (reset interval in uncompressed bytes, typically 0x8000 = 32 KB)
    //   uint64[] resetOffsets  (compressed-data offsets of each reset point)
    var rtData = this.ExtractSection0(resetTableEntry);
    if (rtData.Length < 32)
      return;

    var numEntries       = BitConverter.ToUInt32(rtData, 4);
    var entrySize        = BitConverter.ToUInt32(rtData, 8);
    var tableHeaderSize  = BitConverter.ToUInt32(rtData, 12);
    var uncompressedSize = (long)BitConverter.ToUInt64(rtData, 16);
    /* compressedSize */
    var blockSize        = BitConverter.ToUInt32(rtData, 28);

    if (blockSize == 0 || entrySize != 8)
      return;

    if (uncompressedSize <= 0 || uncompressedSize > 512 * 1024 * 1024)
      return; // safety guard

    this._lzxUncompressedSize = uncompressedSize;
    var output = new byte[uncompressedSize];
    var outPos = 0;

    // Parse reset offsets
    var offsetTableStart = (int)tableHeaderSize;
    var resetOffsets     = new long[numEntries];
    for (var i = 0; i < numEntries && offsetTableStart + i * 8 + 8 <= rtData.Length; i++)
      resetOffsets[i] = (long)BitConverter.ToUInt64(rtData, offsetTableStart + i * 8);

    // Decompress each reset interval independently
    for (var i = 0; i < numEntries; i++) {
      var compStart = resetOffsets[i];
      var compEnd   = i + 1 < numEntries ? resetOffsets[i + 1] : compressedData.Length;
      if (compStart >= compressedData.Length || compStart >= compEnd)
        continue;

      var compLen    = (int)(compEnd - compStart);
      var outLen     = (int)Math.Min(blockSize, uncompressedSize - outPos);
      if (outLen <= 0)
        break;

      try {
        using var blockStream  = new MemoryStream(compressedData, (int)compStart, compLen);
        var decompressor       = new LzxDecompressor(blockStream, this._lzxWindowBits);
        var decompressed       = decompressor.Decompress(outLen);
        decompressed.AsSpan().CopyTo(output.AsSpan(outPos));
        outPos += decompressed.Length;
      } catch {
        // If a block fails, leave remaining bytes as zero and stop
        break;
      }
    }

    this._lzxUncompressed = output;
  }

  private byte[] ExtractSection1(ChmEntry entry) {
    this.EnsureLzxDecompressed();

    if (this._lzxUncompressed == null)
      throw new InvalidDataException($"Could not decompress LZX section for entry '{entry.Path}'.");

    var start = entry.Offset;
    var end   = start + entry.Size;
    if (start < 0 || end > this._lzxUncompressed.Length)
      throw new InvalidDataException(
        $"CHM entry '{entry.Path}' offset/size (0x{start:X}, {entry.Size}) is out of range " +
        $"for decompressed section 1 (size {this._lzxUncompressed.Length}).");

    return this._lzxUncompressed.AsSpan((int)start, (int)entry.Size).ToArray();
  }

  // -------------------------------------------------------------------------
  // Private: ENCINT decoder
  // -------------------------------------------------------------------------

  /// <summary>
  /// Reads a CHM variable-length integer (ENCINT).
  /// High bit of each byte signals continuation; low 7 bits are data (big-endian).
  /// Returns -1 on end-of-stream.
  /// </summary>
  private static long ReadEncInt(BinaryReader br) {
    long value = 0;
    for (var i = 0; i < 8; i++) {
      int b;
      try {
        b = br.ReadByte();
      } catch (EndOfStreamException) {
        return -1;
      }

      value = (value << 7) | (long)(b & 0x7F);
      if ((b & 0x80) == 0)
        return value;
    }

    return value; // truncated after 8 continuation bytes
  }
}
