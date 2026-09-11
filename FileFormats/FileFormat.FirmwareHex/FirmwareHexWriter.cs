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
/// payload is the bytes, the metadata supplies the address map and start address
/// information the flat binary cannot carry.</para>
/// </remarks>
public static class FirmwareHexWriter {

  private readonly record struct SegmentLayout(uint Address, int Length);

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
    ushort? startSegmentCs = null;
    ushort? startSegmentIp = null;
    var segmentLayout = new List<SegmentLayout>();

    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var leaf = Path.GetFileName(input.ArchiveName);
      if (leaf.Equals(MetadataName, StringComparison.OrdinalIgnoreCase)) {
        ReadMetadata(input.ReadContent(), ref baseAddress, ref startAddress,
          segmentLayout, ref startSegmentCs, ref startSegmentIp);
        continue;
      }
      // Any other single input is the payload, whatever it is called: a caller
      // converting a .bin into a .hex should not have to rename it first.
      payload ??= input.ReadContent();
    }

    List<(uint Address, byte[] Data)> segments;
    if (payload is not { Length: > 0 })
      segments = [];
    else if (!TryRestoreSparseSegments(payload, baseAddress, segmentLayout, out segments))
      segments = [(baseAddress, payload)];

    var image = new FirmwareImage(segments, startAddress, RecordCount: 0,
      GapCount: Math.Max(0, segments.Count - 1),
      TotalDataBytes: segments.Sum(segment => segment.Data.Length),
      SourceFormat: sourceFormat);

    if (startAddress is { } linear && startSegmentCs is { } cs && startSegmentIp is { } ip
        && ((uint)cs << 4) + ip == linear)
      image = image with { StartSegmentAddress = (cs, ip) };

    return image;
  }

  /// <summary>
  /// Reads the addresses and segment map that a flat payload cannot carry out of
  /// a rendered summary. Unknown metadata is deliberately ignored so older and
  /// hand-written summaries remain accepted.
  /// </summary>
  private static void ReadMetadata(byte[] metadata, ref uint baseAddress, ref uint? startAddress,
      List<SegmentLayout> segmentLayout, ref ushort? startSegmentCs, ref ushort? startSegmentIp) {
    foreach (var raw in Encoding.UTF8.GetString(metadata).Split('\n')) {
      var line = raw.Trim();
      var equals = line.IndexOf('=', StringComparison.Ordinal);
      if (equals < 0) continue;
      var key = line[..equals].Trim();
      var value = line[(equals + 1)..].Trim();

      if (key.Equals("base_address", StringComparison.OrdinalIgnoreCase)) {
        if (TryParseHexUInt32(value, out var parsed)) baseAddress = parsed;
        continue;
      }
      if (key.Equals("start_address", StringComparison.OrdinalIgnoreCase)) {
        if (TryParseHexUInt32(value, out var parsed)) startAddress = parsed;
        continue;
      }
      if (key.Equals("start_segment_cs", StringComparison.OrdinalIgnoreCase)) {
        if (TryParseHexUInt16(value, out var parsed)) startSegmentCs = parsed;
        continue;
      }
      if (key.Equals("start_segment_ip", StringComparison.OrdinalIgnoreCase)) {
        if (TryParseHexUInt16(value, out var parsed)) startSegmentIp = parsed;
        continue;
      }
      if (key.StartsWith("segment_", StringComparison.OrdinalIgnoreCase)
          && TryParseSegmentLayout(value, out var segment))
        segmentLayout.Add(segment);
    }
  }

  private static bool TryParseHexUInt32(string text, out uint value) {
    value = 0;
    return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
      && uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
  }

  private static bool TryParseHexUInt16(string text, out ushort value) {
    value = 0;
    return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
      && ushort.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
  }

  private static bool TryParseSegmentLayout(string value, out SegmentLayout segment) {
    segment = default;
    const string separator = " .. ";
    var separatorIndex = value.IndexOf(separator, StringComparison.Ordinal);
    if (separatorIndex <= 0) return false;

    var startText = value[..separatorIndex].Trim();
    var remainder = value[(separatorIndex + separator.Length)..].Trim();
    var endTokenLength = remainder.IndexOf(' ');
    if (endTokenLength < 0) endTokenLength = remainder.Length;
    var endText = remainder[..endTokenLength];
    if (!TryParseHexUInt32(startText, out var start) || !TryParseHexUInt32(endText, out var end) || end < start)
      return false;

    var length = (ulong)end - start;
    if (length > int.MaxValue) return false;
    segment = new SegmentLayout(start, (int)length);
    return true;
  }

  /// <summary>
  /// Restores the sparse runs described by metadata from the reader's flat binary.
  /// The layout is used only when it spans the payload exactly; if callers replace
  /// <c>firmware.bin</c> with a differently sized binary, stale segment metadata is
  /// ignored and the replacement becomes one contiguous run at <paramref name="baseAddress"/>.
  /// </summary>
  private static bool TryRestoreSparseSegments(byte[] payload, uint baseAddress,
      IReadOnlyList<SegmentLayout> layout, out List<(uint Address, byte[] Data)> segments) {
    segments = [];
    if (layout.Count == 0) return false;

    var ordered = layout.OrderBy(segment => segment.Address).ToArray();
    if (ordered[0].Address != baseAddress) return false;

    ulong previousEnd = baseAddress;
    ulong highestEnd = baseAddress;
    foreach (var segment in ordered) {
      if (segment.Length < 0 || segment.Address < previousEnd) return false;
      var end = (ulong)segment.Address + (uint)segment.Length;
      if (end > 0x1_0000_0000UL) return false;
      previousEnd = end;
      highestEnd = Math.Max(highestEnd, end);
    }
    if (highestEnd - baseAddress != (ulong)payload.Length) return false;

    foreach (var segment in ordered) {
      var offset = (ulong)segment.Address - baseAddress;
      if (offset + (uint)segment.Length > (ulong)payload.Length) return false;
      segments.Add((segment.Address, payload.AsSpan((int)offset, segment.Length).ToArray()));
    }
    return true;
  }

  /// <summary>
  /// Writes <paramref name="image"/> as Intel HEX: type-04 extended-linear-address
  /// records whenever the high half of the address changes, type-00 data records of
  /// at most <paramref name="bytesPerRecord"/> bytes that never straddle a 64 KiB
  /// boundary, an optional type-03 or type-05 start-address record, and the type-01
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
      if ((ulong)segmentAddress + (uint)data.Length > 0x1_0000_0000UL)
        throw new InvalidDataException(
          $"IntelHex: segment at 0x{segmentAddress:X8} with {data.Length} bytes exceeds the 32-bit address space.");

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

    if (image.StartSegmentAddress is { } segmentedStart) {
      var (cs, ip) = segmentedStart;
      Record(text, 0, 0x03, [(byte)(cs >> 8), (byte)cs, (byte)(ip >> 8), (byte)ip]);
    } else if (image.StartAddress is { } start) {
      Record(text, 0, 0x05, [(byte)(start >> 24), (byte)(start >> 16), (byte)(start >> 8), (byte)start]);
    }

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
