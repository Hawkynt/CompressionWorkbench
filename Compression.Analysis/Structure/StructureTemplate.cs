#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace Compression.Analysis.Structure;

/// <summary>
/// Represents a parsed .cwbt structure template containing one or more struct definitions.
/// </summary>
public sealed class StructureTemplate {
  /// <summary>Template name.</summary>
  public required string Name { get; init; }

  /// <summary>Struct definitions in declaration order.</summary>
  public required List<StructDefinition> Structs { get; init; }

  /// <summary>The entry-point struct (first defined).</summary>
  public StructDefinition Root => Structs[0];
}

/// <summary>A struct definition with named fields.</summary>
public sealed class StructDefinition {
  /// <summary>Struct name.</summary>
  public required string Name { get; init; }

  /// <summary>Fields in declaration order.</summary>
  public required List<FieldDefinition> Fields { get; init; }
}

/// <summary>A field within a struct definition.</summary>
public sealed class FieldDefinition {
  /// <summary>Field name.</summary>
  public required string Name { get; init; }

  /// <summary>The data type of this field.</summary>
  public required FieldType Type { get; init; }

  /// <summary>For primitive types, the byte width. 0 for variable-length.</summary>
  public int FixedSize { get; init; }

  /// <summary>For array types (char[N], u8[N]): a literal count, a field name reference, or "*" for repeat-to-EOF.</summary>
  public string? ArrayLength { get; init; }

  /// <summary>For nested struct references, the struct name.</summary>
  public string? StructRef { get; init; }

  /// <summary>For typed arrays (e.g., u16le[4]), the element type.</summary>
  public FieldType ElementType { get; init; }

  /// <summary>For typed arrays, the element byte size.</summary>
  public int ElementSize { get; init; }

  /// <summary>For bitfields, the number of bits.</summary>
  public int BitCount { get; init; }
}

/// <summary>Supported field data types.</summary>
public enum FieldType {
  // Unsigned integers
  U8, U16LE, U16BE, U32LE, U32BE, U64LE, U64BE,
  // Signed integers
  I8, I16LE, I16BE, I32LE, I32BE, I64LE, I64BE,
  // IEEE floats
  F32LE, F32BE, F64LE, F64BE,
  F16LE, F16BE,

  // Date & Time
  UnixTs32LE, UnixTs32BE, UnixTs64LE, UnixTs64BE,
  DosDate, DosTime,
  FileTime, OleDate, HfsDate, NetTicks, WebKitTime,

  // Fixed-point (Q-format)
  Q8_8LE, Q16_16LE, UQ8_8LE, UQ16_16LE,

  // BCD (packed decimal)
  Bcd8, Bcd16LE, Bcd32LE,

  // Color
  Rgb24, Rgba32, Bgr24, Bgra32, Rgb565LE, Rgb555LE,

  // Large integers (displayed as hex)
  U96LE, U96BE, U128LE, U128BE, I128LE, I128BE,
  U256LE, U256BE, U512LE, U512BE,

  // ML / Tiny floats
  BF16LE, BF16BE,    // BFloat16 (brain float)
  FP8E4M3, FP8E5M2,  // 8-bit ML floats

  // More fixed-point
  Q32_32LE, UQ32_32LE,

  // More color
  Rgba5551LE,

  // Network & ID
  Ipv4, Ipv6, Mac48, FourCC,

  // Special
  Bool8, GuidLE,

  // Meta types (not read by ReadPrimitive)
  CharArray,   // char[N]
  ByteArray,   // u8[N]
  TypedArray,  // type[N] for non-u8/char arrays (e.g., u16le[4], u32be[2])
  Bits,        // bits[N] — N-bit bitfield extracted from the current byte(s)
  StructRef,   // nested struct
}
