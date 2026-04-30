#pragma warning disable CS1591
namespace FileFormat.Parquet;

/// <summary>
/// Read-only walker for Apache Parquet files. Validates the leading and trailing PAR1 magics,
/// reads the footer length, and walks the Thrift compact-encoded FileMetaData footer to extract
/// version, row count, schema element names, row-group count, and the created-by string.
/// Page-level decompression and full record decoding are intentionally out of scope.
/// </summary>
public sealed class ParquetReader {

  /// <summary>FileMetaData.version (Thrift field 1, i32). 0 if not present in the footer.</summary>
  public int Version { get; }

  /// <summary>FileMetaData.num_rows (Thrift field 3, i64). 0 if not present.</summary>
  public long NumRows { get; }

  /// <summary>Number of FileMetaData.row_groups list elements (Thrift field 4).</summary>
  public int NumRowGroups { get; }

  /// <summary>Names (SchemaElement.name, field 4) collected from FileMetaData.schema (field 2).</summary>
  public IReadOnlyList<string> Columns { get; }

  /// <summary>FileMetaData.created_by (Thrift field 6, string). Null if not present.</summary>
  public string? CreatedBy { get; }

  /// <summary>"full" if the Thrift footer was walked end-to-end without error; "partial" otherwise.</summary>
  public string ParseStatus { get; }

  public ParquetReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

    var fileSize = stream.Length;
    if (fileSize < ParquetConstants.MagicLength + ParquetConstants.TrailerLength)
      throw new InvalidDataException("File too small to be a Parquet file.");

    stream.Seek(0, SeekOrigin.Begin);
    var leading = ReadExact(stream, ParquetConstants.MagicLength);
    if (!leading.AsSpan().SequenceEqual(ParquetConstants.Magic))
      throw new InvalidDataException("Not a Parquet file (missing leading PAR1 magic).");

    stream.Seek(fileSize - ParquetConstants.MagicLength, SeekOrigin.Begin);
    var trailing = ReadExact(stream, ParquetConstants.MagicLength);
    if (!trailing.AsSpan().SequenceEqual(ParquetConstants.Magic))
      throw new InvalidDataException("Not a Parquet file (missing trailing PAR1 magic).");

    stream.Seek(fileSize - ParquetConstants.TrailerLength, SeekOrigin.Begin);
    Span<byte> lenBuf = stackalloc byte[4];
    var lenRead = 0;
    while (lenRead < 4) {
      var n = stream.Read(lenBuf[lenRead..]);
      if (n <= 0) throw new EndOfStreamException("EOF reading Parquet footer length.");
      lenRead += n;
    }
    var footerLength = (uint)(lenBuf[0] | (lenBuf[1] << 8) | (lenBuf[2] << 16) | (lenBuf[3] << 24));

    var footerStart = fileSize - ParquetConstants.TrailerLength - (long)footerLength;
    if (footerStart < ParquetConstants.MagicLength || footerLength == 0) {
      this.Version = 0;
      this.NumRows = 0;
      this.NumRowGroups = 0;
      this.Columns = Array.Empty<string>();
      this.CreatedBy = null;
      this.ParseStatus = "partial";
      return;
    }

    stream.Seek(footerStart, SeekOrigin.Begin);
    var footerBytes = ReadExact(stream, (int)footerLength);

    var version = 0;
    long numRows = 0;
    var numRowGroups = 0;
    var columns = new List<string>();
    string? createdBy = null;
    var status = "full";

    try {
      using var ms = new MemoryStream(footerBytes, writable: false);
      var prevId = 0;
      while (true) {
        var type = ThriftCompact.ReadFieldHeader(ms, ref prevId);
        if (type == ParquetConstants.TypeStop) break;
        switch (prevId) {
          case 1 when type == ParquetConstants.TypeI32:
            version = ThriftCompact.ReadVarInt(ms);
            break;
          case 2 when type == ParquetConstants.TypeList: {
            var (size, elemType) = ThriftCompact.ReadListHeader(ms);
            for (var i = 0; i < size; i++) {
              if (elemType == ParquetConstants.TypeStruct) {
                var name = ReadSchemaElementName(ms);
                if (name != null) columns.Add(name);
              } else {
                ThriftCompact.Skip(ms, elemType);
              }
            }
            break;
          }
          case 3 when type == ParquetConstants.TypeI64:
            numRows = ThriftCompact.ReadVarLong(ms);
            break;
          case 4 when type == ParquetConstants.TypeList: {
            var (size, elemType) = ThriftCompact.ReadListHeader(ms);
            numRowGroups = size;
            for (var i = 0; i < size; i++) ThriftCompact.Skip(ms, elemType);
            break;
          }
          case 6 when type == ParquetConstants.TypeBinary:
            createdBy = ThriftCompact.ReadBinary(ms);
            break;
          default:
            ThriftCompact.Skip(ms, type);
            break;
        }
      }
    } catch (EndOfStreamException) {
      status = "partial";
    } catch (InvalidDataException) {
      status = "partial";
    }

    this.Version = version;
    this.NumRows = numRows;
    this.NumRowGroups = numRowGroups;
    this.Columns = columns;
    this.CreatedBy = createdBy;
    this.ParseStatus = status;
  }

  private static string? ReadSchemaElementName(Stream stream) {
    string? name = null;
    var prevId = 0;
    while (true) {
      var type = ThriftCompact.ReadFieldHeader(stream, ref prevId);
      if (type == ParquetConstants.TypeStop) break;
      if (prevId == 4 && type == ParquetConstants.TypeBinary)
        name = ThriftCompact.ReadBinary(stream);
      else
        ThriftCompact.Skip(stream, type);
    }
    return name;
  }

  private static byte[] ReadExact(Stream stream, int count) {
    var buf = new byte[count];
    var read = 0;
    while (read < count) {
      var n = stream.Read(buf, read, count - read);
      if (n <= 0) throw new EndOfStreamException("Unexpected EOF reading Parquet container.");
      read += n;
    }
    return buf;
  }
}
