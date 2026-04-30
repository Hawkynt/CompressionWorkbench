#pragma warning disable CS1591
namespace FileFormat.Avro;

public static class AvroConstants {

  /// <summary>Avro OCF magic bytes: "Obj" followed by version byte 0x01.</summary>
  public static readonly byte[] Magic = [0x4F, 0x62, 0x6A, 0x01];

  /// <summary>Length of the OCF magic header in bytes.</summary>
  public const int MagicLength = 4;

  /// <summary>Length of the sync marker that delimits each block.</summary>
  public const int SyncMarkerLength = 16;

  /// <summary>Avro meta-map key for the JSON schema (UTF-8 bytes).</summary>
  public const string MetaKeySchema = "avro.schema";

  /// <summary>Avro meta-map key for the codec name.</summary>
  public const string MetaKeyCodec = "avro.codec";

  /// <summary>Default codec when the meta map omits <c>avro.codec</c>.</summary>
  public const string DefaultCodec = "null";
}
