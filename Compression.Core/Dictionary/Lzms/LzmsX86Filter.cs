using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Lzms;

/// <summary>
/// The x86 filter LZMS runs over every chunk.
/// </summary>
/// <remarks>
/// <para>A decoder applies this to whatever it decompresses, with nothing in the
/// stream to say so. A chunk that decodes to the payload verbatim is therefore
/// turned into something else underneath the writer, which is why x86 code has to
/// be filtered before it is compressed and not merely copied.</para>
///
/// <para>The transform is one line: a candidate instruction's thirty-two bit field
/// gains the instruction's own position, and the reverse direction subtracts it.
/// When it applies is the interesting part. The filter lies dormant until it sees
/// the same absolute target - the field plus the instruction's position - twice;
/// from then on it translates every candidate for the next 1023 bytes. Two
/// instructions sharing a target arm it and two sharing only a displacement do
/// not, which is what separates code from bytes that merely look like it.</para>
/// </remarks>
public static class LzmsX86Filter {
  private const int Window = 1023;

  /// <summary>
  /// A call keeps a shorter window than the rest. Measured, and it is the whole
  /// difference between reading a binary correctly and not: an e8 byte turns up
  /// by chance far more often than a REX prefix followed by 8d does, so the
  /// format trusts it for half as long.
  /// </summary>
  private const int WindowForCall = 511;
  private const int TailMargin = 16;
  private const int KeyMask = 0xFFFF;

  /// <summary>
  /// How far back a remembered target still counts. Measured: a repeat 65535
  /// bytes on arms the filter and one 65536 bytes on does not, which is what a
  /// table of sixteen-bit positions would do.
  /// </summary>
  private const int RememberedFor = 65535;

  /// <summary>
  /// Where an instruction's field starts, or -1 if this is not a candidate.
  /// </summary>
  /// <remarks>
  /// Measured one byte value at a time. The lea test is cruder than decoding an
  /// instruction: only the low three bits of the ModRM byte are looked at, so a
  /// form carrying an eight-bit displacement, or none at all, is still read as
  /// though it held a thirty-two bit field.
  /// </remarks>
  private static int FieldOffset(ReadOnlySpan<byte> data, int i) {
    var b = data[i];
    if (b is 0xE8 or 0xE9 && i + 5 <= data.Length) return i + 1;
    if (b == 0xFF && i + 6 <= data.Length && data[i + 1] == 0x15) return i + 2;

    // One literal three-byte sequence, and only this one: a lock-prefixed add to
    // a rip-relative dword. Neither the same instruction without the lock nor the
    // lock with any other ModRM byte counts.
    if (b == 0xF0 && i + 7 <= data.Length && data[i + 1] == 0x83 && data[i + 2] == 0x05) return i + 3;
    if (i + 7 > data.Length || (b != 0x48 && b != 0x4C)) return -1;

    var op = data[i + 1];
    var modrm = data[i + 2];
    if (op == 0x8D && (modrm & 7) == 5) return i + 3;
    if (op == 0x8B && b == 0x48 && (modrm & 0xF7) == 0x05) return i + 3;
    return -1;
  }

  /// <summary>Runs the filter, forwards before compressing or backwards after.</summary>
  public static byte[] Apply(ReadOnlySpan<byte> data, bool forward) {
    var x = data.ToArray();
    var lastSeen = new Dictionary<uint, int>();
    var armedUntil = -1;
    var armedUntilCall = -1;
    var i = 0;
    var limit = x.Length - TailMargin;
    while (i < limit) {
      var field = FieldOffset(x, i);
      if (field < 0) {
        ++i;
        continue;
      }

      // e9 is recognised and stepped over but never translated, so an
      // instruction lying inside its field is never examined.
      if (x[i] == 0xE9) {
        i = field + 4;
        continue;
      }

      var value = BinaryPrimitives.ReadUInt32LittleEndian(x.AsSpan(field));
      var until = x[i] == 0xE8 ? armedUntilCall : armedUntil;
      var translated = i < until;
      if (translated)
        BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(field),
          forward ? value + (uint)i : value - (uint)i);

      // The target is always taken from the unfiltered value, so both directions
      // arm at the same places. Reading it from the stored value instead works
      // going forwards and quietly disagrees coming back.
      var plain = translated && !forward ? value - (uint)i : value;
      var key = (plain + (uint)i) & KeyMask;
      if (lastSeen.TryGetValue(key, out var seenAt) && i - seenAt <= RememberedFor) {
        armedUntil = field + 4 + Window;
        armedUntilCall = field + 4 + WindowForCall;
      }

      lastSeen[key] = i;
      i = field + 4;
    }

    return x;
  }

}
