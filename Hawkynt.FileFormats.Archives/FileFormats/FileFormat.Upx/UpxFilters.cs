#pragma warning disable CS1591
namespace FileFormat.Upx;

/// <summary>
/// Reverses the byte transforms UPX applies to a block before compressing it.
/// </summary>
/// <remarks>
/// <para>
/// The transforms exist to make machine code compress better. A relative
/// <c>CALL</c>/<c>JMP</c> displacement differs for every call site even when
/// they all target the same routine, so the packer rewrites the displacement
/// into the absolute target, which repeats and therefore compresses. Reversing
/// the transform is a prerequisite for getting the original bytes back.
/// </para>
/// <para>
/// The behaviour implemented here was derived by diffing decompressed blocks
/// against their known-good originals across the ELF sample corpus, not from
/// any packer source.
/// </para>
/// </remarks>
public static class UpxFilters {

  /// <summary>x86 relative call/jump conversion keyed by a marker byte.</summary>
  public const byte CallTrickWithMarker = 0x49;

  /// <summary>
  /// Reverses <paramref name="filterId"/> over <paramref name="data"/> in place.
  /// Returns <see langword="false"/> and fills <paramref name="error"/> when the
  /// filter is one we cannot undo, so callers can surface the block untouched
  /// rather than silently hand back wrong bytes.
  /// </summary>
  public static bool TryReverse(byte[] data, byte filterId, byte filterCto, out string? error) {
    error = null;
    switch (filterId) {
      case 0:
        return true;
      case CallTrickWithMarker:
        ReverseCallTrick(data, filterCto);
        return true;
      default:
        error = $"UPX filter 0x{filterId:X2} has no managed reversal; the block cannot be restored to its original bytes.";
        return false;
    }
  }

  /// <summary>
  /// Undoes filter <c>0x49</c>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The filter walks the block for instructions carrying a 32-bit relative
  /// displacement — <c>E8</c> (CALL rel32), <c>E9</c> (JMP rel32) and the
  /// two-byte <c>0F 80…0F 8F</c> (Jcc rel32) — and replaces the displacement
  /// with the absolute in-block target of the branch, stored big-endian. The
  /// most significant byte of that big-endian word is forced to
  /// <paramref name="marker"/>, which is what marks a word as converted: on
  /// the way back, a displacement whose first byte is not the marker is left
  /// alone.
  /// </para>
  /// <para>
  /// The target is measured from the first byte of the displacement field
  /// rather than from the end of the instruction, so the displacement is
  /// recovered as <c>target - offsetOfDisplacement</c>. Only the low 24 bits of
  /// the stored word carry the target; the marker occupies the top 8.
  /// </para>
  /// </remarks>
  private static void ReverseCallTrick(byte[] data, byte marker) {
    var length = data.Length;
    var i = 0;
    while (i + 5 <= length) {
      int displacement;
      if (data[i] is 0xE8 or 0xE9)
        displacement = i + 1;
      else if (data[i] == 0x0F && data[i + 1] >= 0x80 && data[i + 1] <= 0x8F && i + 6 <= length)
        displacement = i + 2;
      else {
        ++i;
        continue;
      }

      if (data[displacement] != marker) {
        ++i;
        continue;
      }

      var target = (data[displacement + 1] << 16) | (data[displacement + 2] << 8) | data[displacement + 3];
      var relative = (uint)(target - displacement);
      data[displacement] = (byte)relative;
      data[displacement + 1] = (byte)(relative >> 8);
      data[displacement + 2] = (byte)(relative >> 16);
      data[displacement + 3] = (byte)(relative >> 24);
      i = displacement + 4;
    }
  }
}
