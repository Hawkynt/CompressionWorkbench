#pragma warning disable CS1591
using System.Globalization;
using System.Text;

namespace FileFormat.Srec;

/// <summary>
/// Emits Motorola S-record text for a flat binary image. Picks the data-record
/// type from the address width (S1/S2/S3 for 2/3/4-byte addresses) — either
/// auto-selected from the highest address or pinned by the caller — and writes
/// the matching S9/S8/S7 termination record.
/// </summary>
public static class SrecWriter {

  private const int DefaultBytesPerRecord = 16;

  /// <summary>Encodes <paramref name="data"/> as an S-record document.</summary>
  /// <param name="data">Flat binary payload.</param>
  /// <param name="baseAddress">Load address of the first byte.</param>
  /// <param name="addressWidth">2, 3 or 4 bytes; 0 auto-selects the smallest fit.</param>
  /// <param name="header">Optional S0 header text (module name); defaults to empty.</param>
  /// <param name="startAddress">Execution start address written to the termination record.</param>
  /// <param name="bytesPerRecord">Data bytes per S1/S2/S3 line (1..250).</param>
  public static string Write(
      byte[] data,
      uint baseAddress = 0,
      int addressWidth = 0,
      string? header = null,
      uint startAddress = 0,
      int bytesPerRecord = DefaultBytesPerRecord) {
    ArgumentNullException.ThrowIfNull(data);
    if (bytesPerRecord is < 1 or > 250)
      throw new ArgumentOutOfRangeException(nameof(bytesPerRecord));

    var maxAddress = baseAddress + (data.Length == 0 ? 0u : (uint)data.Length - 1);
    var width = addressWidth == 0 ? AutoWidth(maxAddress) : addressWidth;
    if (width is < 2 or > 4)
      throw new ArgumentOutOfRangeException(nameof(addressWidth), "Address width must be 2, 3 or 4.");
    if (RequiredWidth(maxAddress) > width)
      throw new ArgumentException(
        $"Address 0x{maxAddress:X} does not fit in {width}-byte records.", nameof(addressWidth));

    var dataType = width switch { 2 => '1', 3 => '2', _ => '3' };
    var termType = width switch { 2 => '9', 3 => '8', _ => '7' };

    var sb = new StringBuilder();
    WriteRecord(sb, '0', 0, 2, Encoding.ASCII.GetBytes(header ?? string.Empty));

    for (var pos = 0; pos < data.Length; pos += bytesPerRecord) {
      var chunk = Math.Min(bytesPerRecord, data.Length - pos);
      WriteRecord(sb, dataType, baseAddress + (uint)pos, width, data.AsSpan(pos, chunk));
    }

    WriteRecord(sb, termType, startAddress, width, ReadOnlySpan<byte>.Empty);
    return sb.ToString();
  }

  private static void WriteRecord(StringBuilder sb, char type, uint address, int addrBytes, ReadOnlySpan<byte> payload) {
    var count = (byte)(addrBytes + payload.Length + 1); // address + data + checksum
    sb.Append('S').Append(type);
    sb.Append(count.ToString("X2", CultureInfo.InvariantCulture));

    byte sum = count;
    for (var i = addrBytes - 1; i >= 0; i--) {
      var b = (byte)(address >> (8 * i));
      sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
      sum += b;
    }
    foreach (var b in payload) {
      sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
      sum += b;
    }
    sb.Append(((byte)(~sum & 0xFF)).ToString("X2", CultureInfo.InvariantCulture));
    sb.Append("\r\n");
  }

  private static int AutoWidth(uint maxAddress) => RequiredWidth(maxAddress);

  private static int RequiredWidth(uint maxAddress)
    => maxAddress <= 0xFFFF ? 2 : maxAddress <= 0xFFFFFF ? 3 : 4;
}
