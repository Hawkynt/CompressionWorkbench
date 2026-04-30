#pragma warning disable CS1591
namespace FileFormat.Gar;

internal static class GarConstants {

  // Magic "GAR\x05" — Nintendo 3DS GAR v5 (Tomodachi Life / Animal Crossing: New Leaf era).
  internal static readonly byte[] MagicV5 = [0x47, 0x41, 0x52, 0x05];

  internal const int HeaderSize = 28;
  internal const int FileTypeEntrySize = 16;
  internal const int FileEntrySize = 16;

  // V5 always reports four "chunks": header + type table + entry table + string/data region.
  internal const uint DefaultChunkCount = 4;
}
