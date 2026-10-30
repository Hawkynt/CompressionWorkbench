#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.SymbianSis;

/// <summary>
/// Symbian OS 9.x installation package (".sis" made by makesis, ".sisx" once signsis signed it):
/// the files of the package and of every package embedded in it, checked against the
/// controller's CRC-16s and SHA-1s. Creation writes an unsigned package the way makesis does.
///
/// References:
/// <list type="bullet">
///   <item><description>Symbian OS v9.X SIS File Format Specification, version 1.1 (Symbian, June 2006)</description></item>
/// </list>
/// </summary>
public sealed class SymbianSisFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "SymbianSis";
  public string DisplayName => "Symbian SIS package";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".sis";
  public IReadOnlyList<string> Extensions => [".sis", ".sisx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // UID 1 0x10201A7A.
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x7A, 0x1A, 0x20, 0x10], 0, 0.80)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate"), new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Symbian OS 9 installation package: files of the package and its embedded packages, SHA-1-checked";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    SymbianSisReader reader;
    try {
      reader = new SymbianSisReader(stream);
    } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException) {
      var note = Encoding.UTF8.GetBytes($"format=symbian-sis\nparse_status=partial\nerror={ex.Message.Replace('\n', ' ')}\n");
      return [new ArchiveEntryInfo(0, "metadata.ini", note.Length, note.Length, "stored", false, false, null, Kind: "metadata")];
    }
    return reader.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.FileName, e.Size, e.CompressedSize, e.CompressedSize == e.Size ? "stored" : "deflate", false, false, null)).ToList();
  }

  /// <summary>Package UID 3 for created packages: the first UID of the unprotected test range.</summary>
  public const uint CreatedPackageUid = 0xE0000000;

  /// <summary>
  /// Writes an unsigned package installing every input to "!:\&lt;name&gt;", named "package",
  /// version 1.0.0, stamped with the current time; <see cref="SymbianSisWriter"/> takes the rest.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = inputs.Where(i => !i.IsDirectory)
      .Select(i => new SymbianSisFile(@"!:\" + i.ArchiveName.Replace('/', '\\').TrimStart('\\'), i.ReadContent()))
      .ToList();
    output.Write(SymbianSisWriter.Write(new() { Name = "package", Uid = CreatedPackageUid, CreationTime = DateTime.UtcNow, Files = files }));
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var reader = new SymbianSisReader(stream);
    foreach (var e in reader.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files))
        continue;
      WriteFile(outputDir, e.FileName, reader.Extract(e));
    }
  }
}
