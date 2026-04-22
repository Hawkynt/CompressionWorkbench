#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Au;

/// <summary>
/// Sun / NeXT <c>.au</c> (<c>.snd</c>) header parser. The first 24 bytes are
/// all big-endian:
/// <list type="bullet">
///   <item>4-byte magic <c>.snd</c> (0x2E 0x73 0x6E 0x64).</item>
///   <item>uint32 data offset (≥ 24 — annotation bytes after byte 24 padded to this).</item>
///   <item>uint32 data size (<c>0xFFFFFFFF</c> means "until EOF" in streaming).</item>
///   <item>uint32 encoding (1=μ-law, 2=8-bit PCM, 3=16-bit BE PCM, 4=24-bit BE,
///         5=32-bit BE, 6/7=float, 23=G.721 ADPCM, 27=A-law).</item>
///   <item>uint32 sample rate.</item>
///   <item>uint32 channels.</item>
/// </list>
/// Bytes between header end (24) and <c>data offset</c> are an optional ASCII
/// annotation string.
/// </summary>
public sealed class AuReader {
  public sealed record ParsedAu(
    uint Encoding,
    int SampleRate,
    int NumChannels,
    byte[] SoundData,
    string Annotation);

  public ParsedAu Read(ReadOnlySpan<byte> data) {
    if (data.Length < 24)
      throw new InvalidDataException(".au too short for 24-byte header.");
    if (data[0] != '.' || data[1] != 's' || data[2] != 'n' || data[3] != 'd')
      throw new InvalidDataException("Missing .snd magic.");
    var dataOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
    var dataSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
    var encoding = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
    var sampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
    var channels = (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]);

    if (dataOffset < 24 || dataOffset > data.Length)
      throw new InvalidDataException($".au data offset {dataOffset} out of range.");

    var annotation = "";
    if (dataOffset > 24) {
      var raw = data.Slice(24, dataOffset - 24);
      // Trim trailing NULs.
      var end = raw.Length;
      while (end > 0 && raw[end - 1] == 0) --end;
      annotation = Encoding.ASCII.GetString(raw[..end]);
    }

    var effectiveSize = dataSize == -1 /* 0xFFFFFFFF */ ? data.Length - dataOffset : dataSize;
    if (dataOffset + effectiveSize > data.Length) effectiveSize = data.Length - dataOffset;
    if (effectiveSize < 0) effectiveSize = 0;
    var sound = data.Slice(dataOffset, effectiveSize).ToArray();

    return new ParsedAu(encoding, sampleRate, channels, sound, annotation);
  }
}
