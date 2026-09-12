#pragma warning disable CS1591
namespace FileFormat.Ghost;

/// <summary>
/// On-disk constants for the Ghost 11.x / 12.x record container.
/// Reverse-engineered from Norton Ghost 11.5.1 (see <see cref="GhostReader"/>
/// header comment for source attribution + scope notes).
/// </summary>
public static class GhostConstants {

  /// <summary>FE EF magic at offset 0 of both the file header and the FEEF partition header.</summary>
  public const ushort FileMagic = 0xEFFE;

  /// <summary>Magic at offset 4 of every record header (LE uint32 = 0x012F18D8).</summary>
  public const uint RecordMagic = 0x012F18D8;

  /// <summary>Both the file header and the FEEF partition header are exactly 512 bytes.</summary>
  public const int HeaderSize = 512;

  /// <summary>Record header layout: [4B type][4B magic][2B body_len].</summary>
  public const int RecordHeaderSize = 10;

  /// <summary>Compressed data is chopped into 32 KB decompressed blocks.</summary>
  public const int BlockSize = 32768;

  /// <summary>
  /// Each block is prefixed with a 16-bit LE <c>stored_len</c> that includes
  /// the 2-byte length field itself. The largest representable stored_len
  /// for a 32 KB raw block plus the 4-byte block header + worst-case
  /// expansion overhead is bounded by <c>0xFFFF</c>; we cap at this.
  /// </summary>
  public const int MaxStoredLen = 33002;

  /// <summary>Fast LZ hash table is 4096 entries (12-bit index).</summary>
  public const int FastLzHashSize = 4096;

  // Record type codes (low 16 bits of the 32-bit type field).
  /// <summary>
  /// Defines the record type track 0 constant value.
  /// </summary>
  public const ushort RecordTypeTrack0 = 0x0006;
  /// <summary>
  /// Defines the record type partition constant value.
  /// </summary>
  public const ushort RecordTypePartition = 0x0603;
  /// <summary>
  /// Defines the record type continuation constant value.
  /// </summary>
  public const ushort RecordTypeContinuation = 0x0703;
  /// <summary>
  /// Defines the record type end constant value.
  /// </summary>
  public const ushort RecordTypeEnd = 0x0023;

  /// <summary>
  /// CompressionWorkbench annotation record (NOT a stock Ghost record type
  /// — chosen from the unused low-16 space). Carried inside a normal
  /// 0x012F18D8-magic record so the framing scanner skips it cleanly when
  /// the reader does not implement annotation handling. Annotation bodies
  /// start with <see cref="AnnotationMagic"/> so accidental data bytes that
  /// happen to use this type code are not mis-parsed as annotations.
  /// </summary>
  public const ushort RecordTypeAnnotation = 0x00FE;

  /// <summary>
  /// Sentinel at the head of every <see cref="RecordTypeAnnotation"/> body —
  /// ASCII "GHO1" little-endian. Lets the reader skip third-party / Ghost-
  /// proper records that happen to land on the same type code.
  /// </summary>
  public const uint AnnotationMagic = 0x31_4F_48_47u; // "GHO1" LE

  /// <summary>Annotation op: remove the entry named in the annotation body.</summary>
  public const byte AnnotationOpRemove = 0x01;

  /// <summary>
  /// Annotation op: replace the entry named in the annotation body with the
  /// payload bytes that follow. Payload is stored uncompressed inside the
  /// annotation record itself (so a Replace tombstone is self-contained).
  /// </summary>
  public const byte AnnotationOpReplace = 0x02;

  // Compression byte (offset 3 of the file/partition header).
  /// <summary>
  /// Defines the compression none constant value.
  /// </summary>
  public const byte CompressionNone = 0;
  /// <summary>
  /// Defines the compression old constant value.
  /// </summary>
  public const byte CompressionOld = 1;
  /// <summary>
  /// Defines the compression fast constant value.
  /// </summary>
  public const byte CompressionFast = 2;
  /// <summary>
  /// Defines the compression high 3 constant value.
  /// </summary>
  public const byte CompressionHigh3 = 3;
  /// <summary>
  /// Defines the compression high 4 constant value.
  /// </summary>
  public const byte CompressionHigh4 = 4;
  /// <summary>
  /// Defines the compression high 5 constant value.
  /// </summary>
  public const byte CompressionHigh5 = 5;
  /// <summary>
  /// Defines the compression high 6 constant value.
  /// </summary>
  public const byte CompressionHigh6 = 6;
  /// <summary>
  /// Defines the compression high 7 constant value.
  /// </summary>
  public const byte CompressionHigh7 = 7;
  /// <summary>
  /// Defines the compression high 8 constant value.
  /// </summary>
  public const byte CompressionHigh8 = 8;
  /// <summary>
  /// Defines the compression high 9 constant value.
  /// </summary>
  public const byte CompressionHigh9 = 9;

  /// <summary>Single-file image (no <c>.ghs</c> spans).</summary>
  public const byte FileTypeSingle = 0x01;

  /// <summary>First file of a spanned image (subsequent parts use <c>.ghs</c>).</summary>
  public const byte FileTypeSpan = 0x09;

  /// <summary>Sub-type byte at offset 2 of the FEEF partition header.</summary>
  public const byte PartitionHeaderSubType = 0x02;
}

/// <summary>Parsed view of the 512-byte file header.</summary>
internal sealed class GhostFileHeader {
  public ushort Magic { get; init; }
  public byte FileType { get; init; }
  public byte Compression { get; init; }
  public uint Id { get; init; }
  public byte[] Raw { get; init; } = new byte[GhostConstants.HeaderSize];

  /// <summary>True when byte 12, bit 1 is set — the encryption indicator.</summary>
  public bool IsEncrypted => this.Raw.Length >= 14 && (this.Raw[12] & 0x02) != 0;
}

/// <summary>Parsed view of the 512-byte FEEF per-partition header.</summary>
internal sealed class GhostPartitionHeader {
  public ushort Magic { get; init; }
  public byte SubType { get; init; }
  public byte Compression { get; init; }
  public uint Id { get; init; }
  public byte[] Raw { get; init; } = new byte[GhostConstants.HeaderSize];
}

/// <summary>Parsed view of a 10-byte record header plus location.</summary>
internal sealed class GhostRecord {
  public uint Type { get; init; }
  public uint Magic { get; init; }
  public ushort BodyLen { get; init; }
  public long Offset { get; init; }
  public ushort TypeCode => (ushort)(this.Type & 0xFFFF);
}

/// <summary>One contiguous compressed-data span between record headers.</summary>
internal readonly record struct GhostSpan(long DataStart, long DataEnd);

/// <summary>
/// A CompressionWorkbench annotation parsed off the wire — the in-place
/// modifier appends one of these per Remove / Replace call so the existing
/// partition bytes can stay byte-identical at their original offsets.
/// </summary>
public sealed class GhostAnnotation {
  /// <summary>Operation code: <see cref="GhostConstants.AnnotationOpRemove"/> or <see cref="GhostConstants.AnnotationOpReplace"/>.</summary>
  public byte Op { get; init; }

  /// <summary>The entry name the annotation targets (e.g. <c>partition1.bin</c>).</summary>
  public string TargetName { get; init; } = "";

  /// <summary>Replacement bytes (empty for Remove ops).</summary>
  public byte[] Payload { get; init; } = [];
}

/// <summary>
/// Per-partition metadata + compressed-data spans (a partition's blocks
/// are split across one or more <see cref="GhostSpan"/>s, one per
/// continuation record).
/// </summary>
internal sealed class GhostPartitionInfo {
  public GhostRecord? Descriptor { get; init; }
  public GhostPartitionHeader? Header { get; init; }
  public List<GhostSpan> Spans { get; } = [];
  public byte[] DescBody { get; init; } = new byte[20];

  public long TotalCompressedSize {
    get {
      long total = 0;
      foreach (var s in this.Spans) total += s.DataEnd - s.DataStart;
      return total;
    }
  }
}
