#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;

namespace FileFormat.FirmwareHex;

/// <summary>
/// Writers for the two firmware text formats, and the shared step that turns a
/// create/edit input list back into the <see cref="FirmwareImage"/> they encode.
/// </summary>
/// <remarks>
/// <para>Both formats are a flat address-to-bytes image written as ASCII records,
/// and both are fully specified, so writing them is a transcription rather than a
/// reconstruction. The reader normalises an image to a single <c>firmware.bin</c>
/// plus a rendered <c>metadata.ini</c>; the writer reads that pair back — the
/// payload is the bytes, the metadata supplies the base and start addresses the
/// flat binary cannot carry.</para>
/// </remarks>
public static class FirmwareHexWriter {

  /// <summary>The name the reader gives the flat payload.</summary>
  public const string PayloadName = "firmware.bin";

  /// <summary>The name the reader gives the rendered summary.</summary>
  public const string MetadataName = "metadata.ini";

  /// <summary>
  /// The image a create or edit describes: the single payload input as the bytes,
  /// and the addresses read out of <c>metadata.ini</c> when the caller passes the
  /// one the reader rendered. An input list with no payload describes an image
  /// with no data, which both formats can write.
  /// </summary>
  public static FirmwareImage ImageFrom(IReadOnlyList<ArchiveInputInfo> inputs, string sourceFormat) {
    ArgumentNullException.ThrowIfNull(inputs);

    byte[]? payload = null;
    var baseAddress = 0u;
    uint? startAddress = null;

    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var leaf = Path.GetFileName(input.ArchiveName);
      if (leaf.Equals(MetadataName, StringComparison.OrdinalIgnoreCase)) {
        ReadMetadata(input.ReadContent(), ref baseAddress, ref startAddress);
        continue;
      }
      // Any other single input is the payload, whatever it is called: a caller
      // converting a .bin into a .hex should not have to rename it first.
      payload ??= input.ReadContent();
    }

    var segments = payload is { Length: > 0 }
      ? new List<(uint, byte[])> { (baseAddress, payload) }
      : [];
    return new FirmwareImage(segments, startAddress, RecordCount: 0, GapCount: 0,
      TotalDataBytes: payload?.Length ?? 0, SourceFormat: sourceFormat);
  }

  /// <summary>Reads the two addresses the flat payload cannot carry out of a rendered summary.</summary>
  private static void ReadMetadata(byte[] metadata, ref uint baseAddress, ref uint? startAddress) {
    foreach (var raw in Encoding.UTF8.GetString(metadata).Split('\n')) {
      var line = raw.Trim();
      var equals = line.IndexOf('=', StringComparison.Ordinal);
      if (equals < 0) continue;
      var key = line[..equals].Trim();
      var value = line[(equals + 1)..].Trim();
      if (!value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;
      if (!uint.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)) continue;
      if (key.Equals("base_address", StringComparison.OrdinalIgnoreCase)) baseAddress = parsed;
      else if (key.Equals("start_address", StringComparison.OrdinalIgnoreCase)) startAddress = parsed;
    }
  }

  /// <summary>
  /// Writes <paramref name="image"/> as Intel HEX: type-04 extended-linear-address
  /// records whenever the high half of the address changes, type-00 data records of
  /// at most <paramref name="bytesPerRecord"/> bytes that never straddle a 64 KiB
  /// boundary, an optional type-05 start-linear-address record, and the type-01
  /// end-of-file record every reader requires.
  /// </summary>
  public static void WriteIntelHex(Stream output, FirmwareImage image, int bytesPerRecord = 16) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(image);
    ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerRecord, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(bytesPerRecord, 255);

    var text = new StringBuilder();
    var upper = -1L;
    foreach (var (segmentAddress, data) in image.Segments) {
      var offset = 0;
      while (offset < data.Length) {
        var address = segmentAddress + (uint)offset;
        var high = address >> 16;
        if (high != upper) {
          Record(text, 0, 0x04, [(byte)(high >> 8), (byte)high]);
          upper = high;
        }

        // A data record's address field is 16 bits wide, so a record that would
        // run past 0xFFFF is cut at the boundary and the next one re-bases.
        var toBoundary = (int)(0x10000 - (address & 0xFFFF));
        var count = Math.Min(Math.Min(bytesPerRecord, data.Length - offset), toBoundary);
        Record(text, (ushort)(address & 0xFFFF), 0x00, data.AsSpan(offset, count));
        offset += count;
      }
    }

    if (image.StartAddress is { } start)
      Record(text, 0, 0x05, [(byte)(start >> 24), (byte)(start >> 16), (byte)(start >> 8), (byte)start]);

    Record(text, 0, 0x01, []);
    var bytes = Encoding.ASCII.GetBytes(text.ToString());
    output.Write(bytes, 0, bytes.Length);
  }

  /// <summary>One <c>:LLAAAATT[DD…]CC</c> record, checksummed the way the reader checks it.</summary>
  private static void Record(StringBuilder text, ushort address, byte type, ReadOnlySpan<byte> data) {
    text.Append(':');
    Span<byte> header = [(byte)data.Length, (byte)(address >> 8), (byte)address, type];
    byte sum = 0;
    foreach (var b in header) { sum += b; text.Append(b.ToString("X2", CultureInfo.InvariantCulture)); }
    foreach (var b in data) { sum += b; text.Append(b.ToString("X2", CultureInfo.InvariantCulture)); }
    text.Append(((byte)((~sum + 1) & 0xFF)).ToString("X2", CultureInfo.InvariantCulture)).Append('\n');
  }

  /// <summary>
  /// Writes <paramref name="image"/> as TI-TXT: an <c>@AAAA</c> address line per
  /// segment, space-separated hex bytes at <paramref name="bytesPerLine"/> a line,
  /// and the single <c>q</c> the format ends with.
  /// </summary>
  public static void WriteTiTxt(Stream output, FirmwareImage image, int bytesPerLine = 16) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(image);
    ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerLine, 1);

    var text = new StringBuilder();
    foreach (var (address, data) in image.Segments) {
      text.Append('@').Append(address.ToString("X4", CultureInfo.InvariantCulture)).Append('\n');
      for (var offset = 0; offset < data.Length; offset += bytesPerLine) {
        var count = Math.Min(bytesPerLine, data.Length - offset);
        for (var i = 0; i < count; ++i) {
          if (i > 0) text.Append(' ');
          text.Append(data[offset + i].ToString("X2", CultureInfo.InvariantCulture));
        }
        text.Append('\n');
      }
    }

    // A TI-TXT file with no data still terminates; the reader requires the 'q'.
    text.Append("q\n");
    var bytes = Encoding.ASCII.GetBytes(text.ToString());
    output.Write(bytes, 0, bytes.Length);
  }
}
