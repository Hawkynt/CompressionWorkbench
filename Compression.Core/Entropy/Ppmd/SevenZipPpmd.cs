using System.Buffers.Binary;

namespace Compression.Core.Entropy.Ppmd;

/// <summary>
/// The 7z PPMd coder (method id <c>03 04 01</c>): PPMd variant H as <see cref="Ppmd7"/> models it,
/// driven by 7-Zip's range coder.
/// </summary>
/// <remarks>
/// The coder properties are five bytes: the model order, then the model memory size as a
/// little-endian 32-bit value. The stream carries no length and, as 7-Zip writes it, no end
/// marker; the folder's unpack size says where it ends. Decoding also stops at an end marker
/// (escape past the order-0 context), which other writers may emit.
/// </remarks>
public static class SevenZipPpmd {
  /// <summary>The smallest model order 7-Zip accepts.</summary>
  public const int MinOrder = 2;
  /// <summary>The largest model order 7-Zip accepts.</summary>
  public const int MaxOrder = 64;
  /// <summary>The smallest model memory 7-Zip accepts (2 KiB).</summary>
  public const uint MinMemorySize = 1u << 11;
  /// <summary>The largest model memory 7-Zip accepts.</summary>
  public const uint MaxMemorySize = 0xFFFFFFFFu - 12 * 3;

  /// <summary>Builds the five coder-property bytes for <paramref name="order"/> and <paramref name="memorySize"/>.</summary>
  /// <exception cref="ArgumentOutOfRangeException">A value lies outside what 7-Zip accepts.</exception>
  public static byte[] Properties(int order, uint memorySize) {
    Validate(order, memorySize);
    var props = new byte[5];
    props[0] = (byte)order;
    BinaryPrimitives.WriteUInt32LittleEndian(props.AsSpan(1), memorySize);
    return props;
  }

  /// <summary>Reads order and memory size from the five coder-property bytes.</summary>
  /// <exception cref="InvalidDataException">The properties are not five bytes or hold values 7-Zip refuses.</exception>
  public static (int Order, uint MemorySize) ParseProperties(ReadOnlySpan<byte> props) {
    if (props.Length != 5)
      throw new InvalidDataException($"7z PPMd properties are 5 bytes, not {props.Length}.");
    var order = (int)props[0];
    var memorySize = BinaryPrimitives.ReadUInt32LittleEndian(props[1..]);
    if (order is < MinOrder or > MaxOrder || memorySize is < MinMemorySize or > MaxMemorySize)
      throw new InvalidDataException($"7z PPMd properties order {order}, memory {memorySize} are out of range.");
    return (order, memorySize);
  }

  /// <summary>Compresses <paramref name="data"/> the way 7-Zip does: no end marker, five flush bytes.</summary>
  /// <exception cref="ArgumentOutOfRangeException">A parameter lies outside what 7-Zip accepts.</exception>
  public static byte[] Encode(ReadOnlySpan<byte> data, int order, uint memorySize) => Encode(data, order, memorySize, endMarker: false);

  /// <summary>Compresses <paramref name="data"/>, optionally closing the stream with the end marker.</summary>
  /// <exception cref="ArgumentOutOfRangeException">A parameter lies outside what 7-Zip accepts.</exception>
  public static byte[] Encode(ReadOnlySpan<byte> data, int order, uint memorySize, bool endMarker) {
    Validate(order, memorySize);
    var model = new Ppmd7();
    model.Allocate(memorySize);
    model.Initialize(order);
    using var output = new MemoryStream(data.Length / 2 + 16);
    var rc = new SevenZipPpmdRangeEncoder(output);
    foreach (var symbol in data)
      model.EncodeSymbol(rc, symbol);
    if (endMarker)
      model.EncodeSymbol(rc, -1);
    rc.Flush();
    return output.ToArray();
  }

  /// <summary>Decompresses a 7z PPMd stream to exactly <paramref name="unpackSize"/> bytes.</summary>
  /// <exception cref="InvalidDataException">
  /// The properties are invalid, the stream does not start as 7-Zip's encoder starts it, a symbol
  /// cannot be decoded, or the stream ends (by end marker or by running out) before
  /// <paramref name="unpackSize"/> bytes.
  /// </exception>
  public static byte[] Decode(ReadOnlySpan<byte> props, byte[] packed, int unpackSize) {
    ArgumentNullException.ThrowIfNull(packed);
    ArgumentOutOfRangeException.ThrowIfNegative(unpackSize);
    var (order, memorySize) = ParseProperties(props);
    var rc = new SevenZipPpmdRangeDecoder(packed);
    if (!rc.IsValidStart)
      throw new InvalidDataException("7z PPMd stream does not start with the range coder's zero byte.");

    var model = new Ppmd7();
    model.Allocate(memorySize);
    model.Initialize(order);
    var output = new byte[unpackSize];
    for (var i = 0; i < output.Length; ++i) {
      var symbol = model.DecodeSymbol(rc);
      if (symbol < 0)
        throw new InvalidDataException(symbol == -1
          ? $"7z PPMd stream ends with its end marker after {i} of {unpackSize} bytes."
          : $"7z PPMd stream is corrupt at byte {i}.");
      // 7-Zip's flush leaves the decoder all the bytes it reads; one read past them means truncation.
      if (rc.Overrun > 0)
        throw new InvalidDataException("7z PPMd stream is truncated.");
      output[i] = (byte)symbol;
    }
    return output;
  }

  private static void Validate(int order, uint memorySize) {
    ArgumentOutOfRangeException.ThrowIfLessThan(order, MinOrder);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(order, MaxOrder);
    ArgumentOutOfRangeException.ThrowIfLessThan(memorySize, MinMemorySize);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(memorySize, MaxMemorySize);
  }
}
