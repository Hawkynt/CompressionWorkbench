#pragma warning disable CS1591
namespace FileFormat.Srec;

/// <summary>
/// A decoded Motorola S-record image: address-ordered, non-overlapping byte
/// runs plus the declared start address (S7/S8/S9 termination). Flattening fills
/// inter-segment gaps with a caller-chosen byte.
/// </summary>
public sealed record SrecImage(
  IReadOnlyList<(uint Address, byte[] Data)> Segments,
  uint? StartAddress,
  int RecordCount,
  int DataRecordCount,
  int TotalDataBytes
) {

  /// <summary>
  /// Flattens all segments into one contiguous binary from the lowest address to
  /// the end of the highest segment. Gaps are filled with <paramref name="fill"/>.
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

  /// <summary>Lowest address across all segments, or 0 when empty.</summary>
  public uint BaseAddress => this.Segments.Count == 0 ? 0u : this.Segments.Min(s => s.Address);
}
