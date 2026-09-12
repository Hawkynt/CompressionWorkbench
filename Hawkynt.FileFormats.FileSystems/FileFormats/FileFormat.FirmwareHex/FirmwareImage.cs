#pragma warning disable CS1591
namespace FileFormat.FirmwareHex;

/// <summary>
/// A decoded firmware image: an ordered collection of address/byte-run segments
/// plus a declared start address (when the source format supplies one — Intel
/// HEX type 03/05, S-Record S7/S8/S9). Segments are sorted by address and never
/// overlap; gaps between segments are filled with <c>0xFF</c> (flash erase
/// default) when the image is flattened to a single binary.
/// </summary>
public sealed record FirmwareImage(
  IReadOnlyList<(uint Address, byte[] Data)> Segments,
  uint? StartAddress,
  int RecordCount,
  int GapCount,
  int TotalDataBytes,
  string SourceFormat
) {

  /// <summary>
  /// Gets the original segmented x86 start address when an Intel HEX type-03
  /// record supplied one. Keeping CS:IP separately matters because many CS:IP
  /// pairs map to the same linear address and therefore cannot be reconstructed
  /// from <see cref="StartAddress"/> alone.
  /// </summary>
  public (ushort CodeSegment, ushort InstructionPointer)? StartSegmentAddress { get; init; }

  /// <summary>
  /// Flattens all segments into a single contiguous binary spanning from the
  /// lowest address to the end of the highest segment. Gaps are filled with
  /// <paramref name="fill"/> (default <c>0xFF</c> to match flash erase state).
  /// </summary>
  public byte[] ToFlatBinary(byte fill = 0xFF) {
    if (this.Segments.Count == 0) return [];
    var lo = this.Segments.Min(s => s.Address);
    var hi = this.Segments.Max(s => (ulong)s.Address + (uint)s.Data.Length);
    var length = hi - lo;
    if (length > int.MaxValue)
      throw new InvalidDataException(
        $"Firmware image spans {length} bytes and cannot be represented as one managed flat byte array.");

    var buf = new byte[(int)length];
    if (fill != 0) Array.Fill(buf, fill);
    foreach (var (addr, data) in this.Segments)
      Array.Copy(data, 0, buf, checked((int)((ulong)addr - lo)), data.Length);
    return buf;
  }

  /// <summary>Returns the lowest address across all segments, or 0 when empty.</summary>
  public uint BaseAddress => this.Segments.Count == 0 ? 0u : this.Segments.Min(s => s.Address);
}
