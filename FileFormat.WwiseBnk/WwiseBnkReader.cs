#pragma warning disable CS1591
using System.Text;

namespace FileFormat.WwiseBnk;

public sealed class WemEntry {
  public uint WemId { get; init; }
  public uint Offset { get; init; }
  public uint Size { get; init; }
}

public sealed class HircObject {
  public byte Type { get; init; }
  public uint Id { get; init; }
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

  public uint BankVersion { get; private set; }
  public uint BankId { get; private set; }
  public long DataChunkOffset { get; private set; }
  public long DataChunkSize { get; private set; }
  public List<WemEntry> Wems { get; } = [];
  public List<HircObject> HircObjects { get; } = [];
  public Dictionary<string, long> Chunks { get; } = [];

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
