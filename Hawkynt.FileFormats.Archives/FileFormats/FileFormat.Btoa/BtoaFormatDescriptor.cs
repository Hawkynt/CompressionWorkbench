using Compression.Registry;

namespace FileFormat.Btoa;

/// <summary>
/// btoa 4.0 (Paul Rutter, Joe Orost): the base-85 text encoding that later became
/// Adobe's ASCII85, framed by <c>xbtoa Begin</c> and an <c>xbtoa End</c> trailer with the
/// byte count and three checksums. Decoding checks every checksum and refuses on a
/// mismatch; encoding writes byte-for-byte what btoa 4.0 writes. See <see cref="BtoaCodec"/>.
/// </summary>
public sealed class BtoaFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  /// <summary>Gets the id.</summary>
  public string Id => "Btoa";
  /// <summary>Gets the display name.</summary>
  public string DisplayName => "btoa (ASCII85)";
  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Wrapper;
  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  /// <summary>Gets the default extension.</summary>
  public string DefaultExtension => ".btoa";
  /// <summary>Gets the extensions.</summary>
  public IReadOnlyList<string> Extensions => [".btoa"];
  /// <summary>Gets the compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>Gets the magic signatures: the <c>xbtoa Begin</c> line at the start of the file.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("xbtoa Begin"u8.ToArray(), 0, 0.95)];
  /// <summary>Gets the methods.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("btoa", "btoa 4.0")];
  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;
  /// <summary>Gets the family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Encoding;
  /// <summary>Gets the description.</summary>
  public string Description => "Base-85 binary-to-text encoding with count and checksum trailer (Rutter's btoa 4.0)";

  /// <summary>Decodes btoa text, checking the trailer's count and checksums.</summary>
  public void Decompress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(BtoaCodec.Decode(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
  }

  /// <summary>Encodes the input as btoa 4.0 text.</summary>
  public void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    BtoaCodec.Encode(ms.GetBuffer().AsSpan(0, (int)ms.Length), output);
  }
}
