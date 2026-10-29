#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.AndroidVbmeta;

/// <summary>
/// Human-readable renderings of AVB descriptors: the fields <c>avbtool info_image</c> prints, as
/// <c>key = value</c> lines, and a short name for the descriptor's entry. Rendering never throws —
/// a descriptor whose body does not match its declared lengths is reported as malformed.
/// </summary>
public static class AvbDescriptorText {

  private static uint U32(ReadOnlySpan<byte> e, int o) => BinaryPrimitives.ReadUInt32BigEndian(e[o..]);
  private static ulong U64(ReadOnlySpan<byte> e, int o) => BinaryPrimitives.ReadUInt64BigEndian(e[o..]);

  /// <summary>The partition name, property key, or nothing — whatever names the descriptor's subject.</summary>
  public static string? Subject(AvbDescriptor d) {
    try {
      var e = d.Encoded.AsSpan();
      return d.Tag switch {
        AvbDescriptor.Hash => Utf8(e, 132, (int)U32(e, 56)),
        AvbDescriptor.Hashtree => Utf8(e, 180, (int)U32(e, 104)),
        AvbDescriptor.ChainPartition => Utf8(e, 92, (int)U32(e, 20)),
        AvbDescriptor.Property => Utf8(e, 32, checked((int)U64(e, 16))),
        _ => null,
      };
    } catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException) {
      return null;
    }
  }

  public static string EntryName(int index, AvbDescriptor d) {
    var subject = Subject(d);
    var safe = subject is null ? "" : "_" + new string(subject.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_').ToArray());
    if (safe.Length > 41) safe = safe[..41];
    return $"descriptors/{index.ToString("D2", CultureInfo.InvariantCulture)}_{d.Kind}{safe}.bin";
  }

  public static void Render(StringBuilder sb, AvbDescriptor d) {
    void Line(string key, string value) => sb.Append(key).Append(" = ").Append(value).Append('\n');
    Line("kind", d.Kind);
    Line("size", d.Encoded.Length.ToString(CultureInfo.InvariantCulture));
    try {
      var e = d.Encoded.AsSpan();
      switch (d.Tag) {
        case AvbDescriptor.Hash: {
          int nameLen = (int)U32(e, 56), saltLen = (int)U32(e, 60), digestLen = (int)U32(e, 64);
          Line("image_size", U64(e, 16).ToString(CultureInfo.InvariantCulture));
          Line("hash_algorithm", Ascii(e.Slice(24, 32)));
          Line("partition_name", Utf8(e, 132, nameLen));
          Line("salt", Hex(e, 132 + nameLen, saltLen));
          Line("digest", Hex(e, 132 + nameLen + saltLen, digestLen));
          Line("flags", U32(e, 68).ToString(CultureInfo.InvariantCulture));
          break;
        }
        case AvbDescriptor.Hashtree: {
          int nameLen = (int)U32(e, 104), saltLen = (int)U32(e, 108), digestLen = (int)U32(e, 112);
          Line("dm_verity_version", U32(e, 16).ToString(CultureInfo.InvariantCulture));
          Line("image_size", U64(e, 20).ToString(CultureInfo.InvariantCulture));
          Line("tree_offset", U64(e, 28).ToString(CultureInfo.InvariantCulture));
          Line("tree_size", U64(e, 36).ToString(CultureInfo.InvariantCulture));
          Line("data_block_size", U32(e, 44).ToString(CultureInfo.InvariantCulture));
          Line("hash_block_size", U32(e, 48).ToString(CultureInfo.InvariantCulture));
          Line("fec_num_roots", U32(e, 52).ToString(CultureInfo.InvariantCulture));
          Line("fec_offset", U64(e, 56).ToString(CultureInfo.InvariantCulture));
          Line("fec_size", U64(e, 64).ToString(CultureInfo.InvariantCulture));
          Line("hash_algorithm", Ascii(e.Slice(72, 32)));
          Line("partition_name", Utf8(e, 180, nameLen));
          Line("salt", Hex(e, 180 + nameLen, saltLen));
          Line("root_digest", Hex(e, 180 + nameLen + saltLen, digestLen));
          Line("flags", U32(e, 116).ToString(CultureInfo.InvariantCulture));
          break;
        }
        case AvbDescriptor.KernelCmdline:
          Line("flags", U32(e, 16).ToString(CultureInfo.InvariantCulture));
          Line("cmdline", Utf8(e, 24, (int)U32(e, 20)));
          break;
        case AvbDescriptor.ChainPartition: {
          int nameLen = (int)U32(e, 20), keyLen = (int)U32(e, 24);
          Line("rollback_index_location", U32(e, 16).ToString(CultureInfo.InvariantCulture));
          Line("partition_name", Utf8(e, 92, nameLen));
          Line("public_key_sha1", Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(e.Slice(92 + nameLen, keyLen))));
          Line("flags", U32(e, 28).ToString(CultureInfo.InvariantCulture));
          break;
        }
        case AvbDescriptor.Property: {
          var keyLen = checked((int)U64(e, 16));
          var valueLen = checked((int)U64(e, 24));
          Line("key", Utf8(e, 32, keyLen));
          var value = e.Slice(32 + keyLen + 1, valueLen);
          Line("value", value.ContainsAnyExceptInRange((byte)0x20, (byte)0x7E) ? "hex:" + Convert.ToHexStringLower(value) : Encoding.ASCII.GetString(value));
          break;
        }
      }
    } catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException) {
      Line("malformed", "true");
    }
  }

  private static string Utf8(ReadOnlySpan<byte> e, int offset, int length) => Encoding.UTF8.GetString(e.Slice(offset, length)).ReplaceLineEndings(" ");
  private static string Hex(ReadOnlySpan<byte> e, int offset, int length) => Convert.ToHexStringLower(e.Slice(offset, length));

  private static string Ascii(ReadOnlySpan<byte> field) {
    var end = field.IndexOf((byte)0);
    return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
  }
}
