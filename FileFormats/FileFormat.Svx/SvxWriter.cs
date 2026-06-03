#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Svx;

/// <summary>
/// Writes an uncompressed IFF / 8SVX file: <c>FORM</c> wrapper around a
/// <c>VHDR</c>, optional <c>CHAN</c> and a <c>BODY</c> of 8-bit signed PCM. For
/// stereo the left samples are written first, then the right samples (the planar
/// layout 8SVX uses for a stereo voice at octave 0). Used by
/// <see cref="SvxFormatDescriptor"/> to assemble a file from per-channel mono WAVs.
/// </summary>
public sealed class SvxWriter {

  /// <summary>
  /// Builds an uncompressed 8SVX. <paramref name="signedHalves"/> holds one entry
  /// for mono, or two (left then right) for stereo; each is signed 8-bit PCM with
  /// the same length.
  /// </summary>
  public byte[] Write(IReadOnlyList<byte[]> signedHalves, int sampleRate) {
    if (signedHalves.Count is < 1 or > 2)
      throw new ArgumentException("8SVX supports one (mono) or two (stereo) channels.");
    var samplesPerHalf = signedHalves[0].Length;
    if (signedHalves.Any(h => h.Length != samplesPerHalf))
      throw new ArgumentException("All channels must have the same sample count.");

    var stereo = signedHalves.Count == 2;
    var body = stereo ? Concat(signedHalves[0], signedHalves[1]) : signedHalves[0];

    using var ms = new MemoryStream();

    var vhdr = new byte[20];
    BinaryPrimitives.WriteUInt32BigEndian(vhdr.AsSpan(0), (uint)samplesPerHalf); // oneShotHiSamples
    BinaryPrimitives.WriteUInt32BigEndian(vhdr.AsSpan(4), 0);                     // repeatHiSamples
    BinaryPrimitives.WriteUInt32BigEndian(vhdr.AsSpan(8), 0);                     // samplesPerHiCycle
    BinaryPrimitives.WriteUInt16BigEndian(vhdr.AsSpan(12), (ushort)sampleRate);
    vhdr[14] = 1;                                                                 // ctOctave
    vhdr[15] = (byte)SvxReader.CompressionNone;
    BinaryPrimitives.WriteUInt32BigEndian(vhdr.AsSpan(16), 0x10000);              // volume 1.0 (16.16)

    var chan = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(chan, (uint)(stereo ? SvxReader.ChannelStereo : SvxReader.ChannelLeft));

    using var inner = new MemoryStream();
    WriteChunk(inner, "VHDR", vhdr);
    WriteChunk(inner, "CHAN", chan);
    WriteChunk(inner, "BODY", body);
    var innerBytes = inner.ToArray();

    Span<byte> head = stackalloc byte[12];
    "FORM"u8.CopyTo(head);
    BinaryPrimitives.WriteUInt32BigEndian(head[4..], (uint)(4 + innerBytes.Length)); // "8SVX" + chunks
    "8SVX"u8.CopyTo(head[8..]);
    ms.Write(head);
    ms.Write(innerBytes);
    return ms.ToArray();
  }

  private static byte[] Concat(byte[] a, byte[] b) {
    var r = new byte[a.Length + b.Length];
    a.CopyTo(r, 0);
    b.CopyTo(r, a.Length);
    return r;
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
