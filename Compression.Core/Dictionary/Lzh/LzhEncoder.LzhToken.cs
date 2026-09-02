namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Encodes lzh data.
/// </summary>
public sealed partial class LzhEncoder {
  private readonly record struct LzhToken {
    public bool IsLiteral { get; private init; }
    public byte Value { get; private init; }
    public int Length { get; private init; }
    public int Distance { get; private init; }
    public static LzhToken CreateLiteral(byte value) => new() { IsLiteral = true, Value = value };
    public static LzhToken CreateMatch(int length, int distance) => new() { IsLiteral = false, Length = length, Distance = distance };
  }
}
