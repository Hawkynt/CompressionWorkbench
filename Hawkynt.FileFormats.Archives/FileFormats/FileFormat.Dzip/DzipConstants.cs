#pragma warning disable CS1591
namespace FileFormat.Dzip;

internal static class DzipConstants {
  internal const string MagicString = "DZIP";
  internal static readonly byte[] MagicBytes = "DZIP"u8.ToArray();

  internal const uint SupportedVersion = 2;

  /// <summary>16-byte file header: magic(4) + version(4) + fileCount(4) + tocOffset(4).</summary>
  internal const int HeaderSize = 16;

  /// <summary>Maximum entry path length (UInt8 length prefix).</summary>
  internal const int MaxPathLength = 255;

  /// <summary>LZSS sliding window size in bytes.</summary>
  internal const int LzssWindowSize = 4096;

  /// <summary>LZSS minimum match length (length field stored as length - 3).</summary>
  internal const int LzssMinMatch = 3;
}
