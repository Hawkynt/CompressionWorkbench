#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Ircam;

/// <summary>
/// Writes a little-endian (VAX, magic <c>64 A3 01 00</c>) IRCAM/BICSF file: the
/// 1024-byte header carrying the f32 sample rate, u32 channel count and u32 sample
/// format (2 = 16-bit linear PCM), followed by the interleaved little-endian samples.
/// The output round-trips through <see cref="IrcamReader"/>.
/// </summary>
public sealed class IrcamWriter {
  private const int DataOffset = 1024;
  private const uint Format16BitLinear = 2;

  public byte[] Write(byte[] interleavedLe, int channels, int sampleRate) {
    var blob = new byte[DataOffset + interleavedLe.Length];
    var hdr = blob.AsSpan();
    hdr[0] = 0x64; hdr[1] = 0xA3; hdr[2] = 0x01; hdr[3] = 0x00;
    BinaryPrimitives.WriteSingleLittleEndian(hdr[4..], sampleRate);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[8..], (uint)channels);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[12..], Format16BitLinear);
    interleavedLe.CopyTo(blob, DataOffset);
    return blob;
  }
}
