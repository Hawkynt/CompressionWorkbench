#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Avr;

/// <summary>
/// Writes a 16-bit signed big-endian AVR: the fixed 128-byte header followed by
/// interleaved big-endian samples. Used by <see cref="AvrFormatDescriptor"/> to
/// assemble a file from per-channel mono WAVs.
/// </summary>
public sealed class AvrWriter {

  /// <summary>
  /// Builds an AVR from interleaved 16-bit signed big-endian PCM.
  /// </summary>
  public byte[] Write(byte[] bigEndianInterleaved, int channels, int sampleRate, string name) {
    if (channels is not (1 or 2))
      throw new ArgumentException("AVR supports mono or stereo.");

    var sizeInSamples = (uint)(bigEndianInterleaved.Length / 2 / channels);
    var file = new byte[AvrReader.HeaderSize + bigEndianInterleaved.Length];
    var s = file.AsSpan();

    "2BIT"u8.CopyTo(s);
    var nameBytes = Encoding.ASCII.GetBytes(name);
    nameBytes.AsSpan(0, Math.Min(8, nameBytes.Length)).CopyTo(s[4..]);
    BinaryPrimitives.WriteUInt16BigEndian(s[12..], (ushort)(channels == 2 ? 0xFFFF : 0)); // mono/stereo
    BinaryPrimitives.WriteUInt16BigEndian(s[14..], 16);     // resolution
    BinaryPrimitives.WriteUInt16BigEndian(s[16..], 0xFFFF); // signed
    BinaryPrimitives.WriteUInt16BigEndian(s[18..], 0);      // loop
    BinaryPrimitives.WriteUInt16BigEndian(s[20..], 0xFFFF); // midi: none
    BinaryPrimitives.WriteUInt32BigEndian(s[22..], (uint)(sampleRate & 0x00FFFFFF));
    BinaryPrimitives.WriteUInt32BigEndian(s[26..], sizeInSamples);
    BinaryPrimitives.WriteUInt32BigEndian(s[30..], 0); // loop begin
    BinaryPrimitives.WriteUInt32BigEndian(s[34..], sizeInSamples); // loop end
    // bytes 38..63 reserved, 64..127 user — left zeroed.

    bigEndianInterleaved.CopyTo(s[AvrReader.HeaderSize..]);
    return file;
  }
}
