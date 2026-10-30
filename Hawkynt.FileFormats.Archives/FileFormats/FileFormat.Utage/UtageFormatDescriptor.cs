#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.Utage;

/// <summary>
/// UTAGE (Unity visual-novel engine) encrypted asset: the original file XORed with a repeating key,
/// except that a byte which is zero or equal to its key byte is left alone. That rule makes the
/// cipher its own inverse. The engine's default key is <c>InputOriginalKey</c>.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/Bioruebe/utagedec</c> — utagedec (BSD-3-Clause), the documented rule</description></item>
///   <item><description><c>https://github.com/morkt/GARbro</c> — GARbro (MIT), <c>ArcFormats/Unity/Utage/ImageUTAGE.cs</c>, the oracle</description></item>
/// </list>
/// </summary>
public sealed class UtageFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {

  /// <summary>The engine's default key.</summary>
  public static ReadOnlySpan<byte> DefaultKey => "InputOriginalKey"u8;

  public string Id => "Utage";
  public string DisplayName => "UTAGE encrypted asset";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities => FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".utage";
  public IReadOnlyList<string> Extensions => [".utage"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // A PNG under the default key: 89 50 4E 47 XOR "Inpu".
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0xC0, 0x3E, 0x3E, 0x32], Confidence: 0.6)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("xor", "UTAGE XOR")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "UTAGE engine asset obfuscation (zero- and key-preserving XOR). Decodes with the engine's default key; " +
    "create option key=<text> encodes with a game's own key.";

  public void Decompress(Stream input, Stream output) => Apply(input, output, DefaultKey);

  public void Compress(Stream input, Stream output) => Apply(input, output, DefaultKey);

  public void Compress(Stream input, Stream output, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var key = options.GetString("key");
    if (key is { Length: 0 })
      throw new ArgumentException("UTAGE key must not be empty.");
    Apply(input, output, key == null ? DefaultKey : Encoding.UTF8.GetBytes(key));
  }

  /// <summary>Applies the cipher (encryption and decryption are the same operation).</summary>
  public static void Apply(Stream input, Stream output, ReadOnlySpan<byte> key) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (key.IsEmpty)
      throw new ArgumentException("UTAGE key must not be empty.", nameof(key));
    var buffer = new byte[81920];
    var position = 0L;
    int read;
    while ((read = input.Read(buffer, 0, buffer.Length)) > 0) {
      for (var i = 0; i < read; ++i) {
        var k = key[(int)((position + i) % key.Length)];
        var b = buffer[i];
        if (b != 0 && b != k)
          buffer[i] = (byte)(b ^ k);
      }
      output.Write(buffer, 0, read);
      position += read;
    }
  }
}
