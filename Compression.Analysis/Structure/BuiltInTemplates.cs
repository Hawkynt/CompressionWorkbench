namespace Compression.Analysis.Structure;

/// <summary>
/// Provides built-in structure templates for common binary formats.
/// </summary>
public static class BuiltInTemplates {

  /// <summary>All available built-in templates, keyed by name.</summary>
  public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string> {
    ["ZIP Local File Header"] = ZipLocalHeader,
    ["PNG Header"] = PngHeader,
    ["BMP Header"] = BmpHeader,
    ["ELF Header"] = ElfHeader,
    ["Gzip Header"] = GzipHeader,
  };

  /// <summary>ZIP Local File Header (PK\x03\x04).</summary>
  public const string ZipLocalHeader = """
    struct ZipLocalHeader {
      u32le signature;
      u16le versionNeeded;
      u16le flags;
      u16le method;
      u16le modTime;
      u16le modDate;
      u32le crc32;
      u32le compressedSize;
      u32le uncompressedSize;
      u16le nameLength;
      u16le extraLength;
      char[nameLength] fileName;
      u8[extraLength] extraField;
    };
    """;

  /// <summary>PNG file header (signature + IHDR chunk).</summary>
  public const string PngHeader = """
    struct PngHeader {
      u8[8] signature;
      u32be ihdrLength;
      u8[4] ihdrType;
      u32be width;
      u32be height;
      u8 bitDepth;
      u8 colorType;
      u8 compressionMethod;
      u8 filterMethod;
      u8 interlaceMethod;
      u32be ihdrCrc;
    };
    """;

  /// <summary>BMP file header.</summary>
  public const string BmpHeader = """
    struct BmpHeader {
      u16le signature;
      u32le fileSize;
      u16le reserved1;
      u16le reserved2;
      u32le dataOffset;
      u32le headerSize;
      i32le width;
      i32le height;
      u16le planes;
      u16le bitsPerPixel;
      u32le compression;
      u32le imageSize;
      i32le xPixelsPerMeter;
      i32le yPixelsPerMeter;
      u32le colorsUsed;
      u32le colorsImportant;
    };
    """;

  /// <summary>ELF file header (64-bit little-endian).</summary>
  public const string ElfHeader = """
    struct ElfHeader {
      u8[4] magic;
      u8 class;
      u8 data;
      u8 version;
      u8 osabi;
      u8[8] padding;
      u16le type;
      u16le machine;
      u32le elfVersion;
      u64le entry;
      u64le phoff;
      u64le shoff;
      u32le flags;
      u16le ehsize;
      u16le phentsize;
      u16le phnum;
      u16le shentsize;
      u16le shnum;
      u16le shstrndx;
    };
    """;

  /// <summary>Gzip header (RFC 1952).</summary>
  public const string GzipHeader = """
    struct GzipHeader {
      u8 id1;
      u8 id2;
      u8 method;
      u8 flags;
      u32le mtime;
      u8 xfl;
      u8 os;
    };
    """;
}
