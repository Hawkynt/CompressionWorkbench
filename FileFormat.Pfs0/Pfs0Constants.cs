namespace FileFormat.Pfs0;

internal static class Pfs0Constants {
  internal const string MagicPfs0String = "PFS0";
  internal const string MagicHfs0String = "HFS0";

  /// <summary>16-byte file header: magic(4) + numFiles(4) + stringTableSize(4) + reserved(4).</summary>
  internal const int HeaderSize = 16;

  /// <summary>24-byte file entry: dataOffset(8) + dataSize(8) + nameOffset(4) + reserved(4).</summary>
  internal const int EntrySize = 24;
}
