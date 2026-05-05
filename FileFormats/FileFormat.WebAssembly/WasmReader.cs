#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.WebAssembly;

/// <summary>
/// Reader for WebAssembly binary modules (the <c>.wasm</c> on-disk format defined
/// by the W3C WebAssembly Core specification, §5). Walks the section list and
/// surfaces each section as an opaque byte payload tagged with its numeric id and
/// (for custom sections) the name embedded at the head of the section body.
/// </summary>
public sealed class WasmReader {

  /// <summary>Per-section metadata + raw body bytes.</summary>
  public sealed record Section(
    int Id,
    string TypeName,
    string? CustomName,    // populated for id == 0 only
    byte[] Body            // section body excluding the (id, leb-size) framing
  );

  public sealed record Module(uint Version, IReadOnlyList<Section> Sections);

  /// <summary>Magic bytes that begin every wasm binary.</summary>
  public static ReadOnlySpan<byte> Magic => [0x00, 0x61, 0x73, 0x6D]; // \0 a s m

  public static Module Read(ReadOnlySpan<byte> data) {
    if (data.Length < 8) throw new InvalidDataException("wasm: file shorter than 8-byte preamble.");
    if (!data[..4].SequenceEqual(Magic))
      throw new InvalidDataException($"wasm: bad magic 0x{data[0]:X2}{data[1]:X2}{data[2]:X2}{data[3]:X2}");

    var version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);

    var sections = new List<Section>();
    var pos = 8;
    while (pos < data.Length) {
      var id = data[pos];
      pos++;
      var size = (int)ReadLeb128(data, ref pos, out var ok);
      if (!ok || pos + size > data.Length) break;

      var body = data.Slice(pos, size).ToArray();
      string? customName = null;
      if (id == 0 && body.Length > 0) {
        // Custom section: body starts with a LEB128-length-prefixed UTF-8 name.
        var p = 0;
        var nameLen = (int)ReadLeb128(body, ref p, out var nameOk);
        if (nameOk && p + nameLen <= body.Length)
          customName = Encoding.UTF8.GetString(body, p, nameLen);
      }
      sections.Add(new Section(
        Id: id,
        TypeName: TypeName(id),
        CustomName: customName,
        Body: body));
      pos += size;
    }

    return new Module(version, sections);
  }

  private static string TypeName(int id) => id switch {
    0 => "custom",
    1 => "type",
    2 => "import",
    3 => "function",
    4 => "table",
    5 => "memory",
    6 => "global",
    7 => "export",
    8 => "start",
    9 => "element",
    10 => "code",
    11 => "data",
    12 => "datacount",
    _ => $"unknown_{id}",
  };

  /// <summary>Reads an unsigned LEB128 integer; advances <paramref name="pos"/>.</summary>
  private static ulong ReadLeb128(ReadOnlySpan<byte> data, ref int pos, out bool ok) {
    ulong result = 0;
    var shift = 0;
    while (pos < data.Length) {
      var b = data[pos++];
      result |= (ulong)(b & 0x7F) << shift;
      if ((b & 0x80) == 0) {
        ok = true;
        return result;
      }
      shift += 7;
      if (shift >= 64) break;
    }
    ok = false;
    return 0;
  }

  /// <summary>Reads an unsigned LEB128 integer from a byte array; advances <paramref name="pos"/>.</summary>
  private static ulong ReadLeb128(byte[] data, ref int pos, out bool ok) =>
    ReadLeb128((ReadOnlySpan<byte>)data, ref pos, out ok);
}
