#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Maud;

/// <summary>
/// Writes an uncompressed IFF / MAUD file: a <c>FORM</c> wrapper around an
/// <c>MHDR</c> header and an <c>MDAT</c> body of signed 16-bit big-endian PCM. The
/// supplied sample buffer is already interleaved in MAUD's big-endian sample order.
/// Used by <see cref="MaudFormatDescriptor"/> to assemble a file from per-channel mono
/// WAVs.
/// </summary>
public sealed class MaudWriter {

  /// <summary>
  /// Builds an uncompressed 16-bit MAUD. <paramref name="interleavedBe"/> holds the
  /// interleaved signed 16-bit big-endian samples; <paramref name="numChannels"/> is
  /// 1 (mono) or 2 (stereo, interleaved).
  /// </summary>
  public byte[] Write(byte[] interleavedBe, int numChannels, int sampleRate) {
    if (numChannels is < 1 or > 2)
      throw new ArgumentException("MAUD supports one (mono) or two (stereo) channels.");

    var frameCount = interleavedBe.Length / (2 * numChannels);

    var mhdr = new byte[32];
    BinaryPrimitives.WriteUInt32BigEndian(mhdr.AsSpan(0), (uint)frameCount);   // sampleCount
    BinaryPrimitives.WriteUInt16BigEndian(mhdr.AsSpan(4), 16);                 // bits compressed
    BinaryPrimitives.WriteUInt16BigEndian(mhdr.AsSpan(6), 16);                 // bits uncompressed
    BinaryPrimitives.WriteUInt32BigEndian(mhdr.AsSpan(8), (uint)sampleRate);   // rate source
    BinaryPrimitives.WriteUInt16BigEndian(mhdr.AsSpan(12), 1);                 // rate divide
    BinaryPrimitives.WriteUInt16BigEndian(mhdr.AsSpan(14),
      (ushort)(numChannels == 2 ? MaudReader.ChannelInfoStereo : MaudReader.ChannelInfoMono));
    BinaryPrimitives.WriteUInt16BigEndian(mhdr.AsSpan(16), (ushort)numChannels);
    BinaryPrimitives.WriteUInt16BigEndian(mhdr.AsSpan(18), (ushort)MaudReader.CompressionNone);
    // bytes 20..31 reserved (zero).

    using var inner = new MemoryStream();
    WriteChunk(inner, "MHDR", mhdr);
    WriteChunk(inner, "MDAT", interleavedBe);
    var innerBytes = inner.ToArray();

    using var ms = new MemoryStream();
    Span<byte> head = stackalloc byte[12];
    "FORM"u8.CopyTo(head);
    BinaryPrimitives.WriteUInt32BigEndian(head[4..], (uint)(4 + innerBytes.Length)); // "MAUD" + chunks
    "MAUD"u8.CopyTo(head[8..]);
    ms.Write(head);
    ms.Write(innerBytes);
    return ms.ToArray();
  }

  private static void WriteChunk(Stream s, string id, byte[] body) {
    Span<byte> head = stackalloc byte[8];
    Encoding.ASCII.GetBytes(id).CopyTo(head);
    BinaryPrimitives.WriteUInt32BigEndian(head[4..], (uint)body.Length);
    s.Write(head);
    s.Write(body);
    if ((body.Length & 1) != 0) s.WriteByte(0); // word-align with a pad byte
  }
}
