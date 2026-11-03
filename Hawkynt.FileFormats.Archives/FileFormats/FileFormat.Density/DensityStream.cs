using System.Buffers.Binary;
using Compression.Core.Dictionary.Density;

namespace FileFormat.Density;

/// <summary>
/// density streams (Chameleon, Cheetah, Lion) in a small container of ours. density itself has no
/// file format: its streams carry neither the algorithm nor the length, so this container records
/// both. The payload is exactly what density-rs 0.16.6 writes; see <see cref="DensityCodec"/>.
/// </summary>
/// <remarks>
/// Layout: <c>DENS</c>, container version 2, algorithm (1 Chameleon, 2 Cheetah, 3 Lion), original
/// size (32-bit little-endian), then the density stream. Version 1 held private algorithms and is
/// refused.
/// </remarks>
public static class DensityStream {
  /// <summary>Compression algorithm.</summary>
  public enum Algorithm : byte {
    /// <summary>One 16-bit hash dictionary. Fastest, lowest ratio.</summary>
    Chameleon = 1,

    /// <summary>Two dictionary slots per hash plus one predicted successor. Balanced.</summary>
    Cheetah = 2,

    /// <summary>Two dictionary slots per hash plus five predicted successors. Best ratio.</summary>
    Lion = 3,
  }

  private static ReadOnlySpan<byte> Magic => "DENS"u8;
  private const byte Version = 2;
  private const int HeaderSize = 10;

  /// <summary>Compresses data with the specified algorithm.</summary>
  public static void Compress(Stream input, Stream output, Algorithm algorithm = Algorithm.Cheetah) {
    if (!Enum.IsDefined(algorithm))
      throw new ArgumentOutOfRangeException(nameof(algorithm));
    using var buffer = new MemoryStream();
    input.CopyTo(buffer);
    var data = buffer.ToArray();

    Span<byte> header = stackalloc byte[HeaderSize];
    Magic.CopyTo(header);
    header[4] = Version;
    header[5] = (byte)algorithm;
    BinaryPrimitives.WriteInt32LittleEndian(header[6..], data.Length);
    output.Write(header);
    output.Write(DensityCodec.Encode(data, (DensityAlgorithm)algorithm));
  }

  /// <summary>Decompresses a container written by <see cref="Compress"/>.</summary>
  /// <exception cref="InvalidDataException">The input is not a version-2 container or is corrupt.</exception>
  public static void Decompress(Stream input, Stream output) {
    Span<byte> header = stackalloc byte[HeaderSize];
    if (input.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) < HeaderSize || !header[..4].SequenceEqual(Magic))
      throw new InvalidDataException("Not a Density stream.");
    if (header[4] != Version)
      throw new InvalidDataException($"Unsupported Density container version: {header[4]}");
    var algorithm = (Algorithm)header[5];
    if (!Enum.IsDefined(algorithm))
      throw new InvalidDataException($"Unknown Density algorithm: {header[5]}");
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(header[6..]);
    if (originalSize < 0)
      throw new InvalidDataException("Density original size is negative.");

    using var buffer = new MemoryStream();
    input.CopyTo(buffer);
    var result = DensityCodec.Decode(buffer.ToArray(), (DensityAlgorithm)algorithm, originalSize);
    if (result.Length != originalSize)
      throw new InvalidDataException($"Density stream decoded to {result.Length} bytes, the header says {originalSize}.");
    output.Write(result);
  }
}
