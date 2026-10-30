#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.B1;

/// <summary>
/// B1 archive (b1.org; the B1 Free Archiver's format): a record stream of catalog and complete
/// records in plain or LZMA blocks, optionally AES-256-GCM encrypted. Single-volume archives only.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/b1-pack/b1-pack</c> — b1-pack (Apache-2.0), converted for the reader and writer, and the oracle</description></item>
///   <item><description><c>http://dev.b1.org/standard/archive-format.html</c> — B1 Pack Format, schema 0.4 (archived)</description></item>
/// </list>
/// </summary>
public sealed class B1FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "B1";
  public string DisplayName => "B1";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: b1-pack lists and extracts what we write (LZMA, plain, AES); edits rebuild an unencrypted
  // archive keeping names, folders, times and its compression.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories | FormatCapabilities.SupportsPassword;
  public string DefaultExtension => ".b1";
  public IReadOnlyList<string> Extensions => [".b1"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("b1:as "u8.ToArray(), Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzma", "LZMA"), new("store", "Store")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "B1 archive (b1-pack): plain or LZMA blocks, AES-256-GCM encryption; single volume";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new B1Reader(stream, password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.Size, e.Size,
      e.IsDirectory ? "dir" : r.IsCompressed ? "lzma" : "store", e.IsDirectory, r.IsEncrypted, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new B1Reader(stream, password);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      if (e.IsDirectory) Directory.CreateDirectory(Path.Combine(outputDir, e.FileName));
      else WriteFile(outputDir, e.FileName, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new B1Writer();
    foreach (var input in inputs) {
      if (string.IsNullOrEmpty(input.ArchiveName)) continue;
      var modified = input.Metadata?.LastWriteTimeUtc?.UtcDateTime
                     ?? (input.InMemoryContent == null && File.Exists(input.FullPath) ? File.GetLastWriteTimeUtc(input.FullPath) : null);
      if (input.IsDirectory) w.AddFolder(input.ArchiveName, modified);
      else w.AddFile(input.ArchiveName, input.ReadContent(), modified);
    }

    output.Write(w.ToArray(options.MethodName != "store", options.Password));
  }

  /// <summary>Adds (or replaces by path) files in an unencrypted archive; other members keep names, folders and times.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var added = FilesOnly(inputs).ToList();
    var names = added.Select(f => f.Name.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
    Rewrite(archive, e => !names.Contains(e.FileName), w => {
      foreach (var (name, data) in added)
        w.AddFile(name, data, DateTime.UtcNow);
    });
  }

  /// <summary>Removes members (a folder with everything below it) from an unencrypted archive.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    var names = entryNames.Select(n => n.Replace('\\', '/').Trim('/')).ToList();
    Rewrite(archive, e => !names.Any(n => e.FileName == n || e.FileName.StartsWith(n + "/", StringComparison.Ordinal)), _ => { });
  }

  private static void Rewrite(Stream archive, Func<B1Entry, bool> keep, Action<B1Writer> append) {
    var r = new B1Reader(archive);
    if (r.IsEncrypted)
      throw new NotSupportedException("Editing an encrypted B1 archive is not supported.");
    var w = new B1Writer();
    foreach (var e in r.Entries.Where(keep)) {
      if (e.IsDirectory) w.AddFolder(e.FileName, e.LastModified);
      else w.AddFile(e.FileName, r.Extract(e), e.LastModified);
    }

    append(w);
    var bytes = w.ToArray(r.IsCompressed);
    archive.Position = 0;
    archive.Write(bytes);
    archive.SetLength(bytes.Length);
    archive.Flush();
  }
}
