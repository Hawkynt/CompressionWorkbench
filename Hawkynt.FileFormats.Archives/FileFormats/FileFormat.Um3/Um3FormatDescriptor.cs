#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Um3;

/// <summary>
/// Bruns engine UM3 ("UltraMarine3") audio: an Ogg stream whose first 0x800 bytes are inverted
/// (XOR 0xFF), so the file starts B0 98 98 AC instead of <c>OggS</c>.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/morkt/GARbro</c> — GARbro (MIT), <c>ArcFormats/Bruns/AudioUM3.cs</c>, the oracle and the source of the layout</description></item>
/// </list>
/// </summary>
public sealed class Um3FormatDescriptor : IFormatDescriptor, IStreamFormatOperations {

  /// <summary>Bytes at the start of the file that are inverted.</summary>
  public const int ObfuscatedLength = 0x800;

  public string Id => "Um3";
  public string DisplayName => "Bruns UM3";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities => FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".um3";
  public IReadOnlyList<string> Extensions => [".um3"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0xB0, 0x98, 0x98, 0xAC], Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("xor", "Inverted head")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Bruns engine audio: an Ogg Vorbis stream with its first 2048 bytes inverted; decodes to the Ogg file.";

  public void Decompress(Stream input, Stream output) => Transform(input, output, "OggS"u8, decoding: true);

  /// <summary>Inverts an Ogg file's head; anything that is not an Ogg stream is refused.</summary>
  public void Compress(Stream input, Stream output) => Transform(input, output, "OggS"u8, decoding: false);

  private static void Transform(Stream input, Stream output, ReadOnlySpan<byte> plainMagic, bool decoding) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    var head = new byte[ObfuscatedLength];
    var read = input.ReadAtLeast(head, ObfuscatedLength, throwOnEndOfStream: false);
    var span = head.AsSpan(0, read);
    if (!decoding && !span.StartsWith(plainMagic))
      throw new InvalidDataException("UM3 holds an Ogg stream; the input is not one.");
    for (var i = 0; i < span.Length; ++i)
      span[i] ^= 0xFF;
    if (decoding && !span.StartsWith(plainMagic))
      throw new InvalidDataException("Not a UM3 file (the inverted head is not an Ogg page).");
    output.Write(span);
    input.CopyTo(output);
  }
}
