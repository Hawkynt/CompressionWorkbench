using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Zfs;

/// <summary>
/// Encoder for the XDR-packed nvlists of ZFS labels and the MOS <c>config</c> object
/// (<c>docs/ZFS-ON-DISK.md</c>, "Writing": the 4-byte header, then version and flags, then
/// pairs of encoded size, decoded (native) size, name, type, element count and value, ended by
/// two zero words). Values are <see cref="ulong"/>, <see cref="string"/>, <see cref="NvList"/>,
/// <see cref="NvList"/>[] or <see langword="null"/> for a boolean flag.
/// </summary>
internal static class XdrNvListWriter {
  private const int TypeBoolean = 1, TypeUInt64 = 8, TypeString = 9, TypeNvList = 19, TypeNvListArray = 20;
  private const int NativePairHeader = 16;   // nvpair_t: size, name length, reserved, element count, type
  private const int NativeListSize = 24;     // nvlist_t
  private const uint UniqueNames = 1;        // NV_UNIQUE_NAME

  /// <summary>An ordered name → value list.</summary>
  internal sealed class NvList : List<(string Name, object? Value)> {
    public NvList Add(string name, object? value) {
      base.Add((name, value));
      return this;
    }
  }

  /// <summary>Encodes <paramref name="list"/> with the header that marks it as XDR, little-endian host.</summary>
  public static byte[] Pack(NvList list) {
    var body = new List<byte>(1024) { 1, 1, 0, 0 };
    EncodeList(list, body);
    return [.. body];
  }

  private static void EncodeList(NvList list, List<byte> output) {
    U32(output, 0);            // version
    U32(output, UniqueNames);  // flags
    foreach (var (name, value) in list) {
      var pair = new List<byte>();
      XdrString(pair, name);
      var (type, count) = value switch {
        null => (TypeBoolean, 0),
        ulong => (TypeUInt64, 1),
        string => (TypeString, 1),
        NvList => (TypeNvList, 1),
        NvList[] lists => (TypeNvListArray, lists.Length),
        _ => throw new ArgumentException($"unsupported nvlist value {value.GetType().Name}"),
      };
      U32(pair, (uint)type);
      U32(pair, (uint)count);
      switch (value) {
        case ulong u:
          U64(pair, u);
          break;
        case string s:
          XdrString(pair, s);
          break;
        case NvList nested:
          EncodeList(nested, pair);
          break;
        case NvList[] lists:
          foreach (var l in lists)
            EncodeList(l, pair);
          break;
      }
      U32(output, (uint)(pair.Count + 8));
      U32(output, (uint)DecodedSize(name, value));
      output.AddRange(pair);
    }
    U32(output, 0);
    U32(output, 0);
  }

  /// <summary>The in-memory size libnvpair gives the pair (aligned header and name, then the aligned value).</summary>
  private static int DecodedSize(string name, object? value) {
    var nameBytes = Encoding.UTF8.GetByteCount(name) + 1;
    var valueSize = value switch {
      null => 0,
      ulong => 8,
      string s => Encoding.UTF8.GetByteCount(s) + 1,
      NvList => NativeListSize,
      NvList[] lists => lists.Length * 8 + lists.Length * NativeListSize,
      _ => 0,
    };
    return Align8(NativePairHeader + nameBytes) + Align8(valueSize);
  }

  private static int Align8(int x) => (x + 7) & ~7;

  private static void XdrString(List<byte> output, string s) {
    var bytes = Encoding.UTF8.GetBytes(s);
    U32(output, (uint)bytes.Length);
    output.AddRange(bytes);
    for (var pad = (4 - bytes.Length % 4) % 4; pad > 0; --pad)
      output.Add(0);
  }

  private static void U32(List<byte> output, uint value) {
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(b, value);
    output.AddRange(b);
  }

  private static void U64(List<byte> output, ulong value) {
    Span<byte> b = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(b, value);
    output.AddRange(b);
  }
}
