#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Codec.Alac;

/// <summary>
/// The ALAC "magic cookie" (the codec-specific <c>ALACSpecificConfig</c> carried in
/// the <c>alac</c> sample-entry atom of an M4A file). All multi-byte fields are
/// big-endian. <see cref="Parse"/> tolerates an optional leading 4-byte
/// version/flags prefix that some QuickTime writers prepend; <see cref="Write"/>
/// emits the bare 24-byte config.
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
  /// Parses a magic cookie. A leading 4-byte version/flags prefix is auto-detected and
  /// skipped when present.
  /// </summary>
  public static AlacCookie Parse(ReadOnlySpan<byte> cookie) {
    var off = 0;
    if (cookie.Length >= 4 + Size) {
      var probe = BinaryPrimitives.ReadUInt32BigEndian(cookie[4..]);
      if (probe is >= 64 and <= 1u << 20)
        off = 4;
    }
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
