#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Wem;

/// <summary>
/// Parses an Audiokinetic Wwise encoded-media <c>.wem</c> file. WEM reuses the RIFF/WAVE
/// container (<c>RIFF…WAVE</c>) with Wwise-specific <c>fmt </c> contents and extra chunks
/// (<c>data</c>, <c>akd </c>, <c>cue </c>, …). This reader walks the chunk list, captures the
/// <c>fmt </c> fields and the <c>data</c> region, and keeps every other chunk addressable as
/// a raw blob.
/// </summary>
public sealed class WemReader {

  /// <summary>
  /// Gets or sets the format tag.
  /// </summary>
public int FormatTag { get; private set; }
  /// <summary>
  /// Gets or sets the channels.
  /// </summary>
public int Channels { get; private set; }
  /// <summary>
  /// Gets or sets the sample rate.
  /// </summary>
public int SampleRate { get; private set; }
  /// <summary>
  /// Gets or sets the bits per sample.
  /// </summary>
public int BitsPerSample { get; private set; }
  /// <summary>
  /// Gets or sets the block align.
  /// </summary>
public int BlockAlign { get; private set; }
  /// <summary>
  /// Gets or sets the channel mask.
  /// </summary>
public ulong ChannelMask { get; private set; }
  /// <summary>
  /// Gets or sets the data.
  /// </summary>
public byte[] Data { get; private set; } = [];

  /// <summary>Every non-fmt/non-data chunk, in file order: (4CC id, raw body bytes).</summary>
  public IReadOnlyList<(string Id, byte[] Data)> ExtraChunks => this._extra;
  private readonly List<(string Id, byte[] Data)> _extra = [];

  /// <summary>
  /// Initializes a new instance of <see cref="WemReader"/>.
  /// </summary>
public WemReader(ReadOnlySpan<byte> data) {
    if (data.Length < 12 || data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
      throw new InvalidDataException("Missing RIFF magic.");
    if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E')
      throw new InvalidDataException("RIFF payload is not WAVE.");

    var pos = 12;
    while (pos + 8 <= data.Length) {
      var id = Encoding.ASCII.GetString(data.Slice(pos, 4));
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
      var bodyStart = pos + 8;
      if (size < 0 || bodyStart + size > data.Length)
        break; // truncated chunk: stop gracefully

      var body = data.Slice(bodyStart, size);
      switch (id) {
        case "fmt ":
          this.ParseFmt(body);
          break;
        case "data":
          this.Data = body.ToArray();
          break;
        default:
          this._extra.Add((id, body.ToArray()));
          break;
      }

      pos = bodyStart + size + (size & 1); // RIFF chunks pad to even size
    }
  }

  private void ParseFmt(ReadOnlySpan<byte> body) {
    if (body.Length < 16)
      return;
    this.FormatTag = BinaryPrimitives.ReadUInt16LittleEndian(body);
    this.Channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
    this.SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
    this.BlockAlign = BinaryPrimitives.ReadUInt16LittleEndian(body[12..]);
    this.BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);

    // WAVEFORMATEXTENSIBLE: cbSize ≥ 22 carries dwChannelMask at +20 of the fmt body.
    if (body.Length >= 18) {
      var cbSize = BinaryPrimitives.ReadUInt16LittleEndian(body[16..]);
      if (cbSize >= 22 && body.Length >= 18 + 6)
        this.ChannelMask = BinaryPrimitives.ReadUInt32LittleEndian(body[20..]);
    }
  }
}
