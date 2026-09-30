using System.Buffers.Binary;
using System.Numerics;

namespace FileFormat.Dar;

/// <summary>
/// The slice-level header every DAR slice file starts with, plus the one-byte slice trailer
/// archive format 8 (dar 2.4.0) added.
/// </summary>
/// <remarks>
/// <para>Layout, from the DAR format description (darbinding.sourceforge.net/specs/dar3.html,
/// "Slice Header" and "Infinint Format") and dar's own internals notes
/// (dar.sourceforge.io/doc/Notes.html, "Slice level"):</para>
/// <code>
/// magic u32 BE (123) | internal name 10 bytes | flag 1 byte | extension 1 byte | extension data
/// </code>
/// <list type="bullet">
///   <item><description>flag: <c>'T'</c> last slice, <c>'N'</c> another follows, <c>'E'</c> the answer
///   is in the slice trailer (the last byte of the file, <c>'T'</c> or <c>'N'</c>) — a slice being
///   written cannot know it is the last.</description></item>
///   <item><description>extension: <c>'N'</c> none; <c>'S'</c> one infinint, the size of the slices
///   after the first (pre-format-8); <c>'T'</c> an infinint count of TLVs, each a u16 BE type, an
///   infinint length and that many value bytes.</description></item>
///   <item><description>infinint: N zero bytes, then one byte with exactly one bit set; bit 7 means one
///   4-byte group, bit 6 two, down to bit 0 for eight, and every zero byte adds eight more groups. The
///   value follows big-endian in that many groups.</description></item>
/// </list>
/// <para>TLV types were measured on dar 2.8.6 output (<c>-s</c>, <c>-S</c> and neither):
/// 1 = slice size, 2 = first slice size (both infinints), 3 = the 10-byte data name.</para>
/// </remarks>
/// <param name="Magic">The leading big-endian magic.</param>
/// <param name="InternalName">The 10-byte identifier every slice of one archive shares.</param>
/// <param name="Flag">The header's last-slice flag: <c>'T'</c>, <c>'N'</c> or <c>'E'</c>.</param>
/// <param name="Extension">The extension flag: <c>'N'</c>, <c>'S'</c> or <c>'T'</c>.</param>
/// <param name="HeaderLength">Bytes the header occupies, extension included; <see langword="null"/> when unreadable.</param>
/// <param name="SliceSize">Size of every slice but the first, when recorded.</param>
/// <param name="FirstSliceSize">Size of the first slice, when recorded apart from the others.</param>
/// <param name="DataName">The 10-byte data name, when recorded.</param>
/// <param name="UnknownTlvTypes">TLV types this reader skipped.</param>
/// <param name="Trailer">The slice trailer, when the file ends in <c>'T'</c> or <c>'N'</c>.</param>
/// <param name="Problem">The first contradiction found, or <see langword="null"/>.</param>
public sealed record DarSliceHeader(
  uint Magic,
  byte[] InternalName,
  char Flag,
  char Extension,
  int? HeaderLength,
  ulong? SliceSize,
  ulong? FirstSliceSize,
  byte[]? DataName,
  IReadOnlyList<ushort> UnknownTlvTypes,
  char? Trailer,
  string? Problem) {

  /// <summary>The value every DAR slice starts with, big-endian.</summary>
  public const uint SliceMagic = 123;

  /// <summary>The fixed part of the header: magic, internal name, flag and extension.</summary>
  public const int FixedLength = 16;

  /// <summary>Length of the internal name and of the data name.</summary>
  public const int NameLength = 10;

  /// <summary>TLV type carrying the size of every slice but the first.</summary>
  public const ushort TlvSliceSize = 1;

  /// <summary>TLV type carrying the size of the first slice.</summary>
  public const ushort TlvFirstSliceSize = 2;

  /// <summary>TLV type carrying the data name.</summary>
  public const ushort TlvDataName = 3;

  /// <summary>Whether the header was read to its end without a contradiction.</summary>
  public bool IsValid => this.Problem == null;

  /// <summary>Whether this is the last slice of its archive; <see langword="null"/> when the header
  /// defers to a trailer that is missing or unreadable.</summary>
  public bool? IsLastSlice => this.Flag switch {
    'T' => true,
    'N' => false,
    'E' => this.Trailer switch { 'T' => true, 'N' => false, _ => null },
    _ => null,
  };

  /// <summary>
  /// Reads the header from <paramref name="head"/> (the first bytes of the slice) and the trailer
  /// from <paramref name="lastByte"/> (the file's final byte, or -1 when the file is empty). Never
  /// throws on malformed input: the first contradiction is reported in <see cref="Problem"/>.
  /// </summary>
  public static DarSliceHeader Parse(ReadOnlySpan<byte> head, int lastByte) {
    if (head.Length < FixedLength)
      return new(0, [], '\0', '\0', null, null, null, null, [], null,
        $"{head.Length} bytes is shorter than the {FixedLength}-byte slice header");

    var magic = BinaryPrimitives.ReadUInt32BigEndian(head);
    var internalName = head.Slice(4, NameLength).ToArray();
    var flag = (char)head[14];
    var extension = (char)head[15];
    char? trailer = lastByte is 'T' or 'N' ? (char)lastByte : null;
    ulong? sliceSize = null, firstSliceSize = null;
    byte[]? dataName = null;
    var unknown = new List<ushort>();

    DarSliceHeader Result(int? length, string? problem)
      => new(magic, internalName, flag, extension, length, sliceSize, firstSliceSize, dataName, unknown, trailer, problem);

    if (magic != SliceMagic)
      return Result(null, $"magic 0x{magic:X8} is not 0x{SliceMagic:X8}");
    if (flag is not ('T' or 'N' or 'E'))
      return Result(null, $"slice flag 0x{(byte)flag:X2} is none of 'T', 'N', 'E'");
    if (flag == 'E' && trailer == null)
      return Result(null, lastByte < 0
        ? "the header defers to a slice trailer the file does not have"
        : $"slice trailer 0x{lastByte:X2} is neither 'T' nor 'N'");

    var pos = FixedLength;
    switch (extension) {
      case 'N':
        return Result(pos, null);
      case 'S':
        if (!TryReadInfinint(head, ref pos, out var size))
          return Result(null, "the size extension's infinint is truncated or malformed");
        sliceSize = size;
        return Result(pos, null);
      case 'T':
        if (!TryReadInfinint(head, ref pos, out var count))
          return Result(null, "the TLV count is truncated or malformed");
        for (ulong i = 0; i < count; ++i) {
          if (pos + 2 > head.Length)
            return Result(null, $"TLV {i} of {count} is truncated before its type");
          var type = BinaryPrimitives.ReadUInt16BigEndian(head[pos..]);
          pos += 2;
          if (!TryReadInfinint(head, ref pos, out var length) || length > (ulong)(head.Length - pos))
            return Result(null, $"TLV {i} (type {type}) is truncated or its length is malformed");
          var value = head.Slice(pos, (int)length);
          pos += (int)length;
          switch (type) {
            case TlvSliceSize or TlvFirstSliceSize: {
              var at = 0;
              if (!TryReadInfinint(value, ref at, out var v) || at != value.Length)
                return Result(null, $"TLV {i} (type {type}) does not hold exactly one infinint");
              if (type == TlvSliceSize) sliceSize = v; else firstSliceSize = v;
              break;
            }
            case TlvDataName:
              dataName = value.ToArray();
              break;
            default:
              unknown.Add(type);
              break;
          }
        }
        return Result(pos, null);
      default:
        return Result(null, $"extension flag 0x{(byte)extension:X2} is none of 'N', 'S', 'T'");
    }
  }

  /// <summary>Reads one DAR infinint at <paramref name="pos"/>; fails on truncation, on a length byte
  /// without exactly one bit set, and on a value that does not fit 64 bits.</summary>
  internal static bool TryReadInfinint(ReadOnlySpan<byte> s, ref int pos, out ulong value) {
    value = 0;
    var p = pos;
    long zeros = 0;
    while (p < s.Length && s[p] == 0) {
      ++zeros;
      ++p;
    }
    if (p >= s.Length || BitOperations.PopCount(s[p]) != 1)
      return false;
    var groups = zeros * 8 + BitOperations.LeadingZeroCount((uint)s[p]) - 24 + 1;
    ++p;
    var bytes = groups * 4;
    if (bytes > s.Length - p)
      return false;
    for (var i = 0; i < bytes; ++i) {
      if ((value >> 56) != 0)
        return false;
      value = (value << 8) | s[p + i];
    }
    pos = p + (int)bytes;
    return true;
  }
}
