namespace FileFormat.InnoSetup;

internal static class InnoSetupConstants {
  internal const string SignaturePrefix = "Inno Setup Setup Data (";
  internal static readonly byte[] LegacyMagic = "rDlPtS"u8.ToArray();

  // Offset of the 4-byte PE header pointer inside the MZ stub
  internal const int PeHeaderPtrOffset = 0x3C;

  // Magic bytes that begin a PE optional header
  internal const ushort PeMagicPe32    = 0x010B;
  internal const ushort PeMagicPe32Plus = 0x020B;

  // LZMA sub-header: 5-byte properties only (no size field — size is unknown / end-marker)
  internal const int LzmaPropSize = 5;

  // Scan window: how far past the PE overlay start we search for the Inno signature
  internal const int ScanWindow = 512 * 1024; // 512 KB
}
