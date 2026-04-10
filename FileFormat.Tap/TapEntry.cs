#pragma warning disable CS1591
namespace FileFormat.Tap;

public sealed class TapEntry {
  public string Name { get; init; } = "";
  public int Size { get; init; }
  public long DataOffset { get; init; }
  /// <summary>0=Program, 1=NumArray, 2=CharArray, 3=Code</summary>
  public byte FileType { get; init; }
}
