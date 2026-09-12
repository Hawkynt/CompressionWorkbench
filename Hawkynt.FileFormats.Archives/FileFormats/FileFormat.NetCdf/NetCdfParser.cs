#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.NetCdf;

/// <summary>
/// Parsed NetCDF classic header: numrecs, dimensions, variables, attributes.
/// </summary>
public sealed class NetCdfHeader {
  /// <summary>
  /// Gets or sets the version.
  /// </summary>
  public int Version { get; set; }
  /// <summary>
  /// Gets or sets the num recs.
  /// </summary>
  public long NumRecs { get; set; }
  /// <summary>
  /// Gets the dimensions.
  /// </summary>
  public List<NetCdfDimension> Dimensions { get; } = new();
  /// <summary>
  /// Gets the variables.
  /// </summary>
  public List<NetCdfVariable> Variables { get; } = new();
  /// <summary>
  /// Gets the global attributes.
  /// </summary>
  public List<NetCdfAttribute> GlobalAttributes { get; } = new();
}

/// <summary>
/// Represents a net cdf dimension.
/// </summary>
public sealed class NetCdfDimension {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
  public string Name { get; set; } = "";
  /// <summary>
  /// Gets or sets the length.
  /// </summary>
  public long Length { get; set; }
  /// <summary>
  /// Gets a value indicating whether is unlimited.
  /// </summary>
  public bool IsUnlimited { get; set; }
}

/// <summary>
/// Represents a net cdf attribute.
/// </summary>
public sealed class NetCdfAttribute {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
  public string Name { get; set; } = "";
  /// <summary>
  /// Gets or sets the nc type.
  /// </summary>
  public int NcType { get; set; }
  /// <summary>
  /// Gets or sets the value count.
  /// </summary>
  public long ValueCount { get; set; }
  /// <summary>
  /// Gets or sets the raw value.
  /// </summary>
  public byte[] RawValue { get; set; } = [];
}

/// <summary>
/// Represents a net cdf variable.
/// </summary>
public sealed class NetCdfVariable {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
  public string Name { get; set; } = "";
  /// <summary>
  /// Gets or sets the dim ids.
  /// </summary>
  public int[] DimIds { get; set; } = [];
  /// <summary>
  /// Gets or sets the nc type.
  /// </summary>
  public int NcType { get; set; }
  /// <summary>
  /// Gets or sets the vsize bytes.
  /// </summary>
  public long VsizeBytes { get; set; }
  /// <summary>
  /// Gets or sets the begin offset.
  /// </summary>
  public long BeginOffset { get; set; }
  /// <summary>
  /// Gets the attributes.
  /// </summary>
  public List<NetCdfAttribute> Attributes { get; } = new();
}

/// <summary>
/// Parses NetCDF Classic headers (CDF-1, CDF-2, CDF-5). Big-endian on-disk.
/// Does not decode variable data, only surfaces per-variable byte ranges.
/// </summary>
internal static class NetCdfParser {
  private const uint NC_DIMENSION = 0x0000000A;
  private const uint NC_VARIABLE  = 0x0000000B;
  private const uint NC_ATTRIBUTE = 0x0000000C;

  public static NetCdfHeader Parse(byte[] data) {
    if (data.Length < 8)
      throw new InvalidDataException("NetCDF header too short");
    if (data[0] != 'C' || data[1] != 'D' || data[2] != 'F')
      throw new InvalidDataException("Missing CDF magic");

    var header = new NetCdfHeader { Version = data[3] };
    var cdf5 = header.Version == 5;
    var offset64 = header.Version == 2;
    var pos = 4;

    // numrecs: int32 (or int64 for CDF-5). 0xFFFFFFFF = STREAMING.
    header.NumRecs = cdf5
      ? ReadInt64BE(data, ref pos)
      : ReadInt32BE(data, ref pos);

    // Three list sections (in order): dims, global atts, vars.
    ReadDimList(data, ref pos, header, cdf5);
    ReadAttList(data, ref pos, header.GlobalAttributes, cdf5);
    ReadVarList(data, ref pos, header, cdf5, offset64);

    return header;
  }

  private static void ReadDimList(byte[] data, ref int pos, NetCdfHeader header, bool cdf5) {
    var tag = (uint)ReadInt32BE(data, ref pos);
    var nelems = cdf5 ? ReadInt64BE(data, ref pos) : ReadInt32BE(data, ref pos);
    if (tag == 0 && nelems == 0) return;
    if (tag != NC_DIMENSION)
      throw new InvalidDataException($"Expected NC_DIMENSION tag, got 0x{tag:X8}");

    for (long i = 0; i < nelems; i++) {
      var name = ReadString(data, ref pos);
      var len = cdf5 ? ReadInt64BE(data, ref pos) : ReadInt32BE(data, ref pos);
      header.Dimensions.Add(new NetCdfDimension {
        Name = name,
        Length = len,
        IsUnlimited = len == 0,
      });
    }
  }

  private static void ReadAttList(byte[] data, ref int pos,
      List<NetCdfAttribute> target, bool cdf5) {
    var tag = (uint)ReadInt32BE(data, ref pos);
    var nelems = cdf5 ? ReadInt64BE(data, ref pos) : ReadInt32BE(data, ref pos);
    if (tag == 0 && nelems == 0) return;
    if (tag != NC_ATTRIBUTE)
      throw new InvalidDataException($"Expected NC_ATTRIBUTE tag, got 0x{tag:X8}");

    for (long i = 0; i < nelems; i++) {
      var name = ReadString(data, ref pos);
      var ncType = ReadInt32BE(data, ref pos);
      var count = cdf5 ? ReadInt64BE(data, ref pos) : ReadInt32BE(data, ref pos);
      var byteSize = TypeSize(ncType);
      var totalBytes = checked(byteSize * count);
      if (pos + totalBytes > data.Length)
        throw new InvalidDataException("Attribute value truncated");
      var raw = new byte[totalBytes];
      Array.Copy(data, pos, raw, 0, totalBytes);
      pos += (int)totalBytes;
      // Pad to 4-byte boundary.
      var pad = (int)((4 - totalBytes % 4) % 4);
      pos += pad;
      target.Add(new NetCdfAttribute {
        Name = name, NcType = ncType, ValueCount = count, RawValue = raw,
      });
    }
  }

  private static void ReadVarList(byte[] data, ref int pos,
      NetCdfHeader header, bool cdf5, bool offset64) {
    var tag = (uint)ReadInt32BE(data, ref pos);
    var nelems = cdf5 ? ReadInt64BE(data, ref pos) : ReadInt32BE(data, ref pos);
    if (tag == 0 && nelems == 0) return;
    if (tag != NC_VARIABLE)
      throw new InvalidDataException($"Expected NC_VARIABLE tag, got 0x{tag:X8}");

    for (long i = 0; i < nelems; i++) {
      var v = new NetCdfVariable();
      v.Name = ReadString(data, ref pos);
      var dimrank = cdf5 ? ReadInt64BE(data, ref pos) : ReadInt32BE(data, ref pos);
      if (dimrank < 0 || dimrank > 1024) throw new InvalidDataException("Bad dim rank");
      var dimIds = new int[dimrank];
      for (var d = 0; d < dimrank; d++)
        dimIds[d] = (int)(cdf5 ? ReadInt64BE(data, ref pos) : ReadInt32BE(data, ref pos));
      v.DimIds = dimIds;
      // vatt_list
      ReadAttList(data, ref pos, v.Attributes, cdf5);
      v.NcType = ReadInt32BE(data, ref pos);
      // vsize: int32 for v1/v2, int64 for v5
      v.VsizeBytes = cdf5 ? ReadInt64BE(data, ref pos) : (uint)ReadInt32BE(data, ref pos);
      // begin: int32 for v1, int64 for v2/v5
      v.BeginOffset = (cdf5 || offset64) ? ReadInt64BE(data, ref pos) : (uint)ReadInt32BE(data, ref pos);
      header.Variables.Add(v);
    }
  }

  // ── Low-level readers ───────────────────────────────────────────────────

  private static int ReadInt32BE(byte[] data, ref int pos) {
    if (pos + 4 > data.Length)
      throw new EndOfStreamException("Truncated NetCDF header");
    var v = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos, 4));
    pos += 4;
    return v;
  }

  private static long ReadInt64BE(byte[] data, ref int pos) {
    if (pos + 8 > data.Length)
      throw new EndOfStreamException("Truncated NetCDF header");
    var v = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(pos, 8));
    pos += 8;
    return v;
  }

  private static string ReadString(byte[] data, ref int pos) {
    var len = ReadInt32BE(data, ref pos);
    if (len < 0 || pos + len > data.Length)
      throw new InvalidDataException("Bad NetCDF string length");
    var s = Encoding.ASCII.GetString(data, pos, len);
    pos += len;
    var pad = (4 - len % 4) % 4;
    pos += pad;
    return s;
  }

  private static int TypeSize(int ncType) => ncType switch {
    1 => 1,  // NC_BYTE
    2 => 1,  // NC_CHAR
    3 => 2,  // NC_SHORT
    4 => 4,  // NC_INT
    5 => 4,  // NC_FLOAT
    6 => 8,  // NC_DOUBLE
    7 => 1,  // NC_UBYTE (CDF-5)
    8 => 2,  // NC_USHORT (CDF-5)
    9 => 4,  // NC_UINT (CDF-5)
    10 => 8, // NC_INT64 (CDF-5)
    11 => 8, // NC_UINT64 (CDF-5)
    _ => 1,
  };
}
