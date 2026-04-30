#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Arrow;

/// <summary>
/// Read-only walker for Apache Arrow IPC files (both File and Streaming variants).
/// Detects the format by leading magic, walks the message sequence counting messages
/// and record batches, and harvests an approximate schema by string-scanning the first
/// message's FlatBuffers metadata. Does not decode record data buffers.
/// </summary>
/// <remarks>
/// The schema extraction is heuristic — it scans the Schema message's FlatBuffers blob
/// for ASCII runs that are preceded by a 4-byte little-endian length matching the run's
/// length. This is the FlatBuffers wire format for inline strings, but the heuristic may
/// pick up incidental matches inside other tables. Missing names or false positives are
/// treated as a known limitation; callers should not depend on exact field counts. The
/// walker reads the FlatBuffers Message table's <c>bodyLength</c> field (field 4) and
/// <c>headerType</c> tag (field 2) directly via vtable lookup so it can skip body bytes
/// without depending on a third-party FlatBuffers runtime.
/// </remarks>
public sealed class ArrowReader {

  /// <summary>"file" if the file starts with the Arrow1 magic; "streaming" otherwise.</summary>
  public string Format { get; }

  /// <summary>Total number of FlatBuffers messages walked (Schema + RecordBatch + DictionaryBatch).</summary>
  public int MessageCount { get; }

  /// <summary>Number of RecordBatch messages found while walking the stream.</summary>
  public int RecordBatchCount { get; }

  /// <summary>Approximate column names harvested by string-scanning the Schema message's FlatBuffers metadata.</summary>
  public IReadOnlyList<string> ApproximateSchema { get; }

  /// <summary>"full" when the entire stream walked without errors, "partial" if any structural error was encountered partway through.</summary>
  public string ParseStatus { get; }

  public ArrowReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

    stream.Seek(0, SeekOrigin.Begin);

    var hasLeadingMagic = HasLeadingMagic(stream);
    Format = hasLeadingMagic ? "file" : "streaming";

    if (hasLeadingMagic && stream.Length < ArrowConstants.MagicLength * 2 + ArrowConstants.FooterLengthFieldLength)
      throw new InvalidDataException("Arrow IPC File too small to contain header + footer.");

    if (!hasLeadingMagic) {
      var firstByte = stream.ReadByte();
      stream.Seek(0, SeekOrigin.Begin);
      if (firstByte < 0)
        throw new InvalidDataException("Arrow IPC stream is empty.");
    }

    var dataStart = hasLeadingMagic ? (long)ArrowConstants.MagicLength : 0L;
    stream.Seek(dataStart, SeekOrigin.Begin);

    var status = "full";
    var messages = 0;
    var recordBatches = 0;
    string[] schemaColumns = [];

    while (true) {
      if (stream.Position >= stream.Length) break;

      // For the File format, stop walking once we reach the footer region. The footer is
      // located at file_length - 8 (trailing magic) - 4 (footer length) - footer_length.
      // We don't know footer_length until we read it; but messages always end with an
      // end-of-stream sentinel (continuation marker followed by length 0). When that
      // sentinel is consumed below the loop will break naturally.

      long messageStart = stream.Position;
      var (header, metadataBytes, bodyLength, isEos, isOk) = TryReadMessage(stream);
      if (!isOk) {
        status = "partial";
        break;
      }
      if (isEos) break;

      messages++;
      if (messages == 1 && header == ArrowConstants.MessageHeaderSchema && metadataBytes != null)
        schemaColumns = ExtractSchemaColumnNamesHeuristic(metadataBytes);
      if (header == ArrowConstants.MessageHeaderRecordBatch) recordBatches++;

      if (bodyLength < 0 || stream.Position + bodyLength > stream.Length) {
        status = "partial";
        break;
      }
      stream.Seek(bodyLength, SeekOrigin.Current);

      // 8-byte alignment between messages — skip padding bytes if present.
      var aligned = AlignUp(stream.Position, ArrowConstants.Alignment);
      if (aligned > stream.Length) break;
      stream.Seek(aligned, SeekOrigin.Begin);

      _ = messageStart;
    }

    MessageCount = messages;
    RecordBatchCount = recordBatches;
    ApproximateSchema = schemaColumns;
    ParseStatus = status;
  }

  private static bool HasLeadingMagic(Stream stream) {
    if (stream.Length < ArrowConstants.MagicLength) return false;
    Span<byte> buf = stackalloc byte[ArrowConstants.MagicLength];
    stream.Seek(0, SeekOrigin.Begin);
    var read = 0;
    while (read < buf.Length) {
      var n = stream.Read(buf[read..]);
      if (n <= 0) break;
      read += n;
    }
    stream.Seek(0, SeekOrigin.Begin);
    return read == ArrowConstants.MagicLength && buf.SequenceEqual(ArrowConstants.Magic);
  }

  private static (byte Header, byte[]? Metadata, long BodyLength, bool IsEndOfStream, bool IsOk) TryReadMessage(Stream stream) {
    Span<byte> u32 = stackalloc byte[4];
    if (!TryReadExact(stream, u32)) return (0, null, 0, false, false);
    var first = BinaryPrimitives.ReadUInt32LittleEndian(u32);

    int metadataLength;
    if (first == ArrowConstants.ContinuationMarker) {
      if (!TryReadExact(stream, u32)) return (0, null, 0, false, false);
      var len = BinaryPrimitives.ReadUInt32LittleEndian(u32);
      if (len == 0) return (0, null, 0, true, true);
      if (len > int.MaxValue) return (0, null, 0, false, false);
      metadataLength = (int)len;
    } else {
      // Older Arrow versions omit the continuation marker — `first` itself is the length.
      // A length of 0 still signals end-of-stream in legacy streams.
      if (first == 0) return (0, null, 0, true, true);
      if (first > int.MaxValue) return (0, null, 0, false, false);
      metadataLength = (int)first;
    }

    if (metadataLength <= 0 || stream.Position + metadataLength > stream.Length)
      return (0, null, 0, false, false);

    var metadata = new byte[metadataLength];
    if (!TryReadExact(stream, metadata)) return (0, null, 0, false, false);

    // Per Arrow IPC, the metadata block is padded to 8-byte alignment before the body begins.
    var afterMeta = stream.Position;
    var aligned = AlignUp(afterMeta, ArrowConstants.Alignment);
    if (aligned > stream.Length) return (0, null, 0, false, false);
    stream.Seek(aligned, SeekOrigin.Begin);

    var (header, bodyLength) = ReadMessageMetadata(metadata);
    return (header, metadata, bodyLength, false, true);
  }

  private static long AlignUp(long value, int alignment) {
    var mask = (long)alignment - 1;
    return (value + mask) & ~mask;
  }

  private static bool TryReadExact(Stream stream, Span<byte> buffer) {
    var read = 0;
    while (read < buffer.Length) {
      var n = stream.Read(buffer[read..]);
      if (n <= 0) return false;
      read += n;
    }
    return true;
  }

  /// <summary>
  /// Extracts the <c>headerType</c> tag (field 2, ubyte) and <c>bodyLength</c> (field 4, long)
  /// from the Arrow IPC <c>Message</c> FlatBuffers root table. Returns (0, 0) on parse failure.
  /// </summary>
  private static (byte Header, long BodyLength) ReadMessageMetadata(byte[] metadata) {
    try {
      if (metadata.Length < 4) return (0, 0);
      var rootOffset = BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(0, 4));
      if (rootOffset < 4 || rootOffset > metadata.Length - 4) return (0, 0);

      var tableStart = rootOffset;
      var vtableRel = BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(tableStart, 4));
      var vtableStart = tableStart - vtableRel;
      if (vtableStart < 0 || vtableStart + 4 > metadata.Length) return (0, 0);

      var vtableSize = BinaryPrimitives.ReadInt16LittleEndian(metadata.AsSpan(vtableStart, 2));
      if (vtableSize < 4 || vtableStart + vtableSize > metadata.Length) return (0, 0);

      // FlatBuffers vtable: [vtable_size:u16][object_size:u16][field0:u16][field1:u16]...
      // Message fields (vtable index): 0=version, 1=headerType (ubyte), 2=header (table),
      //                                 3=bodyLength (long), 4=customMetadata.
      byte header = 0;
      long bodyLength = 0;

      var headerTypeFieldOffset = ReadVtableEntry(metadata, vtableStart, vtableSize, fieldIndex: 1);
      if (headerTypeFieldOffset != 0 && tableStart + headerTypeFieldOffset < metadata.Length)
        header = metadata[tableStart + headerTypeFieldOffset];

      var bodyLengthFieldOffset = ReadVtableEntry(metadata, vtableStart, vtableSize, fieldIndex: 3);
      if (bodyLengthFieldOffset != 0 && tableStart + bodyLengthFieldOffset + 8 <= metadata.Length)
        bodyLength = BinaryPrimitives.ReadInt64LittleEndian(metadata.AsSpan(tableStart + bodyLengthFieldOffset, 8));

      if (bodyLength < 0) bodyLength = 0;
      return (header, bodyLength);
    } catch {
      return (0, 0);
    }
  }

  private static int ReadVtableEntry(byte[] metadata, int vtableStart, int vtableSize, int fieldIndex) {
    var entryOffset = vtableStart + 4 + fieldIndex * 2;
    if (entryOffset + 2 > vtableStart + vtableSize) return 0;
    if (entryOffset + 2 > metadata.Length) return 0;
    return BinaryPrimitives.ReadInt16LittleEndian(metadata.AsSpan(entryOffset, 2));
  }

  /// <summary>
  /// Heuristic column-name extraction. Scans <paramref name="schemaMetadata"/> for sequences
  /// of printable ASCII bytes preceded by a 4-byte little-endian length that matches the run's
  /// byte length — the canonical FlatBuffers inline-string encoding. Names shorter than 1 or
  /// longer than 256 bytes are ignored. The result is de-duplicated while preserving the order
  /// of first appearance.
  /// </summary>
  private static string[] ExtractSchemaColumnNamesHeuristic(byte[] schemaMetadata) {
    var found = new List<string>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    if (schemaMetadata.Length < 5) return [];

    for (var i = 0; i + 4 < schemaMetadata.Length; i++) {
      var len = BinaryPrimitives.ReadInt32LittleEndian(schemaMetadata.AsSpan(i, 4));
      if (len < 1 || len > 256) continue;
      if (i + 4 + len > schemaMetadata.Length) continue;

      var ok = true;
      for (var j = 0; j < len; j++) {
        var b = schemaMetadata[i + 4 + j];
        if (b < 0x20 || b > 0x7E) { ok = false; break; }
      }
      if (!ok) continue;

      var name = Encoding.ASCII.GetString(schemaMetadata, i + 4, len);
      if (LooksLikeIdentifier(name) && seen.Add(name))
        found.Add(name);
    }

    return found.ToArray();
  }

  private static bool LooksLikeIdentifier(string s) {
    if (s.Length == 0) return false;
    var first = s[0];
    if (!(char.IsLetter(first) || first == '_')) return false;
    foreach (var c in s) {
      if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-' || c == ' ')) return false;
    }
    return true;
  }
}
