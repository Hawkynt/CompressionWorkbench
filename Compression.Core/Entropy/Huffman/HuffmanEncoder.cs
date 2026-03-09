using Compression.Core.BitIO;

namespace Compression.Core.Entropy.Huffman;

/// <summary>
/// Writes symbols to a stream using canonical Huffman coding.
/// Codes are written MSB-first (the standard for canonical Huffman).
/// Generic on <typeparamref name="TOrder"/> for zero-branch bit-order dispatch.
/// </summary>
public sealed class HuffmanEncoder<TOrder> where TOrder : struct, IBitOrder {
  private readonly CanonicalHuffman _table;
  private readonly BitWriter<TOrder> _bitWriter;

  /// <summary>
  /// Initializes a new <see cref="HuffmanEncoder{TOrder}"/>.
  /// </summary>
  /// <param name="table">The canonical Huffman code table to use.</param>
  /// <param name="bitWriter">The bit writer to output codes to.</param>
  public HuffmanEncoder(CanonicalHuffman table, BitWriter<TOrder> bitWriter) {
    ArgumentNullException.ThrowIfNull(table);
    ArgumentNullException.ThrowIfNull(bitWriter);
    this._table = table;
    this._bitWriter = bitWriter;
  }

  /// <summary>
  /// Encodes a single symbol.
  /// </summary>
  /// <param name="symbol">The symbol to encode.</param>
  public void EncodeSymbol(int symbol) {
    var (code, length) = this._table.GetCode(symbol);
    // Write MSB-first: the canonical code must be written from high bit to low bit
    for (int i = length - 1; i >= 0; --i)
      this._bitWriter.WriteBit((int)(code >> i) & 1);
  }

  /// <summary>
  /// Encodes a sequence of symbols.
  /// </summary>
  /// <param name="symbols">The symbols to encode.</param>
  public void EncodeSymbols(ReadOnlySpan<int> symbols) {
    for (int i = 0; i < symbols.Length; ++i)
      EncodeSymbol(symbols[i]);
  }

  /// <summary>
  /// Flushes any remaining bits in the writer.
  /// </summary>
  public void Flush() => this._bitWriter.FlushBits();
}
