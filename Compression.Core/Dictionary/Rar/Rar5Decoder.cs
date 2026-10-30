namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// In-memory front end of <see cref="Rar5Unpacker"/>: decodes RAR 5 packed data held in a span.
/// Successive calls continue one solid stream.
/// </summary>
public sealed class Rar5Decoder {
  private readonly Rar5Unpacker _unpacker;
  private bool _started;

  /// <summary>Creates a decoder for a stream declaring <paramref name="dictionarySize"/>.</summary>
  /// <param name="dictionarySize">Dictionary size in bytes, at least 128 KB.</param>
  public Rar5Decoder(int dictionarySize) {
    ArgumentOutOfRangeException.ThrowIfLessThan(dictionarySize, Rar5Constants.MinDictionarySize);
    this._unpacker = new Rar5Unpacker(dictionarySize);
  }

  /// <summary>Decodes one file of <paramref name="uncompressedSize"/> bytes.</summary>
  /// <exception cref="InvalidDataException">The packed data is malformed.</exception>
  public byte[] Decompress(ReadOnlySpan<byte> compressed, int uncompressedSize) {
    ArgumentOutOfRangeException.ThrowIfNegative(uncompressedSize);
    if (uncompressedSize == 0)
      return [];
    using var input = new MemoryStream(compressed.ToArray(), writable: false);
    using var output = new MemoryStream(uncompressedSize);
    this._unpacker.Unpack(input, input.Length, uncompressedSize, output, this._started);
    this._started = true;
    return output.ToArray();
  }
}
