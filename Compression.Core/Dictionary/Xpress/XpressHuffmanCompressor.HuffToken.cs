namespace Compression.Core.Dictionary.Xpress;

public sealed partial class XpressHuffmanCompressor {
  private readonly record struct HuffToken(int Symbol, int Distance, int Length);
}
