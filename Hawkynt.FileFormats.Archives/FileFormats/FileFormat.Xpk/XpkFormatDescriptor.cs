using Compression.Core.Dictionary.Xpk;
using Compression.Registry;

namespace FileFormat.Xpk;

/// <summary>
/// Amiga XPK packed files (<c>XPKF</c>). Reads the NONE, RLEN, SQSH, NUKE and DUKE
/// sub-libraries; writes NONE and RLEN. Other sub-libraries are refused, never guessed.
/// </summary>
public sealed class XpkFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <inheritdoc />
  public string Id => "Xpk";
  /// <inheritdoc />
  public string DisplayName => "XPK (Amiga)";
  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Stream;
  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  /// <inheritdoc />
  public string DefaultExtension => ".xpk";
  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".xpk"];
  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("XPKF"u8.ToArray(), Confidence: 0.95)];
  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rlen", "RLEN"), new("none", "NONE")];
  /// <inheritdoc />
  public string? TarCompressionFormatId => null;
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  /// <inheritdoc />
  public string Description => "Amiga XPK library container: checksummed chunks packed by a named sub-library";

  /// <inheritdoc />
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new(
      Key: "Method",
      DisplayName: "Sub-library",
      Kind: FormatOptionKind.Enum,
      Default: "RLEN",
      AllowedValues: ["RLEN", "NONE"],
      Description: "XPK sub-library to pack with: RLEN run-length coding or NONE (stored chunks)."),
  ];

  /// <summary>Sub-libraries this descriptor decodes.</summary>
  public static IReadOnlySet<string> ReadableSubLibraries => XpkSubLibraries.Supported;

  /// <inheritdoc />
  public void Decompress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(XpkStream.Decode(ms.ToArray()));
  }

  /// <inheritdoc />
  public void Compress(Stream input, Stream output) => this.Compress(input, output, "RLEN");

  /// <inheritdoc />
  public void Compress(Stream input, Stream output, FormatCreateOptions options)
    => this.Compress(input, output, options.GetOption("Method", "RLEN").ToUpperInvariant());

  private void Compress(Stream input, Stream output, string type) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(XpkStream.Encode(ms.GetBuffer().AsSpan(0, (int)ms.Length), type));
  }
}
