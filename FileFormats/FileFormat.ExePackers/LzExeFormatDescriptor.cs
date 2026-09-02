#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for LZEXE-packed DOS executables. Fabrice
/// Bellard's LZEXE (1989) was one of the first widely-distributed DOS exe
/// compressors; the unpacker stub embeds an <c>"LZ91"</c> or <c>"LZ09"</c>
/// signature near the start of the code section that uniquely identifies it.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://en.wikipedia.org/wiki/LZEXE</c> — format and tool history</description></item>
///   <item><description>UNLZEXE (Mitugu Kurizono) source — the de-facto documentation of the LZ91/LZ09 stub layout</description></item>
/// </list>
/// </summary>
public sealed class LzExeFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "LzExe";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "LZEXE (DOS exe)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".exe";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description =>
    "LZEXE (Fabrice Bellard, 1989) DOS exe compressor — surfaces MZ header, " +
    "the LZEXE stub, and the compressed payload. Decompression delegated to " +
    "`unlzexe` or `unp`.";

  /// <summary>LZEXE 0.91 signature.</summary>
  private static ReadOnlySpan<byte> Sig91 => "LZ91"u8;
  /// <summary>LZEXE 0.90 signature (rarer).</summary>
  private static ReadOnlySpan<byte> Sig09 => "LZ09"u8;

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
        "stored", false, false, null))
      .ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static List<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var bytes = ms.ToArray();

    if (!PackerScanner.IsMzExecutable(bytes))
      throw new InvalidDataException("LZEXE: not a DOS MZ executable.");

    var version = "";
    var idx = PackerScanner.IndexOfBounded(bytes, Sig91, 0x400);
    if (idx >= 0) version = "0.91";
    else {
      idx = PackerScanner.IndexOfBounded(bytes, Sig09, 0x400);
      if (idx >= 0) version = "0.90";
    }
    if (idx < 0) throw new InvalidDataException("LZEXE: LZ91/LZ09 signature not found in first 1 KB.");

    return [
      ("metadata.ini", BuildMetadata(idx, version)),
      ("mz_header.bin", bytes.AsSpan(0, Math.Min(0x40, bytes.Length)).ToArray()),
      ("packed_payload.bin", bytes),
    ];
  }

  private static byte[] BuildMetadata(int signatureOffset, string version) {
    var sb = new StringBuilder();
    sb.AppendLine("[lzexe]");
    sb.Append(CultureInfo.InvariantCulture, $"version = {version}\n");
    sb.Append(CultureInfo.InvariantCulture, $"signature_offset = 0x{signatureOffset:X4}\n");
    sb.Append("note = decompression delegated to `unlzexe` or `unp`\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
