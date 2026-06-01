#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Aiff;

/// <summary>
/// Minimal uncompressed AIFF (FORM/AIFF) writer: a COMM chunk describing the
/// big-endian linear PCM plus an SSND chunk carrying the samples. Used by
/// <see cref="AiffFormatDescriptor"/> to assemble a multi-channel AIFF from
/// per-channel mono inputs. The sample rate is stored in the 80-bit IEEE 754
/// extended-precision form AIFF mandates — the inverse of
/// <see cref="AiffReader.Decode80BitFloatToInt"/>.
/// </summary>
public sealed class AiffWriter {

  /// <summary>
  /// Builds an uncompressed AIFF from already big-endian interleaved PCM.
  /// </summary>
  public byte[] Write(byte[] bigEndianInterleaved, int channels, int sampleRate, int bitsPerSample) {
    var bytesPerSample = bitsPerSample / 8;
    var frameBytes = bytesPerSample * channels;
    var sampleFrames = frameBytes == 0 ? 0 : bigEndianInterleaved.Length / frameBytes;

    // COMM body: numChannels(2) + numSampleFrames(4) + sampleSize(2) + rate(10) = 18 bytes.
    var comm = new byte[18];
    BinaryPrimitives.WriteInt16BigEndian(comm.AsSpan(0), (short)channels);
    BinaryPrimitives.WriteUInt32BigEndian(comm.AsSpan(2), (uint)sampleFrames);
    BinaryPrimitives.WriteInt16BigEndian(comm.AsSpan(6), (short)bitsPerSample);
    Encode80BitFloat(sampleRate).CopyTo(comm.AsSpan(8));

    // SSND body: offset(4) + blockSize(4) + sound data.
    var ssnd = new byte[8 + bigEndianInterleaved.Length];
    // offset and blockSize stay zero.
    bigEndianInterleaved.CopyTo(ssnd.AsSpan(8));

    var commChunk = WrapChunk("COMM", comm);
    var ssndChunk = WrapChunk("SSND", ssnd);
    var formPayloadLen = 4 + commChunk.Length + ssndChunk.Length; // "AIFF" + chunks

    var file = new byte[8 + formPayloadLen];
    var s = file.AsSpan();
    "FORM"u8.CopyTo(s);
    BinaryPrimitives.WriteUInt32BigEndian(s[4..], (uint)formPayloadLen);
    "AIFF"u8.CopyTo(s[8..]);
    commChunk.CopyTo(s[12..]);
    ssndChunk.CopyTo(s[(12 + commChunk.Length)..]);
    return file;
  }

  private static byte[] WrapChunk(string id, byte[] body) {
    var padded = body.Length + (body.Length & 1); // chunks pad to even length
    var chunk = new byte[8 + padded];
    var s = chunk.AsSpan();
    System.Text.Encoding.ASCII.GetBytes(id).CopyTo(s);
    BinaryPrimitives.WriteUInt32BigEndian(s[4..], (uint)body.Length);
    body.CopyTo(s[8..]);
    return chunk;
  }

  /// <summary>
  /// Encodes a non-negative integer sample rate as a 10-byte 80-bit IEEE 754
  /// extended-precision float (sign + 15-bit biased exponent + 64-bit mantissa
  /// with an explicit integer bit).
  /// </summary>
  public static byte[] Encode80BitFloat(int value) {
    var b = new byte[10];
    if (value <= 0) return b;

    var mantissa = (ulong)value;
    var exponent = 63;
    while ((mantissa & 0x8000000000000000UL) == 0) {
      mantissa <<= 1;
      --exponent;
    }
    var biasedExponent = 16383 + exponent;
    b[0] = (byte)((biasedExponent >> 8) & 0x7F);
    b[1] = (byte)(biasedExponent & 0xFF);
    for (var i = 0; i < 8; ++i)
      b[2 + i] = (byte)(mantissa >> (56 - i * 8));
    return b;
  }
}
