using Compression.Core.BitIO;

namespace Compression.Core.Entropy.Huffman;

/// <summary>
/// Reads and decodes Huffman-coded symbols from a stream.
/// Generic on <typeparamref name="TOrder"/> for zero-branch bit-order dispatch.
/// </summary>
public sealed class HuffmanDecoder<TOrder> where TOrder : struct, IBitOrder {
  private readonly CanonicalHuffman _table;
  private readonly BitBuffer<TOrder> _bitBuffer;

  /// <summary>
  /// Initializes a new <see cref="HuffmanDecoder{TOrder}"/>.
  /// </summary>
  /// <param name="table">The canonical Huffman code table.</param>
  /// <param name="bitBuffer">The bit buffer to read from.</param>
  public HuffmanDecoder(CanonicalHuffman table, BitBuffer<TOrder> bitBuffer) {
    ArgumentNullException.ThrowIfNull(table);
    ArgumentNullException.ThrowIfNull(bitBuffer);
    this._table = table;
    this._bitBuffer = bitBuffer;
  }

  /// <summary>
  /// Decodes the next symbol from the bit stream.
  /// </summary>
  /// <returns>The decoded symbol.</returns>
  public int DecodeSymbol() => this._table.DecodeSymbol(this._bitBuffer);

  /// <summary>
  /// Decodes <paramref name="count"/> symbols from the bit stream.
  /// </summary>
  /// <param name="count">The number of symbols to decode.</param>
  /// <returns>An array of decoded symbols.</returns>
  public int[] DecodeSymbols(int count) {
    var result = new int[count];
    for (int i = 0; i < count; ++i)
      result[i] = DecodeSymbol();

    return result;
  }
}
