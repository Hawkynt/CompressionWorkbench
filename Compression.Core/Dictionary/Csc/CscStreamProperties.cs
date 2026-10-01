using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Csc;

/// <summary>
/// The 10-byte property header that opens every libcsc stream: a big-endian 32-bit window size
/// followed by the 24-bit compressed block size and the 24-bit raw block size.
/// </summary>
/// <param name="DictionarySize">The LZ77 window in bytes (32 KiB to 1 GiB).</param>
/// <param name="CscBlockSize">The largest compressed block the stream contains.</param>
/// <param name="RawBlockSize">The most uncompressed bytes one coded block may produce.</param>
public readonly record struct CscStreamProperties(uint DictionarySize, uint CscBlockSize, uint RawBlockSize) {
  /// <summary>Size of the serialized header in bytes.</summary>
  public const int Size = CscConstants.PropertySize;

  /// <summary>Parses a property header.</summary>
  /// <param name="header">At least <see cref="Size"/> bytes.</param>
  /// <returns>The parsed properties; call <see cref="Validate"/> before trusting them.</returns>
  public static CscStreamProperties Read(ReadOnlySpan<byte> header) {
    ArgumentOutOfRangeException.ThrowIfLessThan(header.Length, Size, nameof(header));
    return new(
      BinaryPrimitives.ReadUInt32BigEndian(header),
      ReadUInt24BigEndian(header[4..]),
      ReadUInt24BigEndian(header[7..]));
  }

  /// <summary>Serializes the header.</summary>
  /// <param name="destination">At least <see cref="Size"/> bytes.</param>
  public void Write(Span<byte> destination) {
    ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, Size, nameof(destination));
    BinaryPrimitives.WriteUInt32BigEndian(destination, this.DictionarySize);
    WriteUInt24BigEndian(destination[4..], this.CscBlockSize);
    WriteUInt24BigEndian(destination[7..], this.RawBlockSize);
  }

  /// <summary>
  /// Rejects headers libcsc itself would refuse (window outside 32 KiB..1 GiB) and the empty block
  /// sizes it would then fail on, so a damaged stream fails up front instead of mid-decode.
  /// </summary>
  /// <exception cref="InvalidDataException">The header cannot describe a valid stream.</exception>
  public void Validate() {
    if (this.DictionarySize is < CscConstants.MinDictionarySize or > CscConstants.MaxDictionarySize)
      throw new InvalidDataException($"CSC window size {this.DictionarySize} is outside 32 KiB..1 GiB.");
    if (this.CscBlockSize == 0)
      throw new InvalidDataException("CSC compressed block size is zero.");
    if (this.RawBlockSize == 0)
      throw new InvalidDataException("CSC raw block size is zero.");
  }

  private static uint ReadUInt24BigEndian(ReadOnlySpan<byte> source) => ((uint)source[0] << 16) | ((uint)source[1] << 8) | source[2];

  private static void WriteUInt24BigEndian(Span<byte> destination, uint value) {
    destination[0] = (byte)(value >> 16);
    destination[1] = (byte)(value >> 8);
    destination[2] = (byte)value;
  }
}
