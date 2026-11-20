using System.Buffers.Binary;

namespace FileFormat.StuffItX;

/// <summary>
/// The x86 preprocessor of StuffIt X (preprocess id 2): the 32-bit operand after an E8 (call) or
/// E9 (jump) byte is stored as an absolute address, which the filter turns back into the relative
/// one, guided by a small history of recent E8/E9 positions.
/// </summary>
/// <remarks>
/// <para>
/// At an E8/E9 at output position p, the history is cleared when the last one lies more than five
/// bytes back, otherwise shifted once per byte of distance (keeping the masked bits 0x77). The
/// operand is converted only when its top byte is 00 or FF and the history allows it; the relative
/// address is the absolute one minus p + 6, folded again while the history says so, then cut to 25
/// bits and sign-extended. A converted operand clears the history, a refused one marks it.
/// </para>
/// <para>Converted from XADMaster's <c>XADStuffItXX86Handle.m</c> (LGPL-2.1-or-later, see
/// THIRD-PARTY-NOTICE.XADMaster.txt).</para>
/// </remarks>
internal static class StuffItXX86 {
  private static readonly bool[] Allowed = [true, true, true, false, true, false, false, false];
  private static readonly int[] Shifts = [24, 16, 8, 8, 0, 0, 0, 0];

  /// <summary>Undoes the filter on <paramref name="filtered"/>, producing at most <paramref name="length"/> bytes.</summary>
  public static byte[] Decode(byte[] filtered, long length) {
    var count = (int)Math.Min(filtered.Length, length);
    var output = filtered.AsSpan(0, count).ToArray();
    long lastHit = -6;
    var history = 0;
    Span<byte> operand = stackalloc byte[4];
    Span<byte> converted = stackalloc byte[4];
    for (var pos = 0; pos < count; ++pos) {
      var b = output[pos];
      if (b is not (0xE8 or 0xE9)) continue;
      var distance = pos - lastHit;
      lastHit = pos;
      if (distance > 5) history = 0;
      else for (var i = 0; i < distance; ++i) history = (history & 0x77) << 1;

      // the operand as stored, read past the end as zeros
      operand.Clear();
      for (var i = 0; i < 4; ++i) operand[i] = pos + 1 + i < filtered.Length ? filtered[pos + 1 + i] : (byte)0;
      if (operand[3] is not (0x00 or 0xFF)) {
        history |= 0x01;
        continue;
      }
      if (!Allowed[(history >> 1) & 7] || (history >> 1) > 0x0F) {
        history |= 0x11;
        continue;
      }

      var absolute = BinaryPrimitives.ReadInt32LittleEndian(operand);
      int relative;
      for (;;) {
        relative = absolute - pos - 6;
        if (history == 0) break;
        // (history >> 1) < 8 here: bit 4 can only come from bit 3, which the 0x77 mask clears
        var shift = Shifts[(history >> 1) & 7];
        var probe = (relative >> shift) & 0xFF;
        if (probe != 0 && probe != 0xFF) break;
        absolute = relative ^ ((1 << (shift + 8)) - 1);
      }
      relative &= 0x1FFFFFF;
      if (relative >= 0x1000000) relative |= unchecked((int)0xFF000000);

      converted.Clear();
      BinaryPrimitives.WriteInt32LittleEndian(converted, relative);
      for (var i = 0; i < 4 && pos + 1 + i < count; ++i) output[pos + 1 + i] = converted[i];
      history = 0;
      pos += 4; // the converted operand is not scanned again
    }
    return output;
  }
}
