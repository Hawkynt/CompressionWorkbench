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
  private const uint LzxcMagic = 0x43585A4C; // "LZXC" LE
  private bool    _lzxLoaded;
  private byte[]? _lzxUncompressed;
  private bool    _lzxControlValid;
  private long    _lzxResetIntervalBytes;
  private int     _lzxWindowBits;

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
    // Header section table: section 0 (file size record), then the directory (ITSP + chunks).
    /* hs0Offset */      br.ReadUInt64();
    /* hs0Length */      br.ReadUInt64();
    this._dirSectionOffset = (long)br.ReadUInt64();
    this._dirSectionLength = (long)br.ReadUInt64();
    // Version 3 records where content section 0 starts; before it, the content follows the directory.
    this._contentOffset = this._itsfVersion >= 3 && headerSize >= 0x60
      ? (long)br.ReadUInt64()
      : this._dirSectionOffset + this._dirSectionLength;

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
    // ::DataSpace/Storage/MSCompressed/ControlData is LZXC control data, little-endian dwords:
    //   [0] count of dwords that follow   [1] "LZXC"   [2] version
    //   [3] reset interval                [4] window size   [5] cache size   [6] unknown
    // Version 2 counts the interval and the window in 32 KiB units, version 1 in bytes.
    var ctrl = this.Entries.FirstOrDefault(
      e => e.Path.Equals("::DataSpace/Storage/MSCompressed/ControlData",
                          StringComparison.OrdinalIgnoreCase));
    if (ctrl == null || ctrl.Size < 20 || ctrl.Section != 0)
      return;

    var data = this.ExtractSection0(ctrl);
    if (data.Length < 20 || BitConverter.ToUInt32(data, 4) != LzxcMagic)
      return;
    var version = BitConverter.ToUInt32(data, 8);
    var unit = version >= 2 ? 0x8000L : 1L;
    this._lzxResetIntervalBytes = BitConverter.ToUInt32(data, 12) * unit;
    var windowBytes = BitConverter.ToUInt32(data, 16) * unit;
    for (var bits = 15; bits <= 21; ++bits)
      if (windowBytes == 1L << bits)
        this._lzxWindowBits = bits;
    this._lzxControlValid = this._lzxWindowBits > 0 && this._lzxResetIntervalBytes > 0;
  }

  private void EnsureLzxDecompressed() {
    if (this._lzxLoaded)
      return;
    this._lzxLoaded = true;

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
      throw new InvalidDataException("CHM: section 1 has no MSCompressed/Content stream.");
    if (!this._lzxControlValid)
      throw new InvalidDataException("CHM: MSCompressed/ControlData is missing or names no valid LZX window and reset interval.");
    if (resetTableEntry == null || resetTableEntry.Size < 40)
      throw new InvalidDataException("CHM: section 1 has no LZX reset table, so its uncompressed length is unknown.");

    this._lzxUncompressed = this.DecompressLzxWithResetTable(this.ExtractSection0(contentEntry), resetTableEntry);
  }

  private byte[] DecompressLzxWithResetTable(byte[] compressedData, ChmEntry resetTableEntry) {
    // ResetTable layout (all LE):
    //   uint32 version, uint32 numEntries, uint32 entrySize (8), uint32 tableHeaderSize (40)
    //   uint64 uncompressedSize, uint64 compressedSize, uint64 blockSize (the LZX frame, 0x8000)
    //   uint64[numEntries] compressed offset at which each frame starts
    // The decoder state carries across frames and is reset only every reset interval; a frame
    // that starts a reset interval starts a fresh LZX stream.
    var rtData = this.ExtractSection0(resetTableEntry);
    var numEntries       = BitConverter.ToUInt32(rtData, 4);
    var entrySize        = BitConverter.ToUInt32(rtData, 8);
    var tableHeaderSize  = BitConverter.ToUInt32(rtData, 12);
    var uncompressedSize = (long)BitConverter.ToUInt64(rtData, 16);
    var frameSize        = (long)BitConverter.ToUInt64(rtData, 32);

    if (entrySize != 8 || frameSize <= 0 || frameSize > int.MaxValue)
      throw new InvalidDataException($"CHM: LZX reset table with entry size {entrySize} and frame size {frameSize}.");
    if (uncompressedSize < 0 || uncompressedSize > int.MaxValue)
      throw new InvalidDataException($"CHM: LZX section of {uncompressedSize} bytes.");
    if (this._lzxResetIntervalBytes % frameSize != 0)
      throw new InvalidDataException($"CHM: LZX reset interval {this._lzxResetIntervalBytes} is not a whole number of {frameSize}-byte frames.");
    var framesPerReset = (int)(this._lzxResetIntervalBytes / frameSize);
    var frames = (int)((uncompressedSize + frameSize - 1) / frameSize);
    if (numEntries < frames || tableHeaderSize + (long)numEntries * 8 > rtData.Length)
      throw new InvalidDataException($"CHM: LZX reset table lists {numEntries} frames, the section needs {frames}.");

    var output = new byte[uncompressedSize];
    var outPos = 0;
    for (var frame = 0; frame < frames; frame += framesPerReset) {
      var compStart = (long)BitConverter.ToUInt64(rtData, (int)tableHeaderSize + frame * 8);
      if (compStart >= compressedData.Length)
        throw new InvalidDataException($"CHM: LZX frame {frame} starts past the compressed data.");
      // The first bit of a stream says whether x86 call targets were rewritten; CHM compilers do
      // not do that, and decoding such a stream without the translation would return wrong bytes.
      if (compStart + 1 < compressedData.Length && (compressedData[compStart + 1] & 0x80) != 0)
        throw new NotSupportedException("CHM: an LZX stream with Intel E8 preprocessing is not supported.");
      var decoder = new LzxDecompressor(new MemoryStream(compressedData, (int)compStart, compressedData.Length - (int)compStart),
        this._lzxWindowBits, e8Translation: false, LzxStreamFormat.Cab);
      for (var f = frame; f < Math.Min(frame + framesPerReset, frames); ++f) {
        var outLen = (int)Math.Min(frameSize, uncompressedSize - outPos);
        byte[] decoded;
        try {
          decoded = decoder.Decompress(outLen);
        } catch (Exception e) when (e is not InvalidDataException) {
          throw new InvalidDataException($"CHM: LZX frame {f} does not decode: {e.Message}", e);
        }
        decoded.AsSpan().CopyTo(output.AsSpan(outPos));
        outPos += decoded.Length;
      }
    }
    return output;
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
