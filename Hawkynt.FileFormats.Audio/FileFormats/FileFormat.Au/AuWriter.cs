#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Au;

/// <summary>
/// Sun / NeXT <c>.au</c> writer: the 24-byte big-endian header followed by
/// big-endian linear PCM. Used by <see cref="AuFormatDescriptor"/> to assemble a
/// multi-channel <c>.au</c> from per-channel mono inputs.
/// </summary>
public sealed class AuWriter {

  /// <summary>
  /// Builds a linear-PCM <c>.au</c> from already big-endian interleaved samples.
  /// The encoding field is derived from <paramref name="bitsPerSample"/>
  /// (8→2, 16→3, 24→4, 32→5).
  /// </summary>
  public byte[] Write(byte[] bigEndianInterleaved, int channels, int sampleRate, int bitsPerSample) {
    var encoding = bitsPerSample switch {
      8 => 2u,
      16 => 3u,
      24 => 4u,
      32 => 5u,
      _ => throw new ArgumentException($"Unsupported .au PCM width: {bitsPerSample} bits."),
    };

    const int headerSize = 24;
    var file = new byte[headerSize + bigEndianInterleaved.Length];
    var s = file.AsSpan();
    ".snd"u8.CopyTo(s);
    BinaryPrimitives.WriteUInt32BigEndian(s[4..], headerSize);
    BinaryPrimitives.WriteUInt32BigEndian(s[8..], (uint)bigEndianInterleaved.Length);
    BinaryPrimitives.WriteUInt32BigEndian(s[12..], encoding);
    BinaryPrimitives.WriteUInt32BigEndian(s[16..], (uint)sampleRate);
    BinaryPrimitives.WriteUInt32BigEndian(s[20..], (uint)channels);
    bigEndianInterleaved.CopyTo(s[headerSize..]);
    return file;
  }
}
