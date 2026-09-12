namespace FileFormat.Sfar;

internal static class SfarConstants {
  /// <summary>"SFAR" little-endian magic — bytes 0x53 0x46 0x41 0x52 in file order.</summary>
  internal const uint Magic = 0x52414653u;

  /// <summary>32-byte fixed file header: magic + version + 5 offsets/counts + compression tag.</summary>
  internal const int HeaderSize = 32;

  /// <summary>Each entry on disk is 16 (MD5) + 4 (block index) + 5 (size) + 5 (offset) = 30 bytes.</summary>
  internal const int EntrySize = 30;

  /// <summary>Default block size used by Mass Effect 3 SFARs (64 KiB).</summary>
  internal const int DefaultMaxBlockSize = 0x10000;

  /// <summary>"lzx\0" tag — file uses per-block LZX compression.</summary>
  internal static readonly byte[] CompressionLzx = [0x6C, 0x7A, 0x78, 0x00];

  /// <summary>Four zero bytes — file is stored without compression.</summary>
  internal static readonly byte[] CompressionStored = [0x00, 0x00, 0x00, 0x00];
}
