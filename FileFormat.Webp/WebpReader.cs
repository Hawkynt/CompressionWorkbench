#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Webp;

/// <summary>
/// Walks a RIFF/WEBP container and enumerates its chunks. For still images the
/// result has a single VP8/VP8L chunk; for animations VP8X + ANIM + N×ANMF frames
/// are surfaced. Each <see cref="Chunk"/> carries the raw chunk bytes (excluding
/// the 8-byte FourCC + size header).
/// </summary>
public sealed class WebpReader {
  public sealed record Chunk(string FourCc, long BodyOffset, int BodyLength);

  private readonly byte[] _data;
  public IReadOnlyList<Chunk> Chunks { get; }

  public WebpReader(byte[] data) {
    this._data = data;
    this.Chunks = Parse();
  }

  private List<Chunk> Parse() {
    var result = new List<Chunk>();
    if (this._data.Length < 12) return result;
    if (this._data[0] != 'R' || this._data[1] != 'I' || this._data[2] != 'F' || this._data[3] != 'F' ||
        this._data[8] != 'W' || this._data[9] != 'E' || this._data[10] != 'B' || this._data[11] != 'P')
      throw new InvalidDataException("Not a WEBP: RIFF/WEBP header mismatch.");

    var pos = 12;
    while (pos + 8 <= this._data.Length) {
      var fourCc = Encoding.ASCII.GetString(this._data.AsSpan(pos, 4));
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(pos + 4));
      if (pos + 8 + size > this._data.Length) break;
      result.Add(new Chunk(fourCc, pos + 8, size));
      // Chunks are padded to even length.
      pos += 8 + size + (size & 1);
    }
    return result;
  }

  /// <summary>Copies the chunk's body bytes into a new array.</summary>
  public byte[] ReadBody(Chunk chunk)
    => this._data.AsSpan((int)chunk.BodyOffset, chunk.BodyLength).ToArray();
}
