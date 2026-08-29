#pragma warning disable CS1591
namespace FileFormat.Rarc;

[Flags]
public enum RarcEntryAttributes : byte {
  None = 0x00,
  File = 0x01,
  Directory = 0x02,
  Compressed = 0x04,
  PreloadToMram = 0x10,
  PreloadToAram = 0x20,
  LoadFromDvd = 0x40,
  Yaz0Compressed = 0x80,
}

public sealed class RarcEntry {
  public required string Name { get; init; }
  public required bool IsDirectory { get; init; }
  public required ushort Id { get; init; }
  public required RarcEntryAttributes Attributes { get; init; }
  public required long Offset { get; init; }
  public required long Size { get; init; }
}
