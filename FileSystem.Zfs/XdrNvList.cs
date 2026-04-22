#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Zfs;

/// <summary>
/// Minimal XDR-encoded nvlist writer/reader compatible with Solaris <c>libnvpair</c> as
/// used by ZFS vdev labels. We emit the <see cref="EncodingXdr"/> / <see cref="BigEndian"/>
/// variant — the canonical choice for vdev labels per
/// <c>include/sys/nvpair.h</c> and the on-disk-format doc.
/// <para>
/// Wire layout:
/// <code>
/// Header:   1 byte encoding (0x01 = XDR)
///           1 byte endian   (0x00 = BE, 0x01 = LE)
///           2 bytes reserved (zero)
///           4 bytes version  (big-endian uint32, 0 = NV_VERSION)
///           4 bytes flags    (big-endian uint32, 1 = NV_UNIQUE_NAME)
///
/// NvPair:   4 bytes encoded_size (BE, total size of this nvpair including these 4 bytes)
///           4 bytes decoded_size (BE, in-memory size — we copy encoded_size)
///           4 bytes name_length  (BE, excluding NUL)
///           name + padding to 4 bytes
///           4 bytes type         (BE, DATA_TYPE_*)
///           4 bytes n_elem       (BE, usually 1)
///           value (type-dependent)
///
/// Terminator: 8 zero bytes (encoded_size=0, decoded_size=0).
/// </code>
/// </para>
/// </summary>
internal static class XdrNvList {
  public const byte EncodingXdr = 0x01;
  public const byte BigEndian = 0x00;
  public const uint NvVersion = 0;
  public const uint NvUniqueName = 1;

  public enum DataType : uint {
    Boolean = 1,
    Byte = 2,
    Int16 = 3,
    UInt16 = 4,
    Int32 = 5,
    UInt32 = 6,
    Int64 = 7,
    UInt64 = 8,
    String = 9,
    ByteArray = 10,
    NvList = 19,
    NvListArray = 20,
    StringArray = 16,
  }

  // ---------- Encoder ----------

  public sealed class NvList {
    public List<(string Name, DataType Type, object Value)> Pairs { get; } = new();

    public NvList AddUInt64(string name, ulong v) {
      this.Pairs.Add((name, DataType.UInt64, v));
      return this;
    }
    public NvList AddString(string name, string v) {
      this.Pairs.Add((name, DataType.String, v));
      return this;
    }
    public NvList AddNvList(string name, NvList v) {
      this.Pairs.Add((name, DataType.NvList, v));
      return this;
    }
  }

  public static byte[] Encode(NvList list) {
    using var ms = new MemoryStream();
    ms.WriteByte(EncodingXdr);
    ms.WriteByte(BigEndian);
    ms.WriteByte(0);
    ms.WriteByte(0);
    WriteU32Be(ms, NvVersion);
    WriteU32Be(ms, NvUniqueName);
    EncodeBody(ms, list);
    return ms.ToArray();
  }

  private static void EncodeBody(Stream s, NvList list) {
    foreach (var (name, type, value) in list.Pairs)
      EncodeNvPair(s, name, type, value);
    // terminator: two zero uint32s
    WriteU32Be(s, 0);
    WriteU32Be(s, 0);
  }

  private static void EncodeNvPair(Stream s, string name, DataType type, object value) {
    // First encode value to a scratch stream so we know sizes.
    using var valueBody = new MemoryStream();
    WriteU32Be(valueBody, (uint)type);
    WriteU32Be(valueBody, 1); // n_elem

    switch (type) {
      case DataType.UInt64:
        WriteU64Be(valueBody, (ulong)value);
        break;
      case DataType.String:
        WriteXdrString(valueBody, (string)value);
        break;
      case DataType.NvList: {
        // Nested nvlist: 4 bytes version + 4 bytes flags + body (no 4-byte header prefix).
        WriteU32Be(valueBody, NvVersion);
        WriteU32Be(valueBody, NvUniqueName);
        EncodeBody(valueBody, (NvList)value);
        break;
      }
      default:
        throw new NotSupportedException($"DataType {type} not supported.");
    }

    // Name block
    using var nameBlock = new MemoryStream();
    var nameBytes = Encoding.ASCII.GetBytes(name);
    WriteU32Be(nameBlock, (uint)nameBytes.Length);
    nameBlock.Write(nameBytes);
    PadTo4(nameBlock);

    var nameLen = (int)nameBlock.Length;
    var valLen = (int)valueBody.Length;
    var encodedSize = 8 + nameLen + valLen; // 4 encoded + 4 decoded + name + value
    WriteU32Be(s, (uint)encodedSize);
    WriteU32Be(s, (uint)encodedSize); // decoded_size — match encoded for our purposes
    nameBlock.Position = 0;
    nameBlock.CopyTo(s);
    valueBody.Position = 0;
    valueBody.CopyTo(s);
  }

  private static void WriteXdrString(Stream s, string v) {
    var bytes = Encoding.UTF8.GetBytes(v);
    WriteU32Be(s, (uint)bytes.Length);
    s.Write(bytes);
    PadTo4(s);
  }

  private static void WriteU32Be(Stream s, uint v) {
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(b, v);
    s.Write(b);
  }

  private static void WriteU64Be(Stream s, ulong v) {
    Span<byte> b = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(b, v);
    s.Write(b);
  }

  private static void PadTo4(Stream s) {
    var rem = (int)(s.Position & 3);
    if (rem == 0) return;
    for (var i = rem; i < 4; i++) s.WriteByte(0);
  }

  // ---------- Decoder ----------

  public static NvList Decode(ReadOnlySpan<byte> raw) {
    if (raw.Length < 12) throw new InvalidDataException("NvList too short.");
    if (raw[0] != EncodingXdr) throw new InvalidDataException("Unsupported NvList encoding.");
    if (raw[1] != BigEndian) throw new InvalidDataException("Unsupported NvList endian.");
    var pos = 4;
    var version = BinaryPrimitives.ReadUInt32BigEndian(raw.Slice(pos, 4)); pos += 4;
    if (version != NvVersion) throw new InvalidDataException("Unsupported NvList version.");
    pos += 4; // flags
    return DecodeBody(raw, ref pos);
  }

  private static NvList DecodeBody(ReadOnlySpan<byte> raw, ref int pos) {
    var list = new NvList();
    while (true) {
      if (pos + 8 > raw.Length) throw new InvalidDataException("Truncated NvList.");
      var encodedSize = BinaryPrimitives.ReadUInt32BigEndian(raw.Slice(pos, 4));
      // decoded_size at pos+4 (ignored)
      if (encodedSize == 0) {
        pos += 8;
        return list;
      }
      var startPos = pos;
      pos += 8;
      var nameLen = (int)BinaryPrimitives.ReadUInt32BigEndian(raw.Slice(pos, 4));
      pos += 4;
      var name = Encoding.ASCII.GetString(raw.Slice(pos, nameLen));
      pos += nameLen;
      // pad to 4
      while ((pos & 3) != 0) pos++;
      var type = (DataType)BinaryPrimitives.ReadUInt32BigEndian(raw.Slice(pos, 4));
      pos += 4;
      pos += 4; // n_elem

      object value = type switch {
        DataType.UInt64 => DecodeU64(raw, ref pos),
        DataType.String => DecodeString(raw, ref pos),
        DataType.NvList => DecodeNested(raw, ref pos),
        _ => throw new InvalidDataException($"Unsupported NvList type {type}."),
      };
      list.Pairs.Add((name, type, value));

      // Skip any remaining bytes to end of pair
      pos = startPos + (int)encodedSize;
    }
  }

  private static ulong DecodeU64(ReadOnlySpan<byte> raw, ref int pos) {
    var v = BinaryPrimitives.ReadUInt64BigEndian(raw.Slice(pos, 8));
    pos += 8;
    return v;
  }

  private static string DecodeString(ReadOnlySpan<byte> raw, ref int pos) {
    var len = (int)BinaryPrimitives.ReadUInt32BigEndian(raw.Slice(pos, 4));
    pos += 4;
    var s = Encoding.UTF8.GetString(raw.Slice(pos, len));
    pos += len;
    while ((pos & 3) != 0) pos++;
    return s;
  }

  private static NvList DecodeNested(ReadOnlySpan<byte> raw, ref int pos) {
    pos += 4; // version
    pos += 4; // flags
    return DecodeBody(raw, ref pos);
  }
}
