using Compression.Registry;

namespace FileFormat.ExePackers;

/// <summary>
/// DIET-compressed data files: what Teddy Matsumoto's DIET makes of a file that is not an
/// executable (it keeps the name and compresses in place). Read in all three layouts
/// (1.00, 1.02-1.20, 1.44-1.45f); written in the 1.44-1.45f layout, which DIET 1.45f
/// restores with <c>DIET -R</c>. See <see cref="DietDataCodec"/>.
/// </summary>
public sealed class DietDataFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  /// <summary>Gets the id.</summary>
  public string Id => "DietData";
  /// <summary>Gets the display name.</summary>
  public string DisplayName => "DIET data file";
  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Stream;
  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities => FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  /// <summary>Gets the default extension; DIET keeps the original name, so there is none of its own.</summary>
  public string DefaultExtension => ".diet";
  /// <summary>Gets the extensions.</summary>
  public IReadOnlyList<string> Extensions => [];
  /// <summary>Gets the compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>Gets the magic signatures of the 1.44-1.45f, 1.02-1.20 and 1.00 layouts.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xB4, 0x4C, 0xCD, 0x21, 0x9D, 0x89, (byte)'d', (byte)'l', (byte)'z'], 0, 0.95),
    new([0x9D, 0x89, (byte)'d', (byte)'l', (byte)'z'], 0, 0.90),
    new([0xB4, 0x4C, 0xCD, 0x21, 0x9D, 0x89], 0, 0.85),
  ];
  /// <summary>Gets the methods.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("diet", "DIET 1.45f")];
  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;
  /// <summary>Gets the family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  /// <summary>Gets the description.</summary>
  public string Description => "DIET (Teddy Matsumoto, 1991-1992) compressed data file: LZ77 over an 8 KiB window, CRC-16 checked";

  /// <summary>Restores the original file.</summary>
  public void Decompress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(DietDataCodec.Decode(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
  }

  /// <summary>Compresses into a DIET 1.44-1.45f data file.</summary>
  public void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(DietDataCodec.Encode(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
  }
}
