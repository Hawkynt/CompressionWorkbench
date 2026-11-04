#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Qm;

/// <summary>
/// Qt compiled translation catalog (<c>.qm</c>), the file <c>QTranslator</c> loads.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://doc.qt.io/qt-6/linguist-ts-file-format.html</c> — the TS source format</description></item>
///   <item><description><c>docs/QM-ON-DISK.md</c> — the QM layout and the rules lrelease applies</description></item>
/// </list>
/// </summary>
/// <remarks>
/// The catalog is exposed as one entry, <c>translations.ts</c>: its messages decompiled into Qt Linguist's
/// TS XML (lconvert's view of a QM). Create takes that one TS file and compiles it as <c>lrelease</c> does
/// by default, byte for byte. A QM built with <c>lrelease -compress</c> may not store every source text;
/// such messages are marked in the TS output.
/// </remarks>
public sealed class QmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {

  /// <summary>The single entry a catalog lists as.</summary>
  public const string EntryName = "translations.ts";

  public string Id => "Qm";
  public string DisplayName => "Qt translation catalog (QM)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
  public string DefaultExtension => ".qm";
  public IReadOnlyList<string> Extensions => [".qm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new(QmCatalog.Magic.ToArray(), Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lrelease", "lrelease default")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Qt Linguist compiled translations. Lists as one translations.ts (the decompiled TS XML); create compiles one " +
    "TS file exactly as lrelease does by default.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var compressed = ReadAll(stream);
    var ts = Decompile(compressed);
    return [new ArchiveEntryInfo(0, EntryName, ts.Length, compressed.Length, "lrelease", false, false, null)];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (files != null && files.Length > 0 && !MatchesFilter(EntryName, files)) return;
    WriteFile(outputDir, EntryName, Decompile(ReadAll(stream)));
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = FilesOnly(inputs).ToList();
    if (files.Count != 1)
      throw new ArgumentException($"A QM catalog compiles exactly one TS file; {files.Count} were given.");
    output.Write(TsDocument.Parse(files[0].Data).Compile().Write());
  }

  private static byte[] Decompile(byte[] qm) => TsDocument.FromCatalog(QmCatalog.Read(qm)).ToXml();

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
