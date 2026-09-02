namespace Compression.Core.Dictionary.Xpress;

/// <summary>
/// Represents a xpress huffman compressor.
/// </summary>
public sealed partial class XpressHuffmanCompressor {
  private readonly record struct HuffToken(int Symbol, int Distance, int Length);
}
