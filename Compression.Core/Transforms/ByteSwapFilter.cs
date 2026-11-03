using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Compression.Registry;

namespace Compression.Core.Transforms;

/// <summary>
/// 7-Zip's SWAP2 / SWAP4 filters (method ids <c>02 03 02</c> and <c>02 03 04</c>):
/// reverse the byte order of every complete 2- or 4-byte group, leaving a trailing
/// partial group as it is. Each filter is its own inverse.
/// </summary>
/// <remarks>
/// Behaviour taken from 7-Zip's documented method list (Methods.txt) and confirmed
/// against <c>7z</c> archives made with <c>-mf=SWAP2</c> / <c>-mf=SWAP4</c>.
/// </remarks>
public static class ByteSwapFilter {

  /// <summary>Swaps the bytes of each complete 16-bit group.</summary>
  public static byte[] Swap2(ReadOnlySpan<byte> data) {
    var result = data.ToArray();
    var whole = result.Length & ~1;
    var words = MemoryMarshal.Cast<byte, ushort>(result.AsSpan(0, whole));
    BinaryPrimitives.ReverseEndianness(words, words);
    return result;
  }

  /// <summary>Reverses the bytes of each complete 32-bit group.</summary>
  public static byte[] Swap4(ReadOnlySpan<byte> data) {
    var result = data.ToArray();
    var whole = result.Length & ~3;
    var words = MemoryMarshal.Cast<byte, uint>(result.AsSpan(0, whole));
    BinaryPrimitives.ReverseEndianness(words, words);
    return result;
  }
}

/// <summary>Exposes 7-Zip's SWAP2 filter as a building block.</summary>
public sealed class Swap2BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Swap2";
  /// <inheritdoc/>
  public string DisplayName => "Swap2";
  /// <inheritdoc/>
  public string Description => "7-Zip SWAP2 filter: byte-swaps every 16-bit group (big/little-endian sample data)";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;
  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => ByteSwapFilter.Swap2(data);
  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => ByteSwapFilter.Swap2(data);
}

/// <summary>Exposes 7-Zip's SWAP4 filter as a building block.</summary>
public sealed class Swap4BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Swap4";
  /// <inheritdoc/>
  public string DisplayName => "Swap4";
  /// <inheritdoc/>
  public string Description => "7-Zip SWAP4 filter: byte-reverses every 32-bit group (big/little-endian sample data)";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;
  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => ByteSwapFilter.Swap4(data);
  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => ByteSwapFilter.Swap4(data);
}
