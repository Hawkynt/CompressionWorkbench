#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Ircam;

/// <summary>
/// IRCAM / BICSF (<c>.sf</c>) sound-file header parser. The first four bytes are a
/// magic that fixes byte order: a leading <c>0x64</c> marks a little-endian (VAX)
/// file, a trailing <c>0x64</c> a big-endian (Sun) file. Canonical magics are
/// <c>64 A3 01 00</c> / <c>00 01 A3 64</c>, with machine variants
/// <c>…02…</c>/<c>…03…</c>/<c>…04…</c>. The header (in file byte order) carries an
/// f32 sample rate, u32 channel count and u32 sample format (1 = 8-bit linear,
/// 2 = 16-bit linear PCM, 4 = 32-bit IEEE float). Sample data begins at the fixed
/// offset 1024 in file byte order.
/// </summary>
public sealed class IrcamReader {
  public sealed record ParsedIrcam(
    int SampleRate,
    int Channels,
    uint SampleFormat,
    bool LittleEndian,
    byte[] SampleData);

  private const int DataOffset = 1024;

  public ParsedIrcam Read(ReadOnlySpan<byte> data) {
    if (data.Length < DataOffset)
      throw new InvalidDataException("IRCAM file too short for 1024-byte header.");

    bool littleEndian;
    if (data[0] == 0x64 && data[1] == 0xA3)
      littleEndian = true;                 // 64 A3 xx 00 — VAX / machine little-endian
    else if (data[2] == 0xA3 && data[3] == 0x64)
      littleEndian = false;                // 00 xx A3 64 — Sun / machine big-endian
    else
      throw new InvalidDataException("Missing IRCAM/BICSF magic.");

    var rateBits = data.Slice(4, 4);
    var sampleRate = littleEndian
      ? BinaryPrimitives.ReadSingleLittleEndian(rateBits)
      : BinaryPrimitives.ReadSingleBigEndian(rateBits);
    var channels = littleEndian
      ? BinaryPrimitives.ReadUInt32LittleEndian(data[8..])
      : BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
    var format = littleEndian
      ? BinaryPrimitives.ReadUInt32LittleEndian(data[12..])
      : BinaryPrimitives.ReadUInt32BigEndian(data[12..]);

    var sampleData = data[DataOffset..].ToArray();
    return new ParsedIrcam((int)sampleRate, (int)channels, format, littleEndian, sampleData);
  }
}
