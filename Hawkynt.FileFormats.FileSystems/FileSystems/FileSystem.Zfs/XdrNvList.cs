using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Zfs;

/// <summary>
/// Decoder for the XDR-encoded nvlists in ZFS vdev labels (<c>docs/ZFS-ON-DISK.md</c>,
/// "Devices and labels"). Types the reader has no use for are skipped through each
/// pair's encoded size rather than rejected.
/// </summary>
internal sealed class XdrNvList {
  private const byte EncodingXdr = 1;
  private const int TypeBoolean = 1;
  private const int TypeUInt64 = 8;
  private const int TypeString = 9;
  private const int TypeUInt64Array = 11;
  private const int TypeNvList = 19;
  private const int TypeNvListArray = 20;
  private const int MaxDepth = 16;

  private readonly Dictionary<string, object> _pairs = new(StringComparer.Ordinal);

  public IReadOnlyDictionary<string, object> Pairs => this._pairs;

  public ulong? UInt64(string name) => this._pairs.TryGetValue(name, out var v) && v is ulong u ? u : null;
  public string? String(string name) => this._pairs.TryGetValue(name, out var v) ? v as string : null;
  public XdrNvList? List(string name) => this._pairs.TryGetValue(name, out var v) ? v as XdrNvList : null;
  public XdrNvList[] Lists(string name) => this._pairs.TryGetValue(name, out var v) && v is XdrNvList[] a ? a : [];

  /// <summary>Decodes a packed nvlist starting with its 4-byte encoding header.</summary>
  public static XdrNvList Decode(ReadOnlySpan<byte> raw) {
    if (raw.Length < 12) throw new InvalidDataException("ZFS: nvlist too short.");
    if (raw[0] != EncodingXdr) throw new InvalidDataException($"ZFS: nvlist encoding {raw[0]} is not XDR.");
    var pos = 4;
    return DecodeList(raw, ref pos, 0);
  }

  private static XdrNvList DecodeList(ReadOnlySpan<byte> raw, ref int pos, int depth) {
    if (depth > MaxDepth) throw new InvalidDataException("ZFS: nvlist nested too deeply.");
    pos += 8; // version, flags
    var list = new XdrNvList();
    while (true) {
      var encodedSize = (int)ReadU32(raw, pos);
      if (encodedSize == 0) {
        pos += 8;
        return list;
      }
      if (encodedSize < 20 || pos + encodedSize > raw.Length)
        throw new InvalidDataException("ZFS: nvlist pair size out of range.");
      var end = pos + encodedSize;
      var at = pos + 8;
      var name = ReadString(raw, ref at);
      var type = (int)ReadU32(raw, at);
      var count = (int)ReadU32(raw, at + 4);
      at += 8;
      object? value = type switch {
        TypeBoolean => true,
        TypeUInt64 => ReadU64(raw, at),
        TypeString => ReadString(raw, ref at),
        TypeUInt64Array => ReadU64Array(raw, at, count),
        TypeNvList => DecodeList(raw, ref at, depth + 1),
        TypeNvListArray => DecodeLists(raw, ref at, count, depth + 1),
        _ => null,
      };
      if (value != null) list._pairs[name] = value;
      // Nested lists are decoded in sequence; every other pair is skipped by its size.
      pos = type is TypeNvList or TypeNvListArray ? Math.Max(at, end) : end;
    }
  }

  private static XdrNvList[] DecodeLists(ReadOnlySpan<byte> raw, ref int pos, int count, int depth) {
    if (count is < 0 or > 4096) throw new InvalidDataException("ZFS: nvlist array count out of range.");
    var lists = new XdrNvList[count];
    for (var i = 0; i < count; ++i)
      lists[i] = DecodeList(raw, ref pos, depth);
    return lists;
  }

  private static ulong[] ReadU64Array(ReadOnlySpan<byte> raw, int pos, int count) {
    if (count < 0 || pos + 8L * count > raw.Length) throw new InvalidDataException("ZFS: nvlist array truncated.");
    var values = new ulong[count];
    for (var i = 0; i < count; ++i) values[i] = ReadU64(raw, pos + 8 * i);
    return values;
  }

  private static string ReadString(ReadOnlySpan<byte> raw, ref int pos) {
    var length = (int)ReadU32(raw, pos);
    if (length < 0 || pos + 4 + length > raw.Length) throw new InvalidDataException("ZFS: nvlist string truncated.");
    var s = Encoding.UTF8.GetString(raw.Slice(pos + 4, length));
    pos += 4 + ((length + 3) & ~3);
    return s;
  }

  private static uint ReadU32(ReadOnlySpan<byte> raw, int pos) {
    if (pos < 0 || pos + 4 > raw.Length) throw new InvalidDataException("ZFS: nvlist truncated.");
    return BinaryPrimitives.ReadUInt32BigEndian(raw[pos..]);
  }

  private static ulong ReadU64(ReadOnlySpan<byte> raw, int pos) {
    if (pos < 0 || pos + 8 > raw.Length) throw new InvalidDataException("ZFS: nvlist truncated.");
    return BinaryPrimitives.ReadUInt64BigEndian(raw[pos..]);
  }
}
