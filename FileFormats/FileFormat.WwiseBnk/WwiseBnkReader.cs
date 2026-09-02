#pragma warning disable CS1591
using System.Text;

namespace FileFormat.WwiseBnk;

/// <summary>
/// Represents a wem entry.
/// </summary>
public sealed class WemEntry {
    /// <summary>
  /// Gets or sets the wem id.
  /// </summary>
public uint WemId { get; init; }
    /// <summary>
  /// Gets or sets the offset.
  /// </summary>
public uint Offset { get; init; }
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public uint Size { get; init; }
}

/// <summary>
/// Represents a hirc object.
/// </summary>
public sealed class HircObject {
    /// <summary>
  /// Gets or sets the type.
  /// </summary>
public byte Type { get; init; }
    /// <summary>
  /// Gets or sets the id.
  /// </summary>
public uint Id { get; init; }
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public uint Size { get; init; }
}

/// <summary>
/// Parses a Wwise SoundBank (.bnk) file as a sequence of RIFF-style 4CC+uint32-size chunks.
/// Known chunks: BKHD (header), DIDX (data index), DATA (embedded WEM blob pool),
/// HIRC (hierarchy of sound/event objects), STID (soundbank id→name table),
/// INIT (init data), STMG (state manager).
/// </summary>
public sealed class WwiseBnkReader {

  private readonly Stream _stream;

    /// <summary>
  /// Gets or sets the bank version.
  /// </summary>
public uint BankVersion { get; private set; }
    /// <summary>
  /// Gets or sets the bank id.
  /// </summary>
public uint BankId { get; private set; }
    /// <summary>
  /// Gets or sets the data chunk offset.
  /// </summary>
public long DataChunkOffset { get; private set; }
    /// <summary>
  /// Gets or sets the data chunk size.
  /// </summary>
public long DataChunkSize { get; private set; }
    /// <summary>
  /// Gets the wems.
  /// </summary>
public List<WemEntry> Wems { get; } = [];
    /// <summary>
  /// Gets the hirc objects.
  /// </summary>
public List<HircObject> HircObjects { get; } = [];
    /// <summary>
  /// Gets the chunks.
  /// </summary>
public Dictionary<string, long> Chunks { get; } = [];

  /// <summary>Maps each top-level chunk tag to its (body offset, body length) so
  /// callers can surface a raw per-section blob (BKHD.bin, HIRC.bin, …).</summary>
  public Dictionary<string, (long Offset, long Length)> ChunkSpans { get; } = [];

  /// <summary>Reads a top-level chunk's raw body bytes by its 4CC tag.</summary>
  public byte[] ExtractChunk(string tag) {
    if (!this.ChunkSpans.TryGetValue(tag, out var span))
      throw new InvalidDataException($"No {tag} chunk present.");
    this._stream.Position = span.Offset;
    var buf = new byte[span.Length];
    var read = 0;
    while (read < buf.Length) {
      var n = this._stream.Read(buf, read, buf.Length - read);
      if (n == 0) break;
      read += n;
    }
    if (read < buf.Length) throw new InvalidDataException($"Truncated {tag} chunk.");
    return buf;
  }

    /// <summary>
  /// Initializes a new instance of <see cref="WwiseBnkReader"/>.
  /// </summary>
public WwiseBnkReader(Stream stream) {
    this._stream = stream;
    stream.Position = 0;

    // First chunk must be BKHD
    var didxBuf = default(byte[]);
    while (stream.Position + 8 <= stream.Length) {
      var tag = ReadFourCC();
      var size = ReadUInt32LE();
      var chunkStart = stream.Position;
      this.Chunks[tag] = chunkStart;
      this.ChunkSpans[tag] = (chunkStart, Math.Min(size, Math.Max(0, stream.Length - chunkStart)));

      switch (tag) {
        case "BKHD":
          this.BankVersion = ReadUInt32LE();
          this.BankId = ReadUInt32LE();
          break;
        case "DIDX":
          // N × 12-byte entries
          didxBuf = new byte[size];
          if (stream.Read(didxBuf, 0, (int)size) < (int)size)
            throw new InvalidDataException("Truncated DIDX chunk.");
          break;
        case "DATA":
          this.DataChunkOffset = chunkStart;
          this.DataChunkSize = size;
          break;
        case "HIRC": {
          var count = ReadUInt32LE();
          long hircEnd = chunkStart + size;
          for (uint i = 0; i < count && stream.Position + 5 <= hircEnd; i++) {
            byte typeByte = (byte)stream.ReadByte();
            uint objSize = ReadUInt32LE();
            long objEnd = stream.Position + objSize;
            if (objEnd > hircEnd) break;
            uint objId = objSize >= 4 ? ReadUInt32LE() : 0;
            this.HircObjects.Add(new HircObject { Type = typeByte, Id = objId, Size = objSize });
            stream.Position = objEnd;
          }
          break;
        }
      }

      // Advance to next chunk
      stream.Position = chunkStart + size;
    }

    if (didxBuf != null) {
      int entryCount = didxBuf.Length / 12;
      for (int i = 0; i < entryCount; i++) {
        int o = i * 12;
        uint id = BitConverter.ToUInt32(didxBuf, o);
        uint off = BitConverter.ToUInt32(didxBuf, o + 4);
        uint sz = BitConverter.ToUInt32(didxBuf, o + 8);
        this.Wems.Add(new WemEntry { WemId = id, Offset = off, Size = sz });
      }
    }
  }

    /// <summary>
  /// Performs the extract wem operation.
  /// </summary>
public byte[] ExtractWem(WemEntry e) {
    if (this.DataChunkOffset == 0) throw new InvalidDataException("No DATA chunk present.");
    this._stream.Position = this.DataChunkOffset + e.Offset;
    var buf = new byte[e.Size];
    if (this._stream.Read(buf, 0, (int)e.Size) < e.Size)
      throw new InvalidDataException($"Unexpected EOF reading WEM 0x{e.WemId:X8}.");
    return buf;
  }

  private string ReadFourCC() {
    Span<byte> b = stackalloc byte[4];
    if (this._stream.Read(b) < 4) throw new InvalidDataException("Unexpected EOF reading chunk tag.");
    return Encoding.ASCII.GetString(b);
  }

  private uint ReadUInt32LE() {
    Span<byte> b = stackalloc byte[4];
    if (this._stream.Read(b) < 4) throw new InvalidDataException("Unexpected EOF reading uint32.");
    return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
  }
}
