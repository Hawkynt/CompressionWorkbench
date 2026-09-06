#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Codec.Alac;

/// <summary>
/// The ALAC "magic cookie" (the codec-specific <c>ALACSpecificConfig</c> carried in
/// the <c>alac</c> sample-entry atom of an M4A file, or in the <c>kuki</c> chunk of a
/// CAF file). All multi-byte fields are big-endian.
/// <para>
/// The config itself is 24 bytes, but it is rarely handed over bare. As
/// <c>ALACMagicCookieDescription.txt</c> describes and the reference decoder's
/// <c>Init()</c> implements, it may be preceded by a <c>frma</c> atom and an
/// <c>alac</c> atom — that is how CAF carries it — and it may be followed by a
/// channel-layout atom and a terminator, which are not needed to decode.
/// <see cref="Parse"/> peels those wrappers, and also tolerates the bare 4-byte
/// version/flags prefix left over when a caller has already stripped the <c>alac</c>
/// box header. <see cref="Write"/> emits the bare 24-byte config.
/// </para>
/// </summary>
public sealed record AlacCookie(
    uint FrameLength,
    byte CompatibleVersion,
    byte BitDepth,
    byte Pb,
    byte Mb,
    byte Kb,
    byte NumChannels,
    ushort MaxRun,
    uint MaxFrameBytes,
    uint AvgBitRate,
    uint SampleRate) {

  /// <summary>Size in bytes of the bare config (no version/flags prefix).</summary>
  public const int Size = 24;

  /// <summary>
  /// Parses a magic cookie, peeling any <c>frma</c>/<c>alac</c> atom wrapper or bare
  /// version/flags prefix. Trailing atoms (channel layout, terminator) are ignored.
  /// </summary>
  public static AlacCookie Parse(ReadOnlySpan<byte> cookie) {
    var off = 0;

    // A 'frma' atom (size, 'frma', 'alac') and/or an 'alac' atom header
    // (size, 'alac', version/flags) may wrap the config; each is 12 bytes.
    if (HasAtomType(cookie, off, "frma"u8))
      off += 12;
    if (HasAtomType(cookie, off, "alac"u8))
      off += 12;
    else if (off == 0 && cookie.Length >= 4 + Size
             && BinaryPrimitives.ReadUInt32BigEndian(cookie) == 0
             && BinaryPrimitives.ReadUInt32BigEndian(cookie[4..]) is >= 64 and <= 1u << 20)
      // No atom header, but the full-box version/flags word is still in front of the
      // config. A bare config cannot be mistaken for this: its first field is the frame
      // length, which is never zero.
      off = 4;

    if (cookie.Length < off + Size)
      throw new ArgumentException("ALAC cookie is shorter than 24 bytes.", nameof(cookie));

    var s = cookie[off..];
    return new AlacCookie(
      FrameLength: BinaryPrimitives.ReadUInt32BigEndian(s),
      CompatibleVersion: s[4],
      BitDepth: s[5],
      Pb: s[6],
      Mb: s[7],
      Kb: s[8],
      NumChannels: s[9],
      MaxRun: BinaryPrimitives.ReadUInt16BigEndian(s[10..]),
      MaxFrameBytes: BinaryPrimitives.ReadUInt32BigEndian(s[12..]),
      AvgBitRate: BinaryPrimitives.ReadUInt32BigEndian(s[16..]),
      SampleRate: BinaryPrimitives.ReadUInt32BigEndian(s[20..]));
  }

  // True when a 4-byte box size at "offset" is followed by the given fourcc.
  private static bool HasAtomType(ReadOnlySpan<byte> cookie, int offset, ReadOnlySpan<byte> type)
    => cookie.Length >= offset + 8 && cookie.Slice(offset + 4, 4).SequenceEqual(type);

  /// <summary>Serialises the bare 24-byte config (big-endian).</summary>
  public byte[] Write() {
    var b = new byte[Size];
    var s = b.AsSpan();
    BinaryPrimitives.WriteUInt32BigEndian(s, this.FrameLength);
    s[4] = this.CompatibleVersion;
    s[5] = this.BitDepth;
    s[6] = this.Pb;
    s[7] = this.Mb;
    s[8] = this.Kb;
    s[9] = this.NumChannels;
    BinaryPrimitives.WriteUInt16BigEndian(s[10..], this.MaxRun);
    BinaryPrimitives.WriteUInt32BigEndian(s[12..], this.MaxFrameBytes);
    BinaryPrimitives.WriteUInt32BigEndian(s[16..], this.AvgBitRate);
    BinaryPrimitives.WriteUInt32BigEndian(s[20..], this.SampleRate);
    return b;
  }
}
