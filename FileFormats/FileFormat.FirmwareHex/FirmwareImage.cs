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
  /// Flattens all segments into a single contiguous binary spanning from the
  /// lowest address to the end of the highest segment. Gaps are filled with
  /// <paramref name="fill"/> (default <c>0xFF</c> to match flash erase state).
  /// </summary>
  public byte[] ToFlatBinary(byte fill = 0xFF) {
    if (this.Segments.Count == 0) return [];
    var lo = this.Segments.Min(s => s.Address);
    var hi = this.Segments.Max(s => s.Address + (uint)s.Data.Length);
    var buf = new byte[hi - lo];
    if (fill != 0) Array.Fill(buf, fill);
    foreach (var (addr, data) in this.Segments)
      Array.Copy(data, 0, buf, (int)(addr - lo), data.Length);
    return buf;
  }

  /// <summary>Returns the lowest address across all segments, or 0 when empty.</summary>
  public uint BaseAddress => this.Segments.Count == 0 ? 0u : this.Segments.Min(s => s.Address);
}
