namespace FileFormat.Psarc;

internal static class PsarcConstants {
  internal const string MagicString = "PSAR";
  internal const string CompressionZlib = "zlib";
  internal const string CompressionLzma = "lzma";

  /// <summary>32-byte fixed header preceding the TOC.</summary>
  internal const int HeaderSize = 32;

  /// <summary>Each TOC entry is exactly 30 bytes: md5(16) + startBlockIndex(4) + originalSize(5) + startOffset(5).</summary>
  internal const int TocEntrySize = 30;

  /// <summary>Default block size used by Sony's tools and assumed by readers when authoring.</summary>
  internal const int DefaultBlockSize = 0x10000;

  /// <summary>Bit 2 of the archive flags is treated as the encrypted-TOC indicator across community PSARC implementations.</summary>
  internal const uint FlagEncryptedToc = 1u << 2;

  /// <summary>Bit 0 indicates relative paths; we always set it on write.</summary>
  internal const uint FlagRelativePaths = 1u << 0;

  internal const ushort SupportedMajor = 1;
  internal const ushort SupportedMinorMin = 3;
  internal const ushort SupportedMinorMax = 4;
}
