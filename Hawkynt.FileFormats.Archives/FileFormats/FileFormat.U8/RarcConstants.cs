#pragma warning disable CS1591
namespace FileFormat.Rarc;

internal static class RarcConstants {
  internal const int HeaderSize = 0x20;
  internal const int DataHeaderSize = 0x20;
  internal const int NodeSize = 0x10;
  internal const int FileEntrySize = 0x14;
  internal const int Alignment = 0x20;
  internal const ushort DirectoryFileId = 0xFFFF;
}
