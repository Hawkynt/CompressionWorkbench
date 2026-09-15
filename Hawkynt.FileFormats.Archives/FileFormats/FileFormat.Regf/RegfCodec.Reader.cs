#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Structured;

namespace FileFormat.Regf;

/// <summary>Clean-room reader for Windows NT-family REGF registry hives.</summary>
internal static class RegfCodec {
  private const int HeaderSize = 4096;
  private const int CellBase = HeaderSize;
  private const uint NoOffset = 0xffffffff;
  private const uint InlineDataFlag = 0x80000000;
  private const int LargeValueThreshold = 16_344;

  public static StructuredNode Read(Stream stream) {
    var data = StructuredArchive.ReadAll(stream);
    if (data.Length < HeaderSize || !data.AsSpan(0, 4).SequenceEqual("regf"u8))
      throw new InvalidDataException("Not a Windows NT-family REGF registry hive.");

    var major = U32(data, 20);
    var minor = U32(data, 24);
    var fileType = U32(data, 28);
    var fileFormat = U32(data, 32);
    if (major != 1 || minor is < 1 or > 6)
      throw new InvalidDataException($"Unsupported REGF version {major}.{minor}.");
    if (fileType != 0)
      throw new InvalidDataException("REGF transaction-log files are not registry hives.");
    if (fileFormat != 1)
      throw new InvalidDataException($"Unsupported REGF file format {fileFormat}.");

    var hbinsSize = U32(data, 40);
    if (hbinsSize > int.MaxValue || HeaderSize + (long)hbinsSize > data.Length)
      throw new InvalidDataException("REGF hive-bin area is truncated.");

    if (hbinsSize != 0) {
      if (data.Length < HeaderSize + 32 || !data.AsSpan(HeaderSize, 4).SequenceEqual("hbin"u8))
        throw new InvalidDataException("REGF first hive-bin header is missing.");
    }

    var parser = new Parser(data, minor);
    return parser.ReadRoot(U32(data, 36));
  }

  private sealed class Parser(byte[] data, uint minorVersion) {
    private readonly HashSet<uint> _activeKeys = [];
    private readonly HashSet<uint> _activeIndexes = [];
    private int _keyCount;
    private int _valueCount;

    public StructuredNode ReadRoot(uint rootOffset) {
      if (rootOffset == NoOffset)
        throw new InvalidDataException("REGF hive has no root key.");
      var (_, root) = this.ReadKey(rootOffset, 0);
      return root;
    }

    private (string Name, StructuredNode Node) ReadKey(uint cellOffset, int depth) {
      if (depth > 256)
        throw new InvalidDataException("REGF key nesting exceeds the 256-level safety limit.");
      if (++this._keyCount > 1_000_000)
        throw new InvalidDataException("REGF hive exceeds the one-million-key safety limit.");
      if (!this._activeKeys.Add(cellOffset))
        throw new InvalidDataException("REGF key hierarchy contains a cycle.");

      try {
        var payload = this.Cell(cellOffset, "REGF nk cell");
        if (payload.Length < 76 || payload[0] != (byte)'n' || payload[1] != (byte)'k')
          throw new InvalidDataException("REGF key cell is not an nk record.");

        var flags = ReadU16(payload, 2);
        var subkeyCount = ReadU32(payload, 20);
        var volatileSubkeyCount = ReadU32(payload, 24);
        var subkeyListOffset = ReadU32(payload, 28);
        var volatileSubkeyListOffset = ReadU32(payload, 32);
        var valueCount = ReadU32(payload, 36);
        var valueListOffset = ReadU32(payload, 40);
        var nameLength = ReadU16(payload, 72);

        EnsureSpan(payload, 76, nameLength, "REGF key name");
        var compressedName = minorVersion != 1 && (flags & 0x20) != 0;
        var name = DecodeName(payload.Slice(76, nameLength), compressedName);
        var node = StructuredNode.Object("registry-key");

        this.ReadValues(node, valueCount, valueListOffset);
        this.ReadSubkeys(node, subkeyCount, subkeyListOffset, depth);
        this.ReadSubkeys(node, volatileSubkeyCount, volatileSubkeyListOffset, depth);
        return (name, node);
      } finally {
        this._activeKeys.Remove(cellOffset);
      }
    }

    private void ReadValues(StructuredNode node, uint count, uint listOffset) {
      if (count == 0)
        return;
      if (count > 1_000_000 || this._valueCount + (long)count > 4_000_000)
        throw new InvalidDataException("REGF hive exceeds the value-count safety limit.");
      if (listOffset == NoOffset)
        throw new InvalidDataException("REGF key has values but no value list.");

      var list = this.Cell(listOffset, "REGF value list");
      var bytesNeeded = checked((long)count * 4);
      if (bytesNeeded > list.Length)
        throw new InvalidDataException("REGF value list is truncated.");

      for (var i = 0u; i < count; ++i) {
        var valueOffset = ReadU32(list, checked((int)i * 4));
        var (name, value) = this.ReadValue(valueOffset);
        node.Add(name, value);
        ++this._valueCount;
      }
    }

    private (string Name, StructuredNode Value) ReadValue(uint cellOffset) {
      var payload = this.Cell(cellOffset, "REGF vk cell");
      if (payload.Length < 20 || payload[0] != (byte)'v' || payload[1] != (byte)'k')
        throw new InvalidDataException("REGF value cell is not a vk record.");

      var nameLength = ReadU16(payload, 2);
      var rawDataSize = ReadU32(payload, 4);
      var dataOffset = ReadU32(payload, 8);
      var type = ReadU32(payload, 12);
      var flags = minorVersion == 1 ? (ushort)0 : ReadU16(payload, 16);
      EnsureSpan(payload, 20, nameLength, "REGF value name");

      var compressedName = minorVersion != 1 && (flags & 0x0001) != 0;
      var name = nameLength == 0 ? "@" : DecodeName(payload.Slice(20, nameLength), compressedName);
      var dataSize = rawDataSize & ~InlineDataFlag;
      if (dataSize > 256 * 1024 * 1024u)
        throw new InvalidDataException("REGF value exceeds the 256 MiB safety limit.");

      byte[] bytes;
      if ((rawDataSize & InlineDataFlag) != 0) {
        if (dataSize > 4)
          throw new InvalidDataException("REGF inline value is larger than four bytes.");
        bytes = payload.Slice(8, checked((int)dataSize)).ToArray();
      } else if (dataSize == 0) {
        bytes = [];
      } else {
        bytes = this.ReadExternalValueData(dataOffset, checked((int)dataSize));
      }

      return (name, RegistryValue(type, bytes));
    }

    private byte[] ReadExternalValueData(uint dataOffset, int dataSize) {
      if (dataOffset == NoOffset)
        throw new InvalidDataException("REGF value has data but no data-cell offset.");

      var payload = this.Cell(dataOffset, "REGF value-data cell");
      if (minorVersion >= 4 && dataSize > LargeValueThreshold && payload.Length >= 8
          && payload[0] == (byte)'d' && payload[1] == (byte)'b')
        return this.ReadBigData(payload, dataSize);

      if (payload.Length < dataSize)
        throw new InvalidDataException("REGF value-data cell is shorter than the declared value size.");
      return payload[..dataSize].ToArray();
    }

    private byte[] ReadBigData(ReadOnlySpan<byte> record, int dataSize) {
      var segmentCount = ReadU16(record, 2);
      var segmentListOffset = ReadU32(record, 4);
      if (segmentCount == 0 || segmentListOffset == NoOffset)
        throw new InvalidDataException("REGF db record has no data segments.");

      var list = this.Cell(segmentListOffset, "REGF big-data segment list");
      if ((long)segmentCount * 4 > list.Length)
        throw new InvalidDataException("REGF big-data segment list is truncated.");

      var result = new byte[dataSize];
      var written = 0;
      for (var i = 0; i < segmentCount && written < dataSize; ++i) {
        var segmentOffset = ReadU32(list, i * 4);
        var segment = this.Cell(segmentOffset, "REGF big-data segment");
        var take = Math.Min(segment.Length, dataSize - written);
        segment[..take].CopyTo(result.AsSpan(written));
        written += take;
      }

      if (written != dataSize)
        throw new InvalidDataException("REGF big-data segments do not cover the declared value size.");
      return result;
    }

    private void ReadSubkeys(StructuredNode node, uint declaredCount, uint listOffset, int depth) {
      if (declaredCount == 0)
        return;
      if (declaredCount > 1_000_000)
        throw new InvalidDataException("REGF key declares too many subkeys.");
      if (listOffset == NoOffset)
        throw new InvalidDataException("REGF key has subkeys but no subkey list.");

      var offsets = new List<uint>(checked((int)Math.Min(declaredCount, 1_000_000)));
      this.CollectSubkeyOffsets(listOffset, offsets, declaredCount);
      if (offsets.Count < declaredCount)
        throw new InvalidDataException("REGF subkey index contains fewer keys than declared.");

      foreach (var childOffset in offsets.Take(checked((int)declaredCount))) {
        var (name, child) = this.ReadKey(childOffset, depth + 1);
        node.Add(name, child);
      }
    }

    private void CollectSubkeyOffsets(uint listOffset, List<uint> output, uint limit) {
      if (output.Count >= limit)
        return;
      if (!this._activeIndexes.Add(listOffset))
        throw new InvalidDataException("REGF subkey-index hierarchy contains a cycle.");

      try {
        var payload = this.Cell(listOffset, "REGF subkey-list cell");
        if (payload.Length < 4)
          throw new InvalidDataException("REGF subkey-list cell is truncated.");

        var signature0 = payload[0];
        var signature1 = payload[1];
        var count = ReadU16(payload, 2);

        switch (signature0, signature1) {
          case ((byte)'l', (byte)'f') when minorVersion >= 3:
          case ((byte)'l', (byte)'h') when minorVersion >= 5: {
            if ((long)count * 8 + 4 > payload.Length)
              throw new InvalidDataException("REGF lf/lh subkey list is truncated.");
            for (var i = 0; i < count && output.Count < limit; ++i)
              output.Add(ReadU32(payload, 4 + i * 8));
            break;
          }

          case ((byte)'l', (byte)'i'): {
            if ((long)count * 4 + 4 > payload.Length)
              throw new InvalidDataException("REGF li subkey list is truncated.");
            for (var i = 0; i < count && output.Count < limit; ++i)
              output.Add(ReadU32(payload, 4 + i * 4));
            break;
          }

          case ((byte)'r', (byte)'i'): {
            if ((long)count * 4 + 4 > payload.Length)
              throw new InvalidDataException("REGF ri subkey list is truncated.");
            for (var i = 0; i < count && output.Count < limit; ++i)
              this.CollectSubkeyOffsets(ReadU32(payload, 4 + i * 4), output, limit);
            break;
          }

          case ((byte)'l', (byte)'f'):
            throw new InvalidDataException($"REGF {minorVersion} predates lf fast-leaf indexes.");
          case ((byte)'l', (byte)'h'):
            throw new InvalidDataException($"REGF {minorVersion} predates lh hash-leaf indexes.");
          default:
            throw new InvalidDataException($"Unsupported REGF subkey-list signature '{(char)signature0}{(char)signature1}'.");
        }
      } finally {
        this._activeIndexes.Remove(listOffset);
      }
    }

    private ReadOnlySpan<byte> Cell(uint relativeOffset, string what) {
      if (relativeOffset == NoOffset || relativeOffset > int.MaxValue)
        throw new InvalidDataException($"{what} offset is invalid.");

      var cellHeaderSize = minorVersion == 1 ? 8 : 4;
      var absolute = CellBase + (long)relativeOffset;
      if (absolute < CellBase || absolute + cellHeaderSize > data.Length)
        throw new InvalidDataException($"{what} offset is outside the hive.");

      var headerOffset = (int)absolute;
      var rawSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(headerOffset, 4));
      if (rawSize >= 0)
        throw new InvalidDataException($"{what} references an unallocated cell.");
      var size = -(long)rawSize;
      if (size < cellHeaderSize || absolute + size > data.Length)
        throw new InvalidDataException($"{what} cell size is out of bounds.");
      return data.AsSpan(headerOffset + cellHeaderSize, checked((int)size - cellHeaderSize));
    }
  }

  private static StructuredNode RegistryValue(uint type, ReadOnlySpan<byte> data) {
    try {
      return type switch {
        1 => StructuredNode.Text(StructuredNodeKind.String, DecodeUtf16(data), "REG_SZ"),
        2 => StructuredNode.Text(StructuredNodeKind.String, DecodeUtf16(data), "REG_EXPAND_SZ"),
        3 => StructuredNode.Binary(data, "REG_BINARY"),
        4 when data.Length == 4 => StructuredNode.Text(
          StructuredNodeKind.Number,
          $"0x{BinaryPrimitives.ReadUInt32LittleEndian(data):X8}",
          "REG_DWORD"),
        5 when data.Length == 4 => StructuredNode.Text(
          StructuredNodeKind.Number,
          $"0x{BinaryPrimitives.ReadUInt32BigEndian(data):X8}",
          "REG_DWORD_BIG_ENDIAN"),
        6 => StructuredNode.Text(StructuredNodeKind.String, DecodeUtf16(data), "REG_LINK"),
        7 => StructuredNode.Text(StructuredNodeKind.String, DecodeMultiString(data), "REG_MULTI_SZ"),
        11 when data.Length == 8 => StructuredNode.Text(
          StructuredNodeKind.Number,
          $"0x{BinaryPrimitives.ReadUInt64LittleEndian(data):X16}",
          "REG_QWORD"),
        _ => StructuredNode.Binary(data, RegistryTypeName(type)),
      };
    } catch (DecoderFallbackException ex) {
      throw new InvalidDataException("REGF string value contains invalid UTF-16 data.", ex);
    }
  }

  private static string RegistryTypeName(uint type) => type switch {
    0 => "REG_NONE",
    1 => "REG_SZ",
    2 => "REG_EXPAND_SZ",
    3 => "REG_BINARY",
    4 => "REG_DWORD",
    5 => "REG_DWORD_BIG_ENDIAN",
    6 => "REG_LINK",
    7 => "REG_MULTI_SZ",
    8 => "REG_RESOURCE_LIST",
    9 => "REG_FULL_RESOURCE_DESCRIPTOR",
    10 => "REG_RESOURCE_REQUIREMENTS_LIST",
    11 => "REG_QWORD",
    _ => $"REG_TYPE_{type}",
  };

  private static string DecodeUtf16(ReadOnlySpan<byte> value) {
    if ((value.Length & 1) != 0)
      throw new InvalidDataException("REGF UTF-16 value has an odd byte length.");
    return new UnicodeEncoding(false, false, true).GetString(value).TrimEnd('\0');
  }

  private static string DecodeMultiString(ReadOnlySpan<byte> value)
    => string.Join('\n', DecodeUtf16(value).Split('\0', StringSplitOptions.RemoveEmptyEntries));

  private static string DecodeName(ReadOnlySpan<byte> bytes, bool compressed) {
    if (compressed)
      return Encoding.Latin1.GetString(bytes);
    if ((bytes.Length & 1) != 0)
      throw new InvalidDataException("REGF UTF-16 name has an odd byte length.");
    try {
      return new UnicodeEncoding(false, false, true).GetString(bytes);
    } catch (DecoderFallbackException ex) {
      throw new InvalidDataException("REGF name contains invalid UTF-16 data.", ex);
    }
  }

  private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) {
    EnsureSpan(data, offset, 2, "REGF UInt16");
    return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
  }

  private static uint ReadU32(ReadOnlySpan<byte> data, int offset) {
    EnsureSpan(data, offset, 4, "REGF UInt32");
    return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
  }

  private static uint U32(byte[] data, int offset) {
    if (offset < 0 || offset > data.Length - 4)
      throw new InvalidDataException("REGF header is truncated.");
    return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
  }

  private static void EnsureSpan(ReadOnlySpan<byte> data, int offset, int length, string what) {
    if (offset < 0 || length < 0 || offset > data.Length - length)
      throw new InvalidDataException($"{what} is truncated.");
  }
}
