#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.AlZip;

/// <summary>
/// ALZip (ESTsoft), the Korean archiver: stored, deflate and ALZ-bzip2 members, ZipCrypto encryption.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/alkegi/unalz</c> — unalz (MIT), its CC0 test archives, and the reader the layout follows</description></item>
///   <item><description><c>https://github.com/MacPaw/XADMaster</c> — XADALZipParser (LGPL-2.1)</description></item>
/// </list>
/// </summary>
public sealed class AlZipFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new AlZipReader(archive, leaveOpen: true);
    foreach (var e in r.Entries)
      if (e.CompressedSize > 0)
        yield return new DefragBlockInfo(e.DataOffset, e.CompressedSize, DefragBlockKind.Used, FileName: e.FileName);
  }

  public string Id => "AlZip";
  public string DisplayName => "ALZip";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: create writes stored/deflate (optionally encrypted) archives unar and unalz extract; edits
  // use the verified extract -> edit -> re-create rebuild (default IArchiveModifiable).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories | FormatCapabilities.SupportsPassword;
  public string DefaultExtension => ".alz";
  public IReadOnlyList<string> Extensions => [".alz"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("ALZ\x01"u8.ToArray(), Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate"), new("store", "Store")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Korean ALZip archive (stored, deflate and ALZ-bzip2 members; ZipCrypto)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new AlZipReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.IsDirectory ? "dir" : e.MethodName, e.IsDirectory, e.IsEncrypted, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new AlZipReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      if (e.IsDirectory) {
        Directory.CreateDirectory(Path.Combine(outputDir, e.FileName));
        continue;
      }
      WriteFile(outputDir, e.FileName, r.Extract(e, password));
    }
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;
    using var r = new AlZipReader(archive, leaveOpen: true);
    var e = r.Entries.FirstOrDefault(x => !x.IsDirectory && string.Equals(x.FileName, entryName, StringComparison.OrdinalIgnoreCase))
      ?? throw new FileNotFoundException($"No entry '{entryName}' in the ALZip archive.");
    return r.Extract(e, password);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new AlZipWriter(output, leaveOpen: true, password: options.Password, store: options.MethodName == "store");
    foreach (var input in inputs) {
      if (string.IsNullOrEmpty(input.ArchiveName)) continue;
      var modified = input.Metadata?.LastWriteTimeUtc?.LocalDateTime
                     ?? (input.InMemoryContent == null && File.Exists(input.FullPath) ? File.GetLastWriteTime(input.FullPath) : null);
      if (input.IsDirectory) w.AddDirectory(input.ArchiveName, modified);
      else w.AddFile(input.ArchiveName, input.ReadContent(), modified);
    }
  }
}
