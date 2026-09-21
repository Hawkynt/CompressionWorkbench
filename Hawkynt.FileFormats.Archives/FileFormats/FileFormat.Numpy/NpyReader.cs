#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Numpy;

/// <summary>
/// Reader for the NumPy NPY on-disk format (v1, v2, v3). Splits an <c>.npy</c>
/// into its magic prefix, version, Python-dict header string, and raw array
/// payload.
/// </summary>
/// <remarks>
/// Format reference: https://numpy.org/doc/stable/reference/generated/numpy.lib.format.html.
/// Header layout:
///   bytes 0..6   magic  '\x93NUMPY'
///   byte 6       major version (u8)
///   byte 7       minor version (u8)
///   bytes 8..    header length: u16 LE (v1) or u32 LE (v2, v3)
///   then         ASCII Python-dict header (newline-padded, terminated by '\n')
///   then         raw array bytes to EOF
/// V3 adds UTF-8 support for the dict but is otherwise identical to v2 on-disk.
/// </remarks>
public sealed class NpyReader {

  /// <summary>The 6-byte magic that begins every NPY file.</summary>
  public static ReadOnlySpan<byte> Magic => [0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y'];

  /// <summary>Parsed NPY file — the four on-disk regions plus scanned metadata.</summary>
  public sealed record NpyArray(
    byte MajorVersion,
    byte MinorVersion,
    int HeaderLength,
    string HeaderText,         // raw Python dict string (may be padded with spaces + '\n')
    string? Dtype,             // parsed descr, e.g. "<f4"
    string? Shape,             // parsed shape tuple, e.g. "(3, 4)"
    bool FortranOrder,
    byte[] HeaderBytes,        // magic + version + header-len + dict bytes, all framing
    byte[] ArrayBytes          // raw payload after the header
  );

  internal readonly record struct NpyLayout(
    byte MajorVersion,
    byte MinorVersion,
    int HeaderLength,
    string HeaderText,
    string? Dtype,
    string? Shape,
    bool FortranOrder,
    int BodyOffset);

  /// <summary>Parses an NPY file from an in-memory span.</summary>
  public static NpyArray Read(ReadOnlySpan<byte> data) {
    var layout = ReadLayout(data);
    return new NpyArray(
      MajorVersion: layout.MajorVersion,
      MinorVersion: layout.MinorVersion,
      HeaderLength: layout.HeaderLength,
      HeaderText: layout.HeaderText,
      Dtype: layout.Dtype,
      Shape: layout.Shape,
      FortranOrder: layout.FortranOrder,
      HeaderBytes: data[..layout.BodyOffset].ToArray(),
      ArrayBytes: data[layout.BodyOffset..].ToArray());
  }

  internal static NpyLayout ReadLayout(ReadOnlySpan<byte> data) {
    if (data.Length < 10) throw new InvalidDataException("npy: file shorter than 10-byte preamble.");
    if (!data[..6].SequenceEqual(Magic))
      throw new InvalidDataException("npy: bad magic — expected '\\x93NUMPY'.");

    var major = data[6];
    var minor = data[7];
    int headerLen;
    int headerStart;
    switch (major) {
      case 1:
        headerLen = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        headerStart = 10;
        break;
      case 2:
      case 3:
        if (data.Length < 12) throw new InvalidDataException("npy: v2/v3 preamble truncated.");
        headerLen = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]));
        headerStart = 12;
        break;
      default:
        throw new InvalidDataException($"npy: unsupported version {major}.{minor}");
    }

    if (headerLen < 0 || headerStart > data.Length - headerLen)
      throw new InvalidDataException("npy: header length exceeds file size.");

    var dictBytes = data.Slice(headerStart, headerLen);
    // Keep the existing descriptor behaviour: decode the Python-dict header as
    // Latin-1 so every byte round-trips through the metadata parser.
    var headerText = Encoding.Latin1.GetString(dictBytes);

    return new NpyLayout(
      MajorVersion: major,
      MinorVersion: minor,
      HeaderLength: headerLen,
      HeaderText: headerText,
      Dtype: ExtractDictString(headerText, "descr"),
      Shape: ExtractTuple(headerText, "shape"),
      FortranOrder: ExtractDictBool(headerText, "fortran_order"),
      BodyOffset: headerStart + headerLen);
  }

  // Pull a single-quoted or double-quoted value out of a Python-ish dict string.
  private static string? ExtractDictString(string dict, string key) {
    var k = "'" + key + "'";
    var i = dict.IndexOf(k, StringComparison.Ordinal);
    if (i < 0) return null;
    var colon = dict.IndexOf(':', i + k.Length);
    if (colon < 0) return null;
    var j = colon + 1;
    while (j < dict.Length && char.IsWhiteSpace(dict[j])) j++;
    if (j >= dict.Length) return null;
    var quote = dict[j];
    if (quote != '\'' && quote != '"') return null;
    var end = dict.IndexOf(quote, j + 1);
    return end < 0 ? null : dict[(j + 1)..end];
  }

  private static string? ExtractTuple(string dict, string key) {
    var k = "'" + key + "'";
    var i = dict.IndexOf(k, StringComparison.Ordinal);
    if (i < 0) return null;
    var colon = dict.IndexOf(':', i + k.Length);
    if (colon < 0) return null;
    var j = colon + 1;
    while (j < dict.Length && char.IsWhiteSpace(dict[j])) j++;
    if (j >= dict.Length || dict[j] != '(') return null;
    var end = dict.IndexOf(')', j);
    return end < 0 ? null : dict[j..(end + 1)];
  }

  private static bool ExtractDictBool(string dict, string key) {
    var k = "'" + key + "'";
    var i = dict.IndexOf(k, StringComparison.Ordinal);
    if (i < 0) return false;
    var colon = dict.IndexOf(':', i + k.Length);
    if (colon < 0) return false;
    var rest = dict[(colon + 1)..].TrimStart();
    return rest.StartsWith("True", StringComparison.Ordinal);
  }
}
