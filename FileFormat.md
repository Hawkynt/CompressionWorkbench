# FileFormat Architecture

How to build metadata-compatible file format libraries across any domain — image, audio, video, compression, crypto — with idiot-proof implementations, zero hand-written parsing for headers, and automatic format discovery.

## Design Goals

1. **Idiot-proof to implement** — the compiler enforces completeness; forget a method and it won't build
2. **Easy to understand** — every format is one self-contained project, same file naming, same interface set
3. **No hand-written binary parsing** — source generator reads `[HeaderField]` annotations and emits `ReadFrom`/`WriteTo`
4. **Metadata extraction without domain knowledge** — a hex editor, format detector, or metadata viewer can query any format's extensions, magic bytes, capabilities, and header field map without understanding pixels, samples, or codecs
5. **Zero-cost abstraction** — all interface members are `static abstract`; dispatch resolves at compile time, no vtables, no boxing, no allocations
6. **Stateless** — format types are `readonly record struct`; readers and writers are `static` classes; no mutable state anywhere
7. **Low line count** — a headerless format is ~60 lines total; a typical header-based format is ~120 lines of hand-written code plus a generated serializer

## Building Blocks

### 1. The Intermediate Representation

Every domain needs a canonical, format-agnostic data container that sits between raw file bytes and the outside world. This is the single type that all formats convert to and from.

For images, this is `RawImage`:

```csharp
public class RawImage {
  public required int Width { get; init; }
  public required int Height { get; init; }
  public required PixelFormat Format { get; init; }
  public required byte[] PixelData { get; init; }
  public byte[]? Palette { get; init; }
  public byte[]? AlphaTable { get; init; }
}
```

For other domains, define the equivalent:
- **Audio**: `RawAudio { SampleRate, Channels, BitDepth, SampleData }`
- **Compression**: `Stream` input/output (already universal)
- **Video**: `RawFrame[]` or frame-by-frame `RawImage` sequence
- **Crypto**: `ReadOnlySpan<byte>` (raw bytes in, raw bytes out)

The IR is deliberately dumb — no codec knowledge, no format-specific fields, just the universal representation that every format can produce and consume.

### 2. The Interface Stack

Format capabilities are expressed as composable interfaces. A format implements only what it supports — read-only formats skip the writer interfaces, metadata-only formats skip pixel conversion.

```
IImageFormatMetadata<TSelf>          extensions, capabilities, magic bytes
  └─ IImageFormatReader<TSelf>       static FromSpan(ReadOnlySpan<byte>) → TSelf
       └─ IImageToRawImage<TSelf>    static ToRawImage(TSelf) → RawImage

IImageFormatWriter<TSelf>            static ToBytes(TSelf) → byte[]
  └─ IImageFromRawImage<TSelf>       static FromRawImage(RawImage) → TSelf

IImageInfoReader<TSelf>              static ReadImageInfo(header) → ImageInfo?  (fast, no pixel decode)
IMultiImageFileFormat<TSelf>         static ImageCount(TSelf), ToRawImage(TSelf, index)
```

**Why `static abstract` on `TSelf`?** Because the compiler monomorphizes each call site. `FormatIO.Decode<QoiFile>(bytes)` compiles to a direct call to `QoiFile.FromSpan` — no interface dispatch, no virtual table lookup, no allocation. The generic constraint is the abstraction; the runtime cost is zero.

**Why separate `IImageFormatReader` from `IImageToRawImage`?** Because some consumers want the parsed format-specific struct (header fields, metadata, compression info) without converting to the IR. A hex viewer wants `QoiFile.Header`; an optimizer wants `PngFile.Chunks`. The IR conversion is a separate, optional step.

### 3. The Format Data Model

Every format is a `readonly record struct` — value type, immutable, no heap allocation for the struct itself (pixel data is a `byte[]` on the heap, but the struct that holds it is stack-allocated).

```csharp
[FormatMagicBytes([0x71, 0x6F, 0x69, 0x66])]  // "qoif"
public readonly record struct QoiFile :
  IImageFormatReader<QoiFile>,
  IImageToRawImage<QoiFile>,
  IImageFromRawImage<QoiFile>,
  IImageFormatWriter<QoiFile> {

  // --- Metadata (static, compile-time) ---
  static string IImageFormatMetadata<QoiFile>.PrimaryExtension => ".qoi";
  static string[] IImageFormatMetadata<QoiFile>.FileExtensions => [".qoi"];

  // --- Data (instance, per-file) ---
  public int Width { get; init; }
  public int Height { get; init; }
  public QoiChannels Channels { get; init; }
  public byte[] PixelData { get; init; }

  // --- Delegation to Reader/Writer ---
  static QoiFile IImageFormatReader<QoiFile>.FromSpan(ReadOnlySpan<byte> data)
    => QoiReader.FromSpan(data);
  static byte[] IImageFormatWriter<QoiFile>.ToBytes(QoiFile file)
    => QoiWriter.ToBytes(file);

  // --- IR conversion ---
  static RawImage IImageToRawImage<QoiFile>.ToRawImage(QoiFile file) => new() {
    Width = file.Width, Height = file.Height,
    Format = file.Channels == QoiChannels.Rgba ? PixelFormat.Rgba32 : PixelFormat.Rgb24,
    PixelData = file.PixelData
  };
  static QoiFile IImageFromRawImage<QoiFile>.FromRawImage(RawImage image) => new() {
    Width = image.Width, Height = image.Height,
    Channels = image.Format == PixelFormat.Rgba32 ? QoiChannels.Rgba : QoiChannels.Rgb,
    PixelData = image.PixelData
  };
}
```

**Why `readonly record struct`?** Immutability eliminates entire categories of bugs. Record equality lets you compare two parsed files with `==`. Struct layout avoids GC pressure for the container itself.

### 4. The Header Serializer Generator

Binary headers are the most error-prone part of format implementation — endianness bugs, off-by-one offsets, bit packing mistakes. The `[GenerateSerializer]` source generator eliminates all of this.

The generator is modelled after [Kaitai Struct](https://kaitai.io/)'s declarative philosophy: describe the binary layout once using attributes, and the compiler emits correct `ReadFrom`/`WriteTo` code with zero hand-written `BinaryPrimitives` calls.

#### 4.1 Attribute Taxonomy

```
Core
├── [GenerateSerializer]              Triggers source generation on the type
├── [Endian(Little|Big)]              Default byte order for all fields in this type
└── [StructSize(N)]                   Declare total byte size (asserted at compile time)

Field Positioning
├── [Field(offset, size)]             Fixed position: absolute byte offset + size
├── [Field(offset, size, Endian=Big)] Override endianness for one field
└── [SeqField]                        Sequential: position = previous field's end
    └── [SeqField(Size=N)]            Explicit size in sequential mode

Bit-Level Access
├── [BitField(byteOff, bitOff, N)]   Extract N bits starting at bit bitOff of byte byteOff
├── [BitFlags]                        Interpret a field as a [Flags] enum
└── [PackedField(container, mask)]    Extract via bitmask from a named container field

Type Variants
├── [StringField(enc)]                Fixed-size string: "ASCII", "UTF-8", "Latin1"
├── [NullTermString(enc)]             Variable-length null-terminated string
├── [OctalString]                     Octal-encoded integer (TAR headers)
├── [RawBytes]                        Uninterpreted byte array
└── [EnumField(typeof(T))]            Cast raw integer to enum T

Conditional & Dynamic
├── [If(field, op, value)]            Parse only when condition is true
├── [SizedBy(field)]                  Byte count comes from another field's value
├── [SwitchOn(field)]                 Discriminated union: type depends on field value
│   └── [Case(value, typeof(T))]      Maps a discriminator value to a sub-type
├── [Repeat(field)]                   Array of N items, N from another field
├── [Repeat(N)]                       Fixed-count array
├── [RepeatUntil(sentinel)]           Repeat until a sentinel value
└── [RepeatEos]                       Repeat until end of buffer

Validation
├── [Valid(value)]                     Exact match (magic bytes, signatures)
├── [ValidRange(min, max)]            Inclusive range check
├── [ValidAnyOf(v1, v2, ...)]         Whitelist of allowed values
└── [ValidExpr("_ % 2 == 0")]         Expression-based validation

Computed (not in binary)
├── [Computed("Width * Height")]       Derived from other fields
└── [Computed(field, "_ >> 56")]       Transform of another field

Integrity
├── [Checksum(algo, start, end)]       CRC32/Adler32 over byte range
└── [ChecksumField(algo, field)]       Checksum covering another field's span

Processing
├── [Process(Xor, key)]               XOR transform before parsing
├── [Process(Zlib)]                    Inflate before parsing
└── [Process(Custom, typeof(T))]       Custom IFieldProcessor implementation

Layout
├── [Filler(offset, size)]            Skip reserved/padding bytes
└── [Align(N)]                         Align next field to N-byte boundary
```

#### 4.2 Fixed-Layout Mode — Absolute Offsets

For headers where every field has a known byte position (the common case for file format specs), use `[Field(offset, size)]`:

```csharp
[GenerateSerializer, Endian(Little), StructSize(14)]
public readonly partial record struct QoiHeader(
  [property: Field(0, 4, Endian = Big)] uint Magic,
  [property: Field(4, 4, Endian = Big)] uint Width,
  [property: Field(8, 4, Endian = Big)] uint Height,
  [property: Field(12, 1)] byte Channels,
  [property: Field(13, 1)] byte ColorSpace
);
```

**Generated code:**

```csharp
// QoiHeader.g.cs (auto-generated)
public readonly partial record struct QoiHeader {
  public static QoiHeader ReadFrom(ReadOnlySpan<byte> source) => new(
    BinaryPrimitives.ReadUInt32BigEndian(source),
    BinaryPrimitives.ReadUInt32BigEndian(source[4..]),
    BinaryPrimitives.ReadUInt32BigEndian(source[8..]),
    source[12],
    source[13]
  );

  public void WriteTo(Span<byte> dest) {
    BinaryPrimitives.WriteUInt32BigEndian(dest, Magic);
    BinaryPrimitives.WriteUInt32BigEndian(dest[4..], Width);
    BinaryPrimitives.WriteUInt32BigEndian(dest[8..], Height);
    dest[12] = Channels;
    dest[13] = ColorSpace;
  }
}
```

#### 4.3 Sequential Mode — Variable-Length Headers

For headers where fields are read in order and later fields depend on earlier ones (ZIP, GZIP), use `[SeqField]`. The generator emits a cursor-based reader:

```csharp
[GenerateSerializer(Sequential), Endian(Little)]
public readonly partial record struct GzipHeader(
  [property: SeqField, Valid((byte)0x1F)]     byte Magic1,
  [property: SeqField, Valid((byte)0x8B)]     byte Magic2,
  [property: SeqField]                        byte Method,
  [property: SeqField, BitFlags]              GzipFlags Flags,
  [property: SeqField(Size = 4)]              uint ModificationTime,
  [property: SeqField]                        byte ExtraFlags,
  [property: SeqField]                        byte OperatingSystem,
  // --- conditional variable-length tail ---
  [property: SeqField, If(nameof(Flags), Op.HasFlag, GzipFlags.FExtra),
             SizedBy(Size = 2, Prefix = true)]  byte[]? ExtraField,
  [property: SeqField, If(nameof(Flags), Op.HasFlag, GzipFlags.FName),
             NullTermString("Latin1")]          string? FileName,
  [property: SeqField, If(nameof(Flags), Op.HasFlag, GzipFlags.FComment),
             NullTermString("Latin1")]          string? Comment,
  [property: SeqField, If(nameof(Flags), Op.HasFlag, GzipFlags.FHcrc),
             SeqField(Size = 2)]                ushort? HeaderCrc
);
```

**Generated code** uses a `ref int pos` cursor instead of absolute offsets:

```csharp
public static GzipHeader ReadFrom(ReadOnlySpan<byte> source) {
  var pos = 0;
  var magic1 = source[pos++];
  if (magic1 != 0x1F) throw new InvalidDataException("...");
  var magic2 = source[pos++];
  if (magic2 != 0x8B) throw new InvalidDataException("...");
  var method = source[pos++];
  var flags = (GzipFlags)source[pos++];
  var modTime = BinaryPrimitives.ReadUInt32LittleEndian(source[pos..]); pos += 4;
  var extraFlags = source[pos++];
  var os = source[pos++];

  byte[]? extraField = null;
  if (flags.HasFlag(GzipFlags.FExtra)) {
    var len = BinaryPrimitives.ReadUInt16LittleEndian(source[pos..]); pos += 2;
    extraField = source.Slice(pos, len).ToArray(); pos += len;
  }

  string? fileName = null;
  if (flags.HasFlag(GzipFlags.FName)) {
    var end = source[pos..].IndexOf((byte)0);
    fileName = Encoding.Latin1.GetString(source.Slice(pos, end)); pos += end + 1;
  }
  // ... Comment, HeaderCrc similarly ...

  return new(magic1, magic2, method, flags, modTime, extraFlags, os,
             extraField, fileName, comment, headerCrc);
}
```

#### 4.4 Bit-Packing — Sub-Word Field Extraction

For fields packed into a single integer (WIM resource entries: 56-bit size + 8-bit flags in one `uint64`):

```csharp
[GenerateSerializer, Endian(Little), StructSize(24)]
public readonly partial record struct WimResourceEntry(
  [property: BitField(0, 0, 56)]  long CompressedSize,   // bits 0–55 of qword at offset 0
  [property: BitField(0, 56, 8)]  byte Flags,             // bits 56–63 of same qword
  [property: Field(8, 8)]         long Offset,
  [property: Field(16, 8)]        long OriginalSize
);
```

**Generated code** reads the container once, then masks/shifts:

```csharp
public static WimResourceEntry ReadFrom(ReadOnlySpan<byte> source) {
  var qw0 = BinaryPrimitives.ReadUInt64LittleEndian(source);
  return new(
    (long)(qw0 & 0x00FFFFFFFFFFFFFF),
    (byte)(qw0 >> 56),
    BinaryPrimitives.ReadInt64LittleEndian(source[8..]),
    BinaryPrimitives.ReadInt64LittleEndian(source[16..])
  );
}

public void WriteTo(Span<byte> dest) {
  var qw0 = ((ulong)CompressedSize & 0x00FFFFFFFFFFFFFF) | ((ulong)Flags << 56);
  BinaryPrimitives.WriteUInt64LittleEndian(dest, qw0);
  BinaryPrimitives.WriteInt64LittleEndian(dest[8..], Offset);
  BinaryPrimitives.WriteInt64LittleEndian(dest[16..], OriginalSize);
}
```

#### 4.5 Nested Structures

Reference another `[GenerateSerializer]` type as a field. The generator calls the nested type's `ReadFrom`/`WriteTo`:

```csharp
[GenerateSerializer, Endian(Little), StructSize(208)]
public readonly partial record struct WimHeader(
  [property: Field(0, 8), RawBytes]              byte[] Magic,
  [property: Field(8, 4)]                        uint HeaderSize,
  [property: Field(12, 4)]                       uint Version,
  [property: Field(16, 4)]                       uint WimFlags,
  [property: Field(20, 4)]                       uint ChunkSize,
  [property: Field(24, 16), RawBytes]            byte[] Guid,
  [property: Field(40, 2)]                       ushort PartNumber,
  [property: Field(42, 2)]                       ushort TotalParts,
  [property: Field(44, 4)]                       uint ImageCount,
  [property: Field(48, 24)]                      WimResourceEntry OffsetTable,      // nested
  [property: Field(72, 24)]                      WimResourceEntry XmlData,          // nested
  [property: Field(96, 24)]                      WimResourceEntry BootMetadata,     // nested
  [property: Field(120, 4)]                      uint BootIndex,
  [property: Field(124, 24)]                     WimResourceEntry IntegrityTable,   // nested
  [property: Filler(148, 60)]                    // reserved bytes 148–207
  [property: Computed("DeduceCompression(WimFlags)")]
                                                  uint CompressionType
) {
  public const int Size = 208;

  private static uint DeduceCompression(uint flags) =>
    (flags & 0x00040000) != 0 ? 4 :  // LZMS
    (flags & 0x00200000) != 0 ? 3 :  // XPRESS Huffman
    (flags & 0x00080000) != 0 ? 2 :  // LZX
    (flags & 0x00020000) != 0 ? 1 :  // XPRESS
    0;                                // None
}
```

#### 4.6 Octal Strings and Domain-Specific Encodings

TAR headers encode numbers as null-terminated octal ASCII strings. The `[OctalString]` attribute handles this:

```csharp
[GenerateSerializer, StructSize(512)]
public readonly partial record struct TarHeader(
  [property: Field(0, 100),   StringField("ASCII")]  string Name,
  [property: Field(100, 8),   OctalString]           int Mode,
  [property: Field(108, 8),   OctalString]           int Uid,
  [property: Field(116, 8),   OctalString]           int Gid,
  [property: Field(124, 12),  OctalString]           long Size,
  [property: Field(136, 12),  OctalString]           long Mtime,
  [property: Field(148, 8),   Checksum(ChecksumAlgo.TarChecksum, 0, 512)]
                                                      int Checksum,
  [property: Field(156, 1)]                           byte TypeFlag,
  [property: Field(157, 100), StringField("ASCII")]  string LinkName,
  [property: Field(257, 6),   StringField("ASCII"), Valid("ustar")]
                                                      string Magic,
  [property: Field(263, 2),   StringField("ASCII")]  string Version,
  [property: Field(265, 32),  StringField("ASCII")]  string Uname,
  [property: Field(297, 32),  StringField("ASCII")]  string Gname,
  [property: Field(329, 8),   OctalString]           int DevMajor,
  [property: Field(337, 8),   OctalString]           int DevMinor,
  [property: Field(345, 155), StringField("ASCII")]  string Prefix
);
```

The generator emits helpers for octal parsing:

```csharp
// Generated
var mode = ParseOctal(source.Slice(100, 8));   // "0000755\0" → 493
var size = ParseOctalLong(source.Slice(124, 12));

private static long ParseOctalLong(ReadOnlySpan<byte> field) {
  // Handles null-terminated octal string, with GNU binary extension fallback
  // (high bit set → raw big-endian integer)
  if (field[0] >= 0x80)
    return ReadBinarySize(field);
  var s = Encoding.ASCII.GetString(field).TrimEnd('\0', ' ');
  return s.Length == 0 ? 0 : Convert.ToInt64(s, 8);
}
```

#### 4.7 Switch/Discriminated Unions

For formats with multiple block types sharing a common header (ZIP sections, ARC header versions):

```csharp
[GenerateSerializer(Sequential), Endian(Little)]
public readonly partial record struct ZipSection(
  [property: SeqField(Size = 4)] uint Signature,
  [property: SeqField, SwitchOn(nameof(Signature))]
  [Case(0x04034B50u, typeof(ZipLocalFileHeader))]
  [Case(0x02014B50u, typeof(ZipCentralDirEntry))]
  [Case(0x06054B50u, typeof(ZipEndOfCentralDir))]
  [Case(0x06064B50u, typeof(Zip64EndOfCentralDir))]
  object Body
);
```

For TLV (tag-length-value) loops like ZIP extra fields:

```csharp
[GenerateSerializer(Sequential), Endian(Little)]
public readonly partial record struct ZipExtraField(
  [property: SeqField(Size = 2)]                  ushort Tag,
  [property: SeqField(Size = 2)]                  ushort DataSize,
  [property: SeqField, SizedBy(nameof(DataSize)),
   SwitchOn(nameof(Tag))]
  [Case(0x0001, typeof(Zip64ExtendedInfo))]
  [Case(0x000A, typeof(NtfsExtraField))]
  object Data
);

[GenerateSerializer, Endian(Little), StructSize(28)]
public readonly partial record struct Zip64ExtendedInfo(
  [property: Field(0, 8)]  long UncompressedSize,
  [property: Field(8, 8)]  long CompressedSize,
  [property: Field(16, 8)] long HeaderOffset,
  [property: Field(24, 4)] uint DiskNumber
);
```

#### 4.8 Repetition

```csharp
// Fixed count
[property: Field(44, 4)] uint ImageCount,
[property: SeqField, Repeat(nameof(ImageCount))]
  WimResourceEntry[] Resources,

// Until end of buffer
[property: SeqField, RepeatEos]
  TarHeader[] Headers,

// Until sentinel
[property: SeqField, RepeatUntil(0x00)]
  ZipExtraField[] ExtraFields,
```

#### 4.9 Supported `[Field]` Feature Matrix

| Feature | Attribute | Example | Kaitai Equivalent |
| --- | --- | --- | --- |
| Absolute position + size | `Field(offset, size)` | `Field(8, 4)` | `seq` with explicit `pos` |
| Sequential position | `SeqField` | (inferred from decl order) | `seq` default |
| Byte order | `Endian(Big)` | class-level or per-field | `meta.endian`, `u4be` |
| Runtime byte order | `EndianField("ByteOrder")` | TIFF II/MM | `meta.endian: ...` switch |
| Enum cast | `EnumField(typeof(T))` | `ZipCompressionMethod` | `enum: ...` |
| Fixed array | `Repeat(N)` | `uint[4]` mipmap offsets | `repeat: expr` |
| Bit extraction | `BitField(byte, bit, N)` | WIM 56+8 packing | `type: b5` |
| Bit flags | `BitFlags` | GZIP flags | — |
| Packed bitmask | `PackedField(container, mask)` | `_ & 0x1F` | expression |
| ASCII string | `StringField("ASCII")` | TAR name field | `type: str, encoding: ASCII` |
| Null-terminated string | `NullTermString("Latin1")` | GZIP filename | `type: strz` |
| Octal string | `OctalString` | TAR size/mode | — (custom) |
| Raw byte array | `RawBytes` | WIM GUID | no type + `size` |
| Conditional | `If(field, op, value)` | GZIP optional fields | `if: ...` |
| Size from field | `SizedBy(field)` | ZIP filename | `size: len_file_name` |
| Size until end | `SizeEos` | remaining bytes | `size-eos: true` |
| Discriminated union | `SwitchOn(field)` + `Case` | ZIP section types | `switch-on` + `cases` |
| Counted repetition | `Repeat(field)` | entry count | `repeat: expr` |
| Sentinel repetition | `RepeatUntil(value)` | zero-block | `repeat: until` |
| EOS repetition | `RepeatEos` | read all | `repeat: eos` |
| Exact value | `Valid(value)` | magic bytes | `valid: ...` |
| Range check | `ValidRange(min, max)` | version bounds | `valid: { min, max }` |
| Whitelist | `ValidAnyOf(...)` | allowed methods | `valid: { any-of }` |
| Expression check | `ValidExpr("...")` | alignment | `valid: { expr }` |
| Derived value | `Computed("expr")` | not in binary | `instances` with `value` |
| CRC/checksum | `Checksum(algo, start, end)` | TAR, ZIP | — |
| Skip bytes | `Filler(offset, size)` | reserved | — |
| Alignment | `Align(N)` | sector align | — |
| Byte transform | `Process(Xor, key)` | obfuscated | `process: xor(key)` |
| Sub-struct | (type reference) | WIM resource entry | `types` section |

### 5. Format Detection

Every format declares its magic bytes via attributes:

```csharp
[FormatMagicBytes([0x89, 0x50, 0x4E, 0x47], offset: 0)]   // PNG: 4 bytes at offset 0
[FormatMagicBytes([0xFF, 0xD8, 0xFF])]                       // JPEG: 3 bytes at offset 0
[FormatDetectionPriority(50)]                                 // Check before default-priority formats
```

For formats with ambiguous or absent magic bytes, override the static virtual:

```csharp
static bool? IImageFormatMetadata<TiffFile>.MatchesSignature(ReadOnlySpan<byte> header) {
  if (header.Length < 4) return null;
  // TIFF: "II" (little-endian) or "MM" (big-endian) + version 42
  var bo = BinaryPrimitives.ReadUInt16LittleEndian(header);
  if (bo != 0x4949 && bo != 0x4D4D) return false;
  var version = bo == 0x4949
    ? BinaryPrimitives.ReadUInt16LittleEndian(header[2..])
    : BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
  return version == 42 ? true : null;  // true = match, null = unsure, false = definitely not this format
}
```

**Tri-state return:** `true` (match), `false` (definitely not this format — skip magic byte check), `null` (unsure — fall back to magic bytes). This handles TIFF-based formats like DNG vs CameraRaw that share the same magic but differ in IFD tags.

**Confidence-based detection (compression domain):** CompressionWorkbench uses `MagicSignature` records with a `double Confidence` (0.0–1.0) field instead of tri-state, allowing the detector to rank matches when multiple formats share the same magic bytes (e.g., all OLE2 formats share `D0 CF 11 E0`). Extension-only detection uses `MagicSignatures => []`.

At compile time, the source generator extracts all `[FormatMagicBytes]` and `[FormatDetectionPriority]` values and emits them directly into the registration code — no runtime reflection needed to read attributes.

### 6. Format Registration (Zero Reflection)

A Roslyn incremental source generator (`ImageFormatGenerator`) scans all referenced assemblies at compile time and generates two files:

**`ImageFormat.g.cs`** — enum with all discovered formats:
```csharp
public enum ImageFormat {
  Unknown,
  Bam,
  Bmp,
  // ... alphabetically sorted, one entry per IImageFormatReader<T> implementation
  Qoi,
  ZxSpectrum,
}
```

**`FormatRegistration.g.cs`** — typed registration calls:
```csharp
static partial void RegisterAll() {
  _RegisterReaderWriter<global::FileFormat.Qoi.QoiFile>(
    ImageFormat.Qoi,
    new MagicSignature[] { new(new byte[] { 0x71, 0x6F, 0x69, 0x66 }, 0, 4) },
    100
  );
  _RegisterMultiImageReader<global::FileFormat.Apng.ApngFile>(ImageFormat.Apng);
  _AugmentInfoReader<global::FileFormat.Png.PngFile>(ImageFormat.Png);
  // ... one call per format
}
```

**Zero runtime reflection.** No `Assembly.Load`, no `GetTypes()`, no `MakeGenericMethod`. The generic type parameter is resolved at compile time. This enables trimmed, AOT, and single-file publishing.

For detection-only formats without an `IImageFormatReader<T>` implementation (e.g., GIF using an external library), use the assembly attribute:

```csharp
[assembly: AdditionalImageFormat("Gif")]
```

### 7. Metadata Extraction

Three levels of metadata are available without domain-specific knowledge:

**Level 1 — Format identity (static, zero cost):**
```csharp
// Available at compile time, no file needed
T.PrimaryExtension    // ".qoi"
T.FileExtensions      // [".qoi"]
T.Capabilities        // FormatCapability.VariableResolution
```

**Level 2 — Header field map (static, zero cost):**
```csharp
// Generated from [Field] / [SeqField] annotations — hex editors, metadata viewers
QoiHeader.GetFieldMap()
// → [
//   FieldDescriptor("Magic",      offset: 0,  size: 4, endian: Big,    type: "uint"),
//   FieldDescriptor("Width",      offset: 4,  size: 4, endian: Big,    type: "uint"),
//   FieldDescriptor("Height",     offset: 8,  size: 4, endian: Big,    type: "uint"),
//   FieldDescriptor("Channels",   offset: 12, size: 1, endian: Native, type: "byte"),
//   FieldDescriptor("ColorSpace", offset: 13, size: 1, endian: Native, type: "byte"),
// ]

// For sequential headers, offsets are computed at runtime:
GzipHeader.GetFieldMap(headerBytes)
// → includes conditional fields with their actual offsets in this specific file
```

**Level 3 — Parsed metadata (fast, reads only the header):**
```csharp
// Reads first 4KB, parses header only — no pixel decode
var info = FormatIO.ReadInfo<PngFile>(fileBytes);
// → ImageInfo { Width: 1920, Height: 1080, BitsPerPixel: 32, ColorMode: "RGBA" }
```

A metadata viewer tool can load any `FileFormat.*.dll`, enumerate types implementing the interfaces, and extract all three levels without understanding any codec.

### 8. Capability Flags

```csharp
[Flags]
public enum FormatCapability {
  None                  = 0,
  VariableResolution    = 1,   // Supports arbitrary dimensions
  MonochromeOnly        = 2,   // Restricted to 1-bit
  IndexedOnly           = 4,   // Restricted to palette-based
  HasDedicatedOptimizer = 8,   // Has an Optimizer.* project
  MultiImage            = 16,  // Contains multiple frames/pages
}
```

These flags let generic tools make decisions without format-specific code:

- A converter filters `ConversionTargets` to formats that support `VariableResolution`
- A viewer enables frame navigation for formats with `MultiImage`
- A "Save As" dialog excludes formats with `HasDedicatedOptimizer` (they have their own CLI)

## Project Structure

### Per-Format Project

```
FileFormat.<Fmt>/
  FileFormat.<Fmt>.csproj    — references FileFormat.Core + FileFormat.Core.Generators (analyzer)
  <Fmt>File.cs               — readonly record struct, implements interfaces, delegates to reader/writer
  <Fmt>Reader.cs             — static class: FromSpan(), FromFile(), FromStream(), FromBytes()
  <Fmt>Writer.cs             — static class: ToBytes()
  <Fmt>Header.cs             — readonly partial record struct with [GenerateSerializer] + [Field]
  <Fmt>Codec.cs              — (optional) encoding/decoding logic if format uses compression
  <Fmt>ColorMode.cs          — (optional) enums for format-specific options
```

### Minimal .csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\FileFormat.Core\FileFormat.Core.csproj" />
    <ProjectReference Include="..\FileFormat.Core.Generators\FileFormat.Core.Generators.csproj"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
```

`Directory.Build.props` provides `LangVersion`, `Nullable`, `ImplicitUsings`, `AllowUnsafeBlocks` — individual projects only specify `TargetFramework`.

## Complete Example: Fixed-Layout Header (QCOW2 — Big-Endian Disk Image)

QCOW2 is a virtual disk image format with a 72-byte big-endian header. Demonstrates endianness control, validation, and computed fields.

```csharp
// Qcow2Header.cs — currently ~60 LOC of manual BinaryPrimitives, replaced by declaration
[GenerateSerializer, Endian(Big), StructSize(72)]
public readonly partial record struct Qcow2Header(
  [property: Field(0, 4),  Valid(0x514649FBu)]  uint Magic,         // "QFI\xFB"
  [property: Field(4, 4),  ValidAnyOf(2u, 3u)]  uint Version,
  [property: Field(8, 8)]                        ulong BackingFileOffset,
  [property: Field(16, 4)]                       uint BackingFileSize,
  [property: Field(20, 4), ValidRange(9u, 21u)]  uint ClusterBits,
  [property: Field(24, 8)]                        ulong VirtualSize,
  [property: Field(32, 4)]                        uint CryptMethod,
  [property: Field(36, 4)]                        uint L1Size,
  [property: Field(40, 8)]                        ulong L1TableOffset,
  [property: Field(48, 8)]                        ulong RefcountTableOffset,
  [property: Field(56, 4)]                        uint RefcountTableClusters,
  [property: Field(60, 4)]                        uint NbSnapshots,
  [property: Field(64, 8)]                        ulong SnapshotsOffset,
  // --- computed (not in binary) ---
  [property: Computed("1u << (int)ClusterBits")]  uint ClusterSize,
  [property: Computed("ClusterSize / 8")]         uint L2Entries
) {
  public const int Size = 72;
}
```

Zero hand-written `BinaryPrimitives` calls. The generator handles all big-endian reads/writes, validates magic and version on parse, and computes derived fields.

## Complete Example: Sequential Header with Conditionals (GZIP)

GZIP has a 10-byte fixed prefix followed by flag-dependent variable-length fields. Demonstrates the sequential mode with `[If]` conditions:

```csharp
[GenerateSerializer(Sequential), Endian(Little)]
public readonly partial record struct GzipHeader(
  [property: SeqField, Valid((byte)0x1F)]                    byte Magic1,
  [property: SeqField, Valid((byte)0x8B)]                    byte Magic2,
  [property: SeqField, Valid((byte)8)]                       byte Method,
  [property: SeqField]                                       GzipFlags Flags,
  [property: SeqField(Size = 4)]                             uint ModificationTime,
  [property: SeqField]                                       byte ExtraFlags,
  [property: SeqField]                                       byte OperatingSystem,
  [property: SeqField, If(nameof(Flags), Op.HasFlag, GzipFlags.FExtra),
             SizedBy(Size = 2, Prefix = true)]               byte[]? ExtraField,
  [property: SeqField, If(nameof(Flags), Op.HasFlag, GzipFlags.FName),
             NullTermString("Latin1")]                       string? FileName,
  [property: SeqField, If(nameof(Flags), Op.HasFlag, GzipFlags.FComment),
             NullTermString("Latin1")]                       string? Comment,
  [property: SeqField, If(nameof(Flags), Op.HasFlag, GzipFlags.FHcrc)]
                                                             ushort? HeaderCrc
);
```

## Complete Example: Compression Stream (Unix Compress — from CompressionWorkbench)

The same architecture works for compression and archive formats. The sibling repo [CompressionWorkbench](../CompressionWorkbench) uses an instance-based descriptor pattern instead of static abstract interfaces, but the project structure, naming conventions, magic bytes, capability flags, and one-project-per-format principle are identical.

Unix Compress (`.Z`) is a real, shipping format — 3 files, 35-line descriptor.

### Project

```
FileFormat.Compress/
  FileFormat.Compress.csproj
  CompressFormatDescriptor.cs     — the single descriptor: metadata + operations
  CompressConstants.cs            — magic bytes, flags
  CompressStream.cs               — LZW encode/decode stream wrapper
```

```xml
<!-- FileFormat.Compress.csproj — just references, everything else from Directory.Build.props -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\Compression.Core\Compression.Core.csproj" />
    <ProjectReference Include="..\Compression.Registry\Compression.Registry.csproj" />
  </ItemGroup>
</Project>
```

### Format Descriptor (35 lines — metadata + operations in one class)

```csharp
// CompressFormatDescriptor.cs — real production code from CompressionWorkbench
public sealed class CompressFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {

  // --- Metadata (queried by tools, viewers, detectors) ---
  public string Id => "Compress";
  public string DisplayName => "Unix Compress";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsOptimize;
  public string DefaultExtension => ".z";
  public IReadOnlyList<string> Extensions => [".z"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x1F, 0x9D], Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzw", "LZW", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "Unix compress, LZW adaptive dictionary";

  // --- Operations (the actual compress/decompress) ---
  public void Decompress(Stream input, Stream output) {
    using var ds = new CompressStream(input, CompressionStreamMode.Decompress, leaveOpen: true);
    ds.CopyTo(output);
  }
  public void Compress(Stream input, Stream output) {
    using var cs = new CompressStream(output, CompressionStreamMode.Compress, leaveOpen: true);
    input.CopyTo(cs);
  }
  public Stream? WrapDecompress(Stream input) =>
    new CompressStream(input, CompressionStreamMode.Decompress, leaveOpen: true);
  public Stream? WrapCompress(Stream output) =>
    new CompressStream(output, CompressionStreamMode.Compress, leaveOpen: true);
}
```

### Key Differences from Image Domain

The compression domain uses **instance-based descriptors** instead of static abstract interfaces — the descriptor is a concrete `sealed class` that the source generator discovers via `IFormatDescriptor` and registers with `new CompressFormatDescriptor()`. The tradeoffs:

| Aspect | Image pattern (PNGCrushCS) | Compression pattern (CompressionWorkbench) |
|--------|---------------------------|-------------------------------------------|
| Descriptor type | `readonly record struct` (value type) | `sealed class` (reference type) |
| Interface dispatch | Static abstract — zero-cost, compile-time | Instance virtual — one allocation per format at startup |
| IR type | `RawImage` (pixels) | `Stream` (bytes) |
| Registration | Generic typed calls: `_RegisterReader<QoiFile>(...)` | Constructor calls: `new CompressFormatDescriptor()` |
| Header parsing | `[GenerateSerializer]` source generator | `[GenerateSerializer]` source generator (same) |
| Detection | Tri-state (`true`/`false`/`null`) | Confidence `double` (0.0–1.0) + optional `Mask` |
| Validation | Reader throws on bad data | Optional `IFormatValidator` (3-tier: Header/Structure/Integrity) |

Both patterns are valid. The image pattern optimizes for zero-cost abstraction (hundreds of formats loaded at once, no per-format allocation). The compression pattern optimizes for simplicity (one class per format, no separate reader/writer/header files needed for simple formats).

### What's Shared Across Both Domains

Despite the interface style difference, these elements are identical:

1. **One project per format** — `FileFormat.Compress/`, `FileFormat.Qoi/`
2. **Minimal .csproj** — references core + registry/generators only, everything else from `Directory.Build.props`
3. **Magic byte detection** — `[FormatMagicBytes]` attribute or `MagicSignatures` property
4. **Capability flags** — `FormatCapability` / `FormatCapabilities` enum
5. **Source-generated registration** — zero runtime reflection in both repos
6. **Auto-generated format enum** — `ImageFormat.g.cs` / `Format.g.cs`
7. **Header serializer generator** — `[GenerateSerializer]` + `[Field]` attributes (shared generator)
8. **Constants class** — format-specific magic bytes, flags, sizes
9. **LGPL-3.0 license, NUnit 4 tests, same naming conventions**

A future metadata viewer could load assemblies from both repos and enumerate formats uniformly — the metadata surface (extensions, magic bytes, capabilities, description, header field map) is equivalent.

## Cross-Domain Applicability

The pattern is domain-agnostic. The interfaces and IR type change; the structure stays the same.

| Domain          | IR Type          | Reader Interface      | Writer Interface    | Metadata                           |
| --------------- | ---------------- | --------------------- | ------------------- | ---------------------------------- |
| **Image**       | `RawImage`       | `FromSpan → TSelf`    | `ToBytes(TSelf)`    | Width, Height, BitsPerPixel        |
| **Audio**       | `RawAudio`       | `FromSpan → TSelf`    | `ToBytes(TSelf)`    | SampleRate, Channels, Duration     |
| **Compression** | `Stream`         | `Decompress(in, out)` | `Compress(in, out)` | Method, OriginalSize               |
| **Archive**     | `ArchiveEntry[]` | `List/Extract`        | `Create`            | EntryCount, TotalSize              |
| **Video**       | `RawFrame[]`     | `FromSpan → TSelf`    | `ToBytes(TSelf)`    | Width, Height, FrameRate, Duration |
| **Crypto**      | `byte[]`         | `Decrypt(in, key)`    | `Encrypt(in, key)`  | Algorithm, KeySize, BlockSize      |

What stays constant across all domains:
- `readonly record struct` data model
- `[GenerateSerializer]` + `[Field]` / `[SeqField]` for binary headers
- `[FormatMagicBytes]` for detection
- `FormatCapability` flags for generic tooling
- `FieldDescriptor[]` for metadata viewers
- Source-generated registration (zero reflection)
- One project per format, same file naming convention

## What the Framework Owns vs. What You Own

| Framework (generated/provided)                          | You (hand-written)                                 |
| ------------------------------------------------------- | -------------------------------------------------- |
| Binary header ReadFrom/WriteTo                          | Codec logic (RLE, LZ, Huffman, etc.)               |
| Validation (magic, range, checksum)                     | Complex validation beyond declarative scope        |
| Computed fields and bit extraction                      | Business logic for derived values                  |
| Format enum generation                                  | Payload extraction and assembly                    |
| Registration calls with magic bytes                     | IR conversion semantics (ToRawImage / Decompress)  |
| Detection priority ordering                             | `MatchesSignature` for ambiguous formats           |
| Field map for hex editors                               | Format-specific enums and constants                |
| Convenience overloads (File, Stream, byte[])            | Sequential tail parsing beyond fixed header        |

The generator handles the mechanical, error-prone parts. You handle the creative parts — the actual format logic that requires understanding the spec.

## Design Rationale: C# Attributes vs. External DSL

A Kaitai Struct-style `.ksy` YAML file could also work via `AdditionalFiles` in the source generator. The C# attribute approach was chosen because:

1. **Single source of truth** — the struct definition IS the schema. No `.ksy` ↔ `.cs` synchronization.
2. **IDE support** — IntelliSense, refactoring, go-to-definition work on attribute parameters. No YAML editor plugin needed.
3. **Type safety** — `typeof(T)` in `[Case]` is checked at compile time. YAML type references are strings.
4. **Expression language** — `[Computed]` uses C# expressions directly. No separate expression evaluator.
5. **Incremental adoption** — convert one header at a time. Hand-written parsing and generated parsing coexist in the same project.
6. **Debugging** — generated `.g.cs` files are readable C#, steppable in the debugger.

The tradeoff: Kaitai's YAML is more concise for simple cases and cross-language. The attribute approach is more verbose but integrates seamlessly with the C# toolchain.

## Proven in Production

This architecture is not theoretical. It runs in two production repos:

- **PNGCrushCS** — 542 image format libraries (PNG, JPEG, WebP, QOI, TIFF, hundreds of retro/scientific/professional formats), 11 format-specific optimizers, a universal CLI, and a 500+ format viewer
- **CompressionWorkbench** — ~180 archive/compression format libraries (ZIP, RAR, 7z, Gzip, Brotli, LZMA, filesystem images, game archives), 38 building blocks, a universal CLI, a WPF archive browser, a binary analysis engine with auto-extraction, and 3500+ tests
