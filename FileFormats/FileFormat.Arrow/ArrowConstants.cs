#pragma warning disable CS1591
namespace FileFormat.Arrow;

public static class ArrowConstants {

  /// <summary>Arrow IPC File-format magic bytes: ASCII "ARROW1" followed by two zero padding bytes.</summary>
  public static readonly byte[] Magic = [0x41, 0x52, 0x52, 0x4F, 0x57, 0x31, 0x00, 0x00];

  /// <summary>Length of the Arrow IPC magic header/footer in bytes.</summary>
  public const int MagicLength = 8;

  /// <summary>Continuation marker prefixed to message length fields in Arrow IPC streams (0xFFFFFFFF).</summary>
  public const uint ContinuationMarker = 0xFFFFFFFFu;

  /// <summary>Arrow IPC messages are aligned to 8-byte boundaries between metadata and body.</summary>
  public const int Alignment = 8;

  /// <summary>Length of the trailing 4-byte UInt32 LE footer-length field that precedes the trailing magic in File format.</summary>
  public const int FooterLengthFieldLength = 4;

  /// <summary>FlatBuffers-encoded MessageHeader union tag for the Schema message.</summary>
  public const byte MessageHeaderSchema = 1;

  /// <summary>FlatBuffers-encoded MessageHeader union tag for the DictionaryBatch message.</summary>
  public const byte MessageHeaderDictionaryBatch = 2;

  /// <summary>FlatBuffers-encoded MessageHeader union tag for the RecordBatch message.</summary>
  public const byte MessageHeaderRecordBatch = 3;
}
