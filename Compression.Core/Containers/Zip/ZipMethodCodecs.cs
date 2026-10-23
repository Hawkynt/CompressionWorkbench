using System.Collections.Concurrent;

namespace FileFormat.Zip;

/// <summary>
/// Decoders for ZIP compression methods whose codec lives outside this assembly (WinZip's
/// media methods: 97 WavPack). Assemblies that implement such a codec register it here,
/// and <see cref="ZipReader"/> consults this table for any method it does not decode itself.
/// </summary>
public static class ZipMethodCodecs {

  /// <summary>Decodes an entry's compressed bytes; the second argument is the declared uncompressed size.</summary>
  public delegate byte[] Decoder(byte[] compressed, long uncompressedSize);

  private static readonly ConcurrentDictionary<ushort, Decoder> _decoders = new();

  /// <summary>Registers (or replaces) the decoder for <paramref name="method"/>.</summary>
  public static void Register(ushort method, Decoder decoder) {
    ArgumentNullException.ThrowIfNull(decoder);
    _decoders[method] = decoder;
  }

  /// <summary>Looks up the decoder for <paramref name="method"/>.</summary>
  public static bool TryGetDecoder(ushort method, out Decoder decoder) => _decoders.TryGetValue(method, out decoder!);
}
