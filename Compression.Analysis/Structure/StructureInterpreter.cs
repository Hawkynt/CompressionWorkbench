using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Compression.Analysis.Structure;

/// <summary>
/// Applies a StructureTemplate to binary data and produces a tree of parsed field values.
/// </summary>
public static class StructureInterpreter {

  /// <summary>
  /// Interprets binary data using the template's root struct.
  /// </summary>
  public static List<ParsedField> Interpret(StructureTemplate template, ReadOnlySpan<byte> data, int startOffset = 0) {
    var structMap = new Dictionary<string, StructDefinition>(StringComparer.Ordinal);
    foreach (var s in template.Structs)
      structMap[s.Name] = s;

    var offset = startOffset;
    return InterpretStruct(template.Root, structMap, data, ref offset);
  }

  private static List<ParsedField> InterpretStruct(
      StructDefinition structDef,
      Dictionary<string, StructDefinition> structMap,
      ReadOnlySpan<byte> data,
      ref int offset) {

    var results = new List<ParsedField>();
    var fieldValues = new Dictionary<string, long>(StringComparer.Ordinal);
    var bitOffset = 0;

    foreach (var field in structDef.Fields) {
      if (offset >= data.Length) break;

      if (field.Type == FieldType.StructRef && field.StructRef == null && structMap.ContainsKey(field.Name)) {
        var children = InterpretStruct(structMap[field.Name], structMap, data, ref offset);
        results.Add(new ParsedField(field.Name, "struct", offset, 0, null, children));
        continue;
      }

      if (field.Type == FieldType.Bits) {
        var bitsResult = InterpretBitfield(field, data, ref offset, ref bitOffset);
        results.Add(bitsResult);
        continue;
      }

      // Flush partial bit offset to next byte boundary
      if (bitOffset > 0) {
        offset++;
        bitOffset = 0;
      }

      if (field.ArrayLength != null) {
        var arrayResult = InterpretArray(field, fieldValues, data, ref offset);
        results.Add(arrayResult);
        continue;
      }

      var startOff = offset;
      var (value, display) = ReadPrimitive(field.Type, data, ref offset);
      fieldValues[field.Name] = value;
      results.Add(new ParsedField(field.Name, field.Type.ToString(), startOff, offset - startOff, display, null));
    }

    return results;
  }

  private static ParsedField InterpretArray(FieldDefinition field,
      Dictionary<string, long> fieldValues,
      ReadOnlySpan<byte> data, ref int offset) {

    var count = ResolveArrayLength(field.ArrayLength!, fieldValues, data.Length - offset);
    var startOff = offset;

    if (field.Type == FieldType.CharArray) {
      var take = Math.Min(count, data.Length - offset);
      var text = Encoding.ASCII.GetString(data.Slice(offset, take));
      offset += take;
      return new ParsedField(field.Name, $"char[{count}]", startOff, take, $"\"{text}\"", null);
    }

    if (field.Type == FieldType.TypedArray) {
      var elemSize = field.ElementSize;
      var maxElems = elemSize > 0 ? Math.Min(count, (data.Length - offset) / elemSize) : 0;
      var children = new List<ParsedField>();
      var sb = new StringBuilder("{ ");
      for (var i = 0; i < maxElems; i++) {
        var elemOff = offset;
        var (value, display) = ReadPrimitive(field.ElementType, data, ref offset);
        children.Add(new ParsedField($"[{i}]", field.ElementType.ToString(), elemOff, elemSize, display, null));
        if (i > 0) sb.Append(", ");
        if (i < 8) sb.Append(display);
        else if (i == 8) sb.Append("...");
      }
      sb.Append(" }");
      var totalSize = offset - startOff;
      return new ParsedField(field.Name, $"{field.ElementType}[{count}]", startOff, totalSize, sb.ToString(), children);
    }

    // u8[N]
    {
      var take = Math.Min(count, data.Length - offset);
      var hex = Convert.ToHexString(data.Slice(offset, Math.Min(take, 64)));
      if (take > 64) hex += "...";
      offset += take;
      return new ParsedField(field.Name, $"u8[{count}]", startOff, take, hex, null);
    }
  }

  private static ParsedField InterpretBitfield(FieldDefinition field,
      ReadOnlySpan<byte> data, ref int offset, ref int bitOffset) {
    var startOff = offset;
    var bits = field.BitCount;
    if (bits <= 0) bits = 1;

    long value = 0;
    var bitsRead = 0;
    while (bitsRead < bits && offset < data.Length) {
      var bitsAvail = 8 - bitOffset;
      var bitsToRead = Math.Min(bits - bitsRead, bitsAvail);
      var mask = (1 << bitsToRead) - 1;
      var shifted = (data[offset] >> bitOffset) & mask;
      value |= (long)shifted << bitsRead;
      bitsRead += bitsToRead;
      bitOffset += bitsToRead;
      if (bitOffset >= 8) { offset++; bitOffset = 0; }
    }

    var size = offset - startOff + (bitOffset > 0 ? 1 : 0);
    var display = bits <= 8
      ? $"{value} (0b{Convert.ToString(value, 2).PadLeft(bits, '0')})"
      : $"{value} (0x{value:X})";
    return new ParsedField(field.Name, $"bits[{bits}]", startOff, Math.Max(size, 1), display, null);
  }

  private static int ResolveArrayLength(string lengthExpr, Dictionary<string, long> fieldValues, int remaining) {
    if (lengthExpr == "*") return remaining;
    if (int.TryParse(lengthExpr, out var literal)) return literal;
    if (fieldValues.TryGetValue(lengthExpr, out var refVal)) return (int)refVal;
    return 0;
  }

  private static string FormatBigHex(ReadOnlySpan<byte> data, int offset, int size, bool littleEndian) {
    var sb = new StringBuilder("0x", 2 + size * 2);
    if (littleEndian) {
      for (var k = size - 1; k >= 0; k--) sb.Append(data[offset + k].ToString("X2"));
    }
    else {
      for (var k = 0; k < size; k++) sb.Append(data[offset + k].ToString("X2"));
    }
    return sb.ToString();
  }

  private static float DecodeFP8E4M3(byte raw) {
    var sign = (raw >> 7) & 1;
    var exp = (raw >> 3) & 0xF;
    var mantissa = raw & 0x7;
    if (exp == 0xF && mantissa == 0x7) return float.NaN;
    var s = sign != 0 ? -1f : 1f;
    if (exp == 0) return s * (mantissa / 8f) * MathF.Pow(2, -6);
    return s * (1f + mantissa / 8f) * MathF.Pow(2, exp - 7);
  }

  private static float DecodeFP8E5M2(byte raw) {
    var sign = (raw >> 7) & 1;
    var exp = (raw >> 2) & 0x1F;
    var mantissa = raw & 0x3;
    var s = sign != 0 ? -1f : 1f;
    if (exp == 0x1F) return mantissa != 0 ? float.NaN : (sign != 0 ? float.NegativeInfinity : float.PositiveInfinity);
    if (exp == 0) return s * (mantissa / 4f) * MathF.Pow(2, -14);
    return s * (1f + mantissa / 4f) * MathF.Pow(2, exp - 15);
  }

  private static string DecodeBcd(long raw, int digitCount) {
    var sb = new StringBuilder(digitCount);
    for (var d = digitCount - 1; d >= 0; d--) {
      var nibble = (int)((raw >> (d * 4)) & 0xF);
      if (nibble > 9) return $"(invalid BCD: 0x{raw:X})";
      sb.Append((char)('0' + nibble));
    }
    return sb.ToString();
  }

  private static (long value, string display) ReadPrimitive(FieldType type, ReadOnlySpan<byte> data, ref int offset) {
    if (offset >= data.Length) return (0, "(EOF)");

    long value;
    string display;

    switch (type) {
      // ── Unsigned integers ────────────────────────────────────────────
      case FieldType.U8:
        value = data[offset];
        display = $"{value} (0x{value:X2})";
        offset += 1;
        break;
      case FieldType.U16LE:
        if (offset + 2 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        display = $"{value} (0x{value:X4})";
        offset += 2;
        break;
      case FieldType.U16BE:
        if (offset + 2 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        display = $"{value} (0x{value:X4})";
        offset += 2;
        break;
      case FieldType.U32LE:
        if (offset + 4 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        display = $"{value} (0x{value:X8})";
        offset += 4;
        break;
      case FieldType.U32BE:
        if (offset + 4 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        display = $"{value} (0x{value:X8})";
        offset += 4;
        break;
      case FieldType.U64LE:
        if (offset + 8 > data.Length) return (0, "(EOF)");
        value = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
        display = $"{(ulong)value} (0x{(ulong)value:X16})";
        offset += 8;
        break;
      case FieldType.U64BE:
        if (offset + 8 > data.Length) return (0, "(EOF)");
        value = (long)BinaryPrimitives.ReadUInt64BigEndian(data[offset..]);
        display = $"{(ulong)value} (0x{(ulong)value:X16})";
        offset += 8;
        break;

      // ── Signed integers ──────────────────────────────────────────────
      case FieldType.I8:
        value = (sbyte)data[offset];
        display = $"{value}";
        offset += 1;
        break;
      case FieldType.I16LE:
        if (offset + 2 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);
        display = $"{value}";
        offset += 2;
        break;
      case FieldType.I16BE:
        if (offset + 2 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadInt16BigEndian(data[offset..]);
        display = $"{value}";
        offset += 2;
        break;
      case FieldType.I32LE:
        if (offset + 4 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        display = $"{value}";
        offset += 4;
        break;
      case FieldType.I32BE:
        if (offset + 4 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadInt32BigEndian(data[offset..]);
        display = $"{value}";
        offset += 4;
        break;
      case FieldType.I64LE:
        if (offset + 8 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
        display = $"{value}";
        offset += 8;
        break;
      case FieldType.I64BE:
        if (offset + 8 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadInt64BigEndian(data[offset..]);
        display = $"{value}";
        offset += 8;
        break;

      // ── IEEE Floats ──────────────────────────────────────────────────
      case FieldType.F16LE: {
        if (offset + 2 > data.Length) return (0, "(EOF)");
        var h = BinaryPrimitives.ReadHalfLittleEndian(data[offset..]);
        value = BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);
        display = ((float)h).ToString(CultureInfo.InvariantCulture);
        offset += 2;
        break;
      }
      case FieldType.F16BE: {
        if (offset + 2 > data.Length) return (0, "(EOF)");
        var h = BinaryPrimitives.ReadHalfBigEndian(data[offset..]);
        value = BinaryPrimitives.ReadInt16BigEndian(data[offset..]);
        display = ((float)h).ToString(CultureInfo.InvariantCulture);
        offset += 2;
        break;
      }
      case FieldType.F32LE: {
        if (offset + 4 > data.Length) return (0, "(EOF)");
        var f = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
        value = BitConverter.SingleToInt32Bits(f);
        display = f.ToString(CultureInfo.InvariantCulture);
        offset += 4;
        break;
      }
      case FieldType.F32BE: {
        if (offset + 4 > data.Length) return (0, "(EOF)");
        var f = BinaryPrimitives.ReadSingleBigEndian(data[offset..]);
        value = BitConverter.SingleToInt32Bits(f);
        display = f.ToString(CultureInfo.InvariantCulture);
        offset += 4;
        break;
      }
      case FieldType.F64LE: {
        if (offset + 8 > data.Length) return (0, "(EOF)");
        var d = BinaryPrimitives.ReadDoubleLittleEndian(data[offset..]);
        value = BitConverter.DoubleToInt64Bits(d);
        display = d.ToString(CultureInfo.InvariantCulture);
        offset += 8;
        break;
      }
      case FieldType.F64BE: {
        if (offset + 8 > data.Length) return (0, "(EOF)");
        var d = BinaryPrimitives.ReadDoubleBigEndian(data[offset..]);
        value = BitConverter.DoubleToInt64Bits(d);
        display = d.ToString(CultureInfo.InvariantCulture);
        offset += 8;
        break;
      }

      // ── Date & Time ──────────────────────────────────────────────────
      case FieldType.UnixTs32LE: {
        if (offset + 4 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        try {
          var dt = DateTimeOffset.FromUnixTimeSeconds(value);
          display = $"{dt:yyyy-MM-dd HH:mm:ss} UTC";
        }
        catch { display = $"(invalid: {value})"; }
        offset += 4;
        break;
      }
      case FieldType.UnixTs32BE: {
        if (offset + 4 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        try {
          var dt = DateTimeOffset.FromUnixTimeSeconds(value);
          display = $"{dt:yyyy-MM-dd HH:mm:ss} UTC";
        }
        catch { display = $"(invalid: {value})"; }
        offset += 4;
        break;
      }
      case FieldType.UnixTs64LE: {
        if (offset + 8 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
        try {
          var dt = DateTimeOffset.FromUnixTimeSeconds(value);
          display = $"{dt:yyyy-MM-dd HH:mm:ss} UTC";
        }
        catch { display = $"(invalid: {value})"; }
        offset += 8;
        break;
      }
      case FieldType.UnixTs64BE: {
        if (offset + 8 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadInt64BigEndian(data[offset..]);
        try {
          var dt = DateTimeOffset.FromUnixTimeSeconds(value);
          display = $"{dt:yyyy-MM-dd HH:mm:ss} UTC";
        }
        catch { display = $"(invalid: {value})"; }
        offset += 8;
        break;
      }
      case FieldType.DosDate: {
        if (offset + 2 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        var day = (int)(value & 0x1F);
        var month = (int)((value >> 5) & 0x0F);
        var year = (int)(((value >> 9) & 0x7F) + 1980);
        display = $"{year:D4}-{month:D2}-{day:D2}";
        offset += 2;
        break;
      }
      case FieldType.DosTime: {
        if (offset + 2 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        var sec = (int)(value & 0x1F) * 2;
        var min = (int)((value >> 5) & 0x3F);
        var hour = (int)((value >> 11) & 0x1F);
        display = $"{hour:D2}:{min:D2}:{sec:D2}";
        offset += 2;
        break;
      }
      case FieldType.FileTime: {
        if (offset + 8 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
        try {
          var dt = DateTime.FromFileTimeUtc(value);
          display = $"{dt:yyyy-MM-dd HH:mm:ss.fff} UTC";
        }
        catch { display = $"(invalid: 0x{(ulong)value:X16})"; }
        offset += 8;
        break;
      }
      case FieldType.OleDate: {
        if (offset + 8 > data.Length) return (0, "(EOF)");
        var d = BinaryPrimitives.ReadDoubleLittleEndian(data[offset..]);
        value = BitConverter.DoubleToInt64Bits(d);
        try {
          var dt = DateTime.FromOADate(d);
          display = $"{dt:yyyy-MM-dd HH:mm:ss}";
        }
        catch { display = $"(invalid: {d})"; }
        offset += 8;
        break;
      }
      case FieldType.HfsDate: {
        if (offset + 4 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        var epoch = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dt = epoch.AddSeconds(value);
        display = $"{dt:yyyy-MM-dd HH:mm:ss} UTC";
        offset += 4;
        break;
      }
      case FieldType.NetTicks: {
        if (offset + 8 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
        try {
          var dt = new DateTime(value & 0x3FFFFFFFFFFFFFFFL, DateTimeKind.Utc);
          display = $"{dt:yyyy-MM-dd HH:mm:ss.fffffff}";
        }
        catch { display = $"(invalid: {value})"; }
        offset += 8;
        break;
      }
      case FieldType.WebKitTime: {
        if (offset + 8 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
        try {
          var dt = DateTime.FromFileTimeUtc(value * 10); // µs → 100ns ticks
          display = $"{dt:yyyy-MM-dd HH:mm:ss.fff} UTC";
        }
        catch { display = $"(invalid: {value})"; }
        offset += 8;
        break;
      }

      // ── Fixed-point (Q-format) ───────────────────────────────────────
      case FieldType.Q8_8LE: {
        if (offset + 2 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);
        display = (value / 256.0).ToString("F4", CultureInfo.InvariantCulture);
        offset += 2;
        break;
      }
      case FieldType.Q16_16LE: {
        if (offset + 4 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        display = (value / 65536.0).ToString("F6", CultureInfo.InvariantCulture);
        offset += 4;
        break;
      }
      case FieldType.UQ8_8LE: {
        if (offset + 2 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        display = (value / 256.0).ToString("F4", CultureInfo.InvariantCulture);
        offset += 2;
        break;
      }
      case FieldType.UQ16_16LE: {
        if (offset + 4 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        display = (value / 65536.0).ToString("F6", CultureInfo.InvariantCulture);
        offset += 4;
        break;
      }

      // ── BCD (packed decimal) ─────────────────────────────────────────
      case FieldType.Bcd8:
        value = data[offset];
        display = DecodeBcd(value, 2);
        offset += 1;
        break;
      case FieldType.Bcd16LE: {
        if (offset + 2 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        display = DecodeBcd(value, 4);
        offset += 2;
        break;
      }
      case FieldType.Bcd32LE: {
        if (offset + 4 > data.Length) return (0, "(EOF)");
        value = (long)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        display = DecodeBcd(value, 8);
        offset += 4;
        break;
      }

      // ── Color ────────────────────────────────────────────────────────
      case FieldType.Rgb24: {
        if (offset + 3 > data.Length) return (0, "(EOF)");
        var r = data[offset]; var g = data[offset + 1]; var b = data[offset + 2];
        value = (r << 16) | (g << 8) | b;
        display = $"#{r:X2}{g:X2}{b:X2} ({r},{g},{b})";
        offset += 3;
        break;
      }
      case FieldType.Rgba32: {
        if (offset + 4 > data.Length) return (0, "(EOF)");
        var r = data[offset]; var g = data[offset + 1]; var b = data[offset + 2]; var a = data[offset + 3];
        value = (r << 24) | (g << 16) | (b << 8) | a;
        display = $"#{r:X2}{g:X2}{b:X2}{a:X2} ({r},{g},{b},{a})";
        offset += 4;
        break;
      }
      case FieldType.Bgr24: {
        if (offset + 3 > data.Length) return (0, "(EOF)");
        var b2 = data[offset]; var g = data[offset + 1]; var r = data[offset + 2];
        value = (r << 16) | (g << 8) | b2;
        display = $"#{r:X2}{g:X2}{b2:X2} ({r},{g},{b2})";
        offset += 3;
        break;
      }
      case FieldType.Bgra32: {
        if (offset + 4 > data.Length) return (0, "(EOF)");
        var b2 = data[offset]; var g = data[offset + 1]; var r = data[offset + 2]; var a = data[offset + 3];
        value = (r << 24) | (g << 16) | (b2 << 8) | a;
        display = $"#{r:X2}{g:X2}{b2:X2}{a:X2} ({r},{g},{b2},{a})";
        offset += 4;
        break;
      }
      case FieldType.Rgb565LE: {
        if (offset + 2 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        var r5 = (int)((value >> 11) & 0x1F);
        var g6 = (int)((value >> 5) & 0x3F);
        var b5 = (int)(value & 0x1F);
        display = $"R={r5} G={g6} B={b5} (#{r5 * 255 / 31:X2}{g6 * 255 / 63:X2}{b5 * 255 / 31:X2})";
        offset += 2;
        break;
      }
      case FieldType.Rgb555LE: {
        if (offset + 2 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        var r5 = (int)((value >> 10) & 0x1F);
        var g5 = (int)((value >> 5) & 0x1F);
        var b5 = (int)(value & 0x1F);
        display = $"R={r5} G={g5} B={b5} (#{r5 * 255 / 31:X2}{g5 * 255 / 31:X2}{b5 * 255 / 31:X2})";
        offset += 2;
        break;
      }

      // ── Network & ID ─────────────────────────────────────────────────
      case FieldType.Ipv4: {
        if (offset + 4 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        display = $"{data[offset]}.{data[offset + 1]}.{data[offset + 2]}.{data[offset + 3]}";
        offset += 4;
        break;
      }
      case FieldType.Mac48: {
        if (offset + 6 > data.Length) return (0, "(EOF)");
        value = 0;
        for (var k = 0; k < 6; k++) value = (value << 8) | data[offset + k];
        display = $"{data[offset]:X2}:{data[offset + 1]:X2}:{data[offset + 2]:X2}:{data[offset + 3]:X2}:{data[offset + 4]:X2}:{data[offset + 5]:X2}";
        offset += 6;
        break;
      }
      case FieldType.FourCC: {
        if (offset + 4 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        var c0 = (char)data[offset]; var c1 = (char)data[offset + 1];
        var c2 = (char)data[offset + 2]; var c3 = (char)data[offset + 3];
        display = $"'{c0}{c1}{c2}{c3}' (0x{value:X8})";
        offset += 4;
        break;
      }

      // ── Special ──────────────────────────────────────────────────────
      case FieldType.Bool8:
        value = data[offset];
        display = value != 0 ? $"true ({value})" : "false (0)";
        offset += 1;
        break;
      case FieldType.GuidLE: {
        if (offset + 16 > data.Length) return (0, "(EOF)");
        var guid = new Guid(data.Slice(offset, 16));
        value = 0;
        display = guid.ToString();
        offset += 16;
        break;
      }

      // ── Large integers ──────────────────────────────────────────────
      case FieldType.U96LE: {
        if (offset + 12 > data.Length) return (0, "(EOF)");
        value = 0;
        display = FormatBigHex(data, offset, 12, true);
        offset += 12;
        break;
      }
      case FieldType.U96BE: {
        if (offset + 12 > data.Length) return (0, "(EOF)");
        value = 0;
        display = FormatBigHex(data, offset, 12, false);
        offset += 12;
        break;
      }
      case FieldType.U128LE: {
        if (offset + 16 > data.Length) return (0, "(EOF)");
        value = 0;
        display = FormatBigHex(data, offset, 16, true);
        offset += 16;
        break;
      }
      case FieldType.U128BE: {
        if (offset + 16 > data.Length) return (0, "(EOF)");
        value = 0;
        display = FormatBigHex(data, offset, 16, false);
        offset += 16;
        break;
      }
      case FieldType.I128LE: {
        if (offset + 16 > data.Length) return (0, "(EOF)");
        value = 0;
        var neg = (data[offset + 15] & 0x80) != 0;
        display = (neg ? "-" : "") + FormatBigHex(data, offset, 16, true);
        offset += 16;
        break;
      }
      case FieldType.I128BE: {
        if (offset + 16 > data.Length) return (0, "(EOF)");
        value = 0;
        var neg = (data[offset] & 0x80) != 0;
        display = (neg ? "-" : "") + FormatBigHex(data, offset, 16, false);
        offset += 16;
        break;
      }
      case FieldType.U256LE: {
        if (offset + 32 > data.Length) return (0, "(EOF)");
        value = 0;
        display = FormatBigHex(data, offset, 32, true);
        offset += 32;
        break;
      }
      case FieldType.U256BE: {
        if (offset + 32 > data.Length) return (0, "(EOF)");
        value = 0;
        display = FormatBigHex(data, offset, 32, false);
        offset += 32;
        break;
      }
      case FieldType.U512LE: {
        if (offset + 64 > data.Length) return (0, "(EOF)");
        value = 0;
        display = FormatBigHex(data, offset, 64, true);
        offset += 64;
        break;
      }
      case FieldType.U512BE: {
        if (offset + 64 > data.Length) return (0, "(EOF)");
        value = 0;
        display = FormatBigHex(data, offset, 64, false);
        offset += 64;
        break;
      }

      // ── ML / Tiny floats ────────────────────────────────────────────
      case FieldType.BF16LE: {
        if (offset + 2 > data.Length) return (0, "(EOF)");
        var raw = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        value = raw;
        var f32bits = raw << 16; // BF16 = upper 16 bits of float32
        display = BitConverter.Int32BitsToSingle(f32bits).ToString(CultureInfo.InvariantCulture);
        offset += 2;
        break;
      }
      case FieldType.BF16BE: {
        if (offset + 2 > data.Length) return (0, "(EOF)");
        var raw = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        value = raw;
        var f32bits = raw << 16;
        display = BitConverter.Int32BitsToSingle(f32bits).ToString(CultureInfo.InvariantCulture);
        offset += 2;
        break;
      }
      case FieldType.FP8E4M3: {
        value = data[offset];
        display = DecodeFP8E4M3(data[offset]).ToString(CultureInfo.InvariantCulture);
        offset += 1;
        break;
      }
      case FieldType.FP8E5M2: {
        value = data[offset];
        display = DecodeFP8E5M2(data[offset]).ToString(CultureInfo.InvariantCulture);
        offset += 1;
        break;
      }

      // ── More fixed-point ────────────────────────────────────────────
      case FieldType.Q32_32LE: {
        if (offset + 8 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
        display = (value / 4294967296.0).ToString("F10", CultureInfo.InvariantCulture);
        offset += 8;
        break;
      }
      case FieldType.UQ32_32LE: {
        if (offset + 8 > data.Length) return (0, "(EOF)");
        value = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
        display = ((ulong)value / 4294967296.0).ToString("F10", CultureInfo.InvariantCulture);
        offset += 8;
        break;
      }

      // ── More color ──────────────────────────────────────────────────
      case FieldType.Rgba5551LE: {
        if (offset + 2 > data.Length) return (0, "(EOF)");
        value = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        var r5 = (int)((value >> 11) & 0x1F);
        var g5 = (int)((value >> 6) & 0x1F);
        var b5 = (int)((value >> 1) & 0x1F);
        var a1 = (int)(value & 1);
        display = $"R={r5} G={g5} B={b5} A={a1} (#{r5 * 255 / 31:X2}{g5 * 255 / 31:X2}{b5 * 255 / 31:X2})";
        offset += 2;
        break;
      }

      // ── IPv6 ────────────────────────────────────────────────────────
      case FieldType.Ipv6: {
        if (offset + 16 > data.Length) return (0, "(EOF)");
        value = 0;
        var v6sb = new StringBuilder(39);
        for (var k = 0; k < 8; k++) {
          if (k > 0) v6sb.Append(':');
          var word = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + k * 2)..]);
          v6sb.Append(word.ToString("x4"));
        }
        display = v6sb.ToString();
        offset += 16;
        break;
      }

      default:
        value = 0;
        display = "(unknown type)";
        break;
    }

    return (value, display);
  }
}

/// <summary>A single parsed field with its offset, size, and display value.</summary>
public sealed record ParsedField(
  string Name,
  string TypeName,
  int Offset,
  int Size,
  string? DisplayValue,
  List<ParsedField>? Children
);
