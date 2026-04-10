#pragma warning disable CS1591
namespace FileFormat.T64;

public sealed class T64Entry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory => false;
  public byte EntryType { get; init; } // 1=normal, 3=snapshot
  public ushort StartAddress { get; init; }
  public ushort EndAddress { get; init; }
  internal int DataOffset { get; init; }
}
