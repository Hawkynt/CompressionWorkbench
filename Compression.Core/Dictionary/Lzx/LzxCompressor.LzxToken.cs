namespace Compression.Core.Dictionary.Lzx;

public sealed partial class LzxCompressor {
  private readonly record struct LzxToken {
    public bool IsLiteral { get; private init; }
    public byte Value { get; private init; }
    public int Length { get; private init; }
    public int Offset { get; private init; }

    public static LzxToken CreateLiteral(byte value) => new() { IsLiteral = true, Value = value };

    public static LzxToken CreateMatch(int length, int offset) => new() { IsLiteral = false, Length = length, Offset = offset };
  }
}
