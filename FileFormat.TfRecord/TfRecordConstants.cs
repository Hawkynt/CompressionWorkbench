#pragma warning disable CS1591
namespace FileFormat.TfRecord;

internal static class TfRecordConstants {
  // The TFRecord-specific masking constant applied to both length-CRC and data-CRC.
  // Defined by the TensorFlow C++ implementation in tensorflow/core/lib/hash/crc32c.h.
  internal const uint CrcMaskDelta = 0xa282ead8u;

  // 8-byte little-endian UInt64 length prefix.
  internal const int LengthFieldSize = 8;

  // 4-byte little-endian masked CRC-32C of the length field.
  internal const int LengthCrcSize = 4;

  // 4-byte little-endian masked CRC-32C of the data payload.
  internal const int DataCrcSize = 4;

  // Total framing overhead per record (length + length-CRC + data-CRC).
  internal const int FramingOverhead = LengthFieldSize + LengthCrcSize + DataCrcSize;
}
