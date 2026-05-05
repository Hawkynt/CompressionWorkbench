#pragma warning disable CS1591
namespace FileFormat.Tfc;

internal static class TfcConstants {
  /// <summary>Bundle chunk-header magic (little-endian on disk: C1 83 2A 9E).</summary>
  internal const uint Magic = 0x9E2A83C1u;

  /// <summary>Size in bytes of the per-bundle chunk header (magic + blockSize + compSize + uncompSize).</summary>
  internal const int HeaderSize = 16;

  /// <summary>Default nominal block size (128 KiB) used when callers don't override.</summary>
  internal const uint DefaultBlockSize = 0x00020000u;
}
