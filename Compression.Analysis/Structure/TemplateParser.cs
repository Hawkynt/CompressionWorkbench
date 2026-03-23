using System.Text.RegularExpressions;

namespace Compression.Analysis.Structure;

/// <summary>
/// Recursive descent parser for .cwbt structure template syntax.
/// </summary>
public static partial class TemplateParser {

  /// <summary>Parses a .cwbt template string into a StructureTemplate.</summary>
  public static StructureTemplate Parse(string source, string name = "Untitled") {
    var structs = new List<StructDefinition>();
    var lines = source.Split('\n');
    var i = 0;

    while (i < lines.Length) {
      var line = lines[i].Trim();

      // Skip empty lines and comments
      if (string.IsNullOrEmpty(line) || line.StartsWith("//")) {
        i++;
        continue;
      }

      // Parse struct definition
      if (line.StartsWith("struct ")) {
        var (sd, nextLine) = ParseStruct(lines, i);
        structs.Add(sd);
        i = nextLine;
      }
      else {
        throw new FormatException($"Unexpected token at line {i + 1}: {line}");
      }
    }

    if (structs.Count == 0)
      throw new FormatException("Template contains no struct definitions.");

    return new StructureTemplate { Name = name, Structs = structs };
  }

  private static (StructDefinition, int) ParseStruct(string[] lines, int startLine) {
    var header = lines[startLine].Trim();
    // "struct Name {"
    var match = StructHeaderRegex().Match(header);
    if (!match.Success)
      throw new FormatException($"Invalid struct header at line {startLine + 1}: {header}");

    var structName = match.Groups[1].Value;
    var fields = new List<FieldDefinition>();
    var i = startLine + 1;

    while (i < lines.Length) {
      var line = lines[i].Trim();
      if (string.IsNullOrEmpty(line) || line.StartsWith("//")) {
        i++;
        continue;
      }

      // End of struct
      if (line.StartsWith("}")) {
        i++;
        break;
      }

      // Split by semicolons to support multiple fields per line: "u8 r2;u8 r1;u8 r;"
      var segments = line.Split(';');
      foreach (var seg in segments) {
        var trimmed = seg.Trim();
        if (string.IsNullOrEmpty(trimmed)) continue;
        if (trimmed.StartsWith("//")) break; // rest of line is comment
        if (trimmed.StartsWith("}")) break;
        fields.Add(ParseField(trimmed, i + 1));
      }
      i++;
    }

    return (new StructDefinition { Name = structName, Fields = fields }, i);
  }

  internal static FieldDefinition ParseField(string line, int lineNum) {
    // Remove trailing semicolon and comments
    var semi = line.IndexOf(';');
    if (semi >= 0) line = line[..semi];
    line = line.Trim();

    // Match: "type[arrayLen] name" or "type name"
    var match = FieldRegex().Match(line);
    if (!match.Success)
      throw new FormatException($"Invalid field definition at line {lineNum}: {line}");

    var typeStr = match.Groups[1].Value;
    var arrayLen = match.Groups[2].Success ? match.Groups[2].Value : null;
    var fieldName = match.Groups[3].Value;

    var (fieldType, fixedSize, elemType, elemSize) = ParseType(typeStr, arrayLen);

    var bitCount = 0;
    if (fieldType == FieldType.Bits && int.TryParse(arrayLen, out var bc))
      bitCount = bc;

    return new FieldDefinition {
      Name = fieldName,
      Type = fieldType,
      FixedSize = fixedSize,
      ArrayLength = arrayLen,
      ElementType = elemType,
      ElementSize = elemSize,
      BitCount = bitCount,
    };
  }

  private static (FieldType type, int fixedSize, FieldType elemType, int elemSize) ParseType(string typeStr, string? arrayLen) {
    var lower = typeStr.ToLowerInvariant();

    // Bitfields: bits[N]
    if (lower == "bits" && arrayLen != null)
      return (FieldType.Bits, 0, FieldType.U8, 0);

    // Primitive with array → typed array (except u8 and char which are special)
    if (arrayLen != null) {
      if (lower == "u8") return (FieldType.ByteArray, 0, FieldType.U8, 1);
      if (lower == "char") return (FieldType.CharArray, 0, FieldType.U8, 1);

      // Any other type with [N] is a typed array
      var (elemType, elemSize) = ResolvePrimitive(lower);
      if (elemSize > 0) return (FieldType.TypedArray, 0, elemType, elemSize);
    }

    var (ft, fs) = ResolvePrimitive(lower);
    return (ft, fs, FieldType.U8, 0);
  }

  private static (FieldType type, int fixedSize) ResolvePrimitive(string lower) => lower switch {
    // Unsigned integers
    "u8" or "uint8" or "byte" => (FieldType.U8, 1),
    "u16le" or "uint16le" => (FieldType.U16LE, 2),
    "u16be" or "uint16be" => (FieldType.U16BE, 2),
    "u32le" or "uint32le" => (FieldType.U32LE, 4),
    "u32be" or "uint32be" => (FieldType.U32BE, 4),
    "u64le" or "uint64le" => (FieldType.U64LE, 8),
    "u64be" or "uint64be" => (FieldType.U64BE, 8),
    "u96le" => (FieldType.U96LE, 12),
    "u96be" => (FieldType.U96BE, 12),
    "u128le" or "uint128le" => (FieldType.U128LE, 16),
    "u128be" or "uint128be" => (FieldType.U128BE, 16),
    "u256le" => (FieldType.U256LE, 32),
    "u256be" => (FieldType.U256BE, 32),
    "u512le" => (FieldType.U512LE, 64),
    "u512be" => (FieldType.U512BE, 64),

    // Signed integers
    "i8" or "int8" or "sbyte" => (FieldType.I8, 1),
    "i16le" or "int16le" => (FieldType.I16LE, 2),
    "i16be" or "int16be" => (FieldType.I16BE, 2),
    "i32le" or "int32le" => (FieldType.I32LE, 4),
    "i32be" or "int32be" => (FieldType.I32BE, 4),
    "i64le" or "int64le" => (FieldType.I64LE, 8),
    "i64be" or "int64be" => (FieldType.I64BE, 8),
    "i128le" or "int128le" => (FieldType.I128LE, 16),
    "i128be" or "int128be" => (FieldType.I128BE, 16),

    // IEEE floats
    "f16le" or "half_le" or "float16le" => (FieldType.F16LE, 2),
    "f16be" or "half_be" or "float16be" => (FieldType.F16BE, 2),
    "f32le" or "float32le" or "floatle" => (FieldType.F32LE, 4),
    "f32be" or "float32be" or "floatbe" => (FieldType.F32BE, 4),
    "f64le" or "float64le" or "doublele" => (FieldType.F64LE, 8),
    "f64be" or "float64be" or "doublebe" => (FieldType.F64BE, 8),
    "bf16le" or "bfloat16le" => (FieldType.BF16LE, 2),
    "bf16be" or "bfloat16be" => (FieldType.BF16BE, 2),
    "fp8e4m3" or "fp8_e4m3" => (FieldType.FP8E4M3, 1),
    "fp8e5m2" or "fp8_e5m2" => (FieldType.FP8E5M2, 1),

    // Date & Time
    "unixts32le" or "unix32le" => (FieldType.UnixTs32LE, 4),
    "unixts32be" or "unix32be" => (FieldType.UnixTs32BE, 4),
    "unixts64le" or "unix64le" => (FieldType.UnixTs64LE, 8),
    "unixts64be" or "unix64be" => (FieldType.UnixTs64BE, 8),
    "dosdate" => (FieldType.DosDate, 2),
    "dostime" => (FieldType.DosTime, 2),
    "filetime" => (FieldType.FileTime, 8),
    "oledate" or "oadate" => (FieldType.OleDate, 8),
    "hfsdate" or "macdate" => (FieldType.HfsDate, 4),
    "netticks" or "clrticks" => (FieldType.NetTicks, 8),
    "webkittime" => (FieldType.WebKitTime, 8),

    // Fixed-point
    "q8_8le" or "q8.8" => (FieldType.Q8_8LE, 2),
    "q16_16le" or "q16.16" => (FieldType.Q16_16LE, 4),
    "uq8_8le" or "uq8.8" => (FieldType.UQ8_8LE, 2),
    "uq16_16le" or "uq16.16" => (FieldType.UQ16_16LE, 4),
    "q32_32le" or "q32.32" => (FieldType.Q32_32LE, 8),
    "uq32_32le" or "uq32.32" => (FieldType.UQ32_32LE, 8),

    // BCD
    "bcd8" => (FieldType.Bcd8, 1),
    "bcd16le" or "bcd16" => (FieldType.Bcd16LE, 2),
    "bcd32le" or "bcd32" => (FieldType.Bcd32LE, 4),

    // Color
    "rgb24" or "rgb" => (FieldType.Rgb24, 3),
    "rgba32" or "rgba" => (FieldType.Rgba32, 4),
    "bgr24" or "bgr" => (FieldType.Bgr24, 3),
    "bgra32" or "bgra" => (FieldType.Bgra32, 4),
    "rgb565le" or "rgb565" => (FieldType.Rgb565LE, 2),
    "rgb555le" or "rgb555" => (FieldType.Rgb555LE, 2),
    "rgba5551le" or "rgba5551" => (FieldType.Rgba5551LE, 2),

    // Network & ID
    "ipv4" => (FieldType.Ipv4, 4),
    "ipv6" => (FieldType.Ipv6, 16),
    "mac48" or "mac" => (FieldType.Mac48, 6),
    "fourcc" or "fcc" => (FieldType.FourCC, 4),
    "guid" or "uuid" => (FieldType.GuidLE, 16),

    // Special
    "bool8" or "bool" => (FieldType.Bool8, 1),

    _ => (FieldType.StructRef, 0),
  };

  [GeneratedRegex(@"^struct\s+(\w+)\s*\{")]
  private static partial Regex StructHeaderRegex();

  [GeneratedRegex(@"^(\w+)(?:\[([^\]]+)\])?\s+(\w+)\s*$")]
  private static partial Regex FieldRegex();
}
