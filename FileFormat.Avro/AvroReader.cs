#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Avro;

/// <summary>
/// Read-only walker for Apache Avro Object Container Files.
/// Parses the header (magic + meta map + sync marker) and walks block headers to count
/// blocks/records. Records themselves are not decoded — that requires the JSON schema.
/// </summary>
public sealed class AvroReader {

  public string Schema { get; }
  public string Codec { get; }
  public byte[] SyncMarker { get; }
  public int BlockCount { get; }
  public long RecordCount { get; }

  /// <summary>"partial" if the file structure was walked successfully; "corrupt" if a block sync marker mismatched or a structural error was encountered partway through.</summary>
  public string ParseStatus { get; }

  public AvroReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

    stream.Seek(0, SeekOrigin.Begin);
    var magic = ReadExact(stream, AvroConstants.MagicLength);
    if (!magic.AsSpan().SequenceEqual(AvroConstants.Magic))
      throw new InvalidDataException("Not an Avro Object Container File (bad magic).");

    var meta = ReadMetaMap(stream);
    Schema = meta.TryGetValue(AvroConstants.MetaKeySchema, out var schemaBytes)
      ? Encoding.UTF8.GetString(schemaBytes)
      : string.Empty;
    Codec = meta.TryGetValue(AvroConstants.MetaKeyCodec, out var codecBytes)
      ? Encoding.UTF8.GetString(codecBytes)
      : AvroConstants.DefaultCodec;

    SyncMarker = ReadExact(stream, AvroConstants.SyncMarkerLength);

    var status = "partial";
    var blocks = 0;
    long records = 0;
    while (stream.Position < stream.Length) {
      long count;
      long size;
      try {
        count = AvroVarLong.ReadLong(stream);
        size = AvroVarLong.ReadLong(stream);
      } catch (EndOfStreamException) {
        status = "corrupt";
        break;
      } catch (InvalidDataException) {
        status = "corrupt";
        break;
      }
      if (count < 0 || size < 0) {
        status = "corrupt";
        break;
      }
      if (size > stream.Length - stream.Position - AvroConstants.SyncMarkerLength) {
        status = "corrupt";
        break;
      }
      stream.Seek(size, SeekOrigin.Current);
      var trailingSync = ReadExactOrNull(stream, AvroConstants.SyncMarkerLength);
      if (trailingSync == null || !trailingSync.AsSpan().SequenceEqual(SyncMarker)) {
        status = "corrupt";
        break;
      }
      blocks++;
      records += count;
    }

    BlockCount = blocks;
    RecordCount = records;
    ParseStatus = status;
  }

  private static Dictionary<string, byte[]> ReadMetaMap(Stream stream) {
    var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    while (true) {
      var blockCount = AvroVarLong.ReadLong(stream);
      if (blockCount == 0) break;
      if (blockCount < 0) {
        blockCount = -blockCount;
        _ = AvroVarLong.ReadLong(stream);
      }
      for (var i = 0L; i < blockCount; i++) {
        var key = ReadString(stream);
        var value = ReadBytes(stream);
        map[key] = value;
      }
    }
    return map;
  }

  private static string ReadString(Stream stream) {
    var bytes = ReadBytes(stream);
    return Encoding.UTF8.GetString(bytes);
  }

  private static byte[] ReadBytes(Stream stream) {
    var len = AvroVarLong.ReadLong(stream);
    if (len < 0) throw new InvalidDataException("Negative length for Avro string/bytes.");
    if (len == 0) return [];
    return ReadExact(stream, (int)len);
  }

  private static byte[] ReadExact(Stream stream, int count) {
    var buf = new byte[count];
    var read = 0;
    while (read < count) {
      var n = stream.Read(buf, read, count - read);
      if (n <= 0) throw new EndOfStreamException("Unexpected EOF reading Avro container.");
      read += n;
    }
    return buf;
  }

  private static byte[]? ReadExactOrNull(Stream stream, int count) {
    var buf = new byte[count];
    var read = 0;
    while (read < count) {
      var n = stream.Read(buf, read, count - read);
      if (n <= 0) return null;
      read += n;
    }
    return buf;
  }
}
