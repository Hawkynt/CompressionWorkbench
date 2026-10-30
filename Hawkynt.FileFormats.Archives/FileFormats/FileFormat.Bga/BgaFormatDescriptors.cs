#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Bga;

/// <summary>Shared GZA/BZA operations; the two differ only in the per-file compressor.</summary>
public abstract class BgaFormatDescriptorBase : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  private protected abstract bool Gzip { get; }

  public abstract string Id { get; }
  public abstract string DisplayName { get; }
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: BGA32.DLL 0.37 lists and extracts what we write; edits copy every surviving record
  // byte for byte (a self-extractor's stub too).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public abstract string DefaultExtension { get; }
  public IReadOnlyList<string> Extensions => [this.DefaultExtension];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new(this.Gzip ? "GZIP"u8.ToArray() : "BZ2\0"u8.ToArray(), Offset: 4, Confidence: 0.55)];
  public IReadOnlyList<FormatMethodInfo> Methods => [this.Gzip ? new("gzip", "gzip") : new("bzip2", "bzip2")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public abstract string Description { get; }

  private static byte[] ReadAll(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var data = ReadAll(stream);
    return BgaArchive.Read(data, out _).Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.IsDirectory ? "dir" : e.IsStored ? "store" : e.Method.ToLowerInvariant(), e.IsDirectory, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    foreach (var e in BgaArchive.Read(data, out _)) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      if (e.IsDirectory) Directory.CreateDirectory(Path.Combine(outputDir, e.FileName));
      else WriteFile(outputDir, e.FileName, BgaArchive.Extract(data, e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    foreach (var input in inputs) {
      if (string.IsNullOrEmpty(input.ArchiveName)) continue;
      var modified = input.Metadata?.LastWriteTimeUtc?.LocalDateTime
                     ?? (input.InMemoryContent == null && File.Exists(input.FullPath) ? File.GetLastWriteTime(input.FullPath) : null);
      output.Write(input.IsDirectory
        ? BgaArchive.BuildRecord(this.Gzip, input.ArchiveName, [], null, directory: true)
        : BgaArchive.BuildRecord(this.Gzip, input.ArchiveName, input.ReadContent(), modified, directory: false));
    }
  }

  /// <summary>Adds (or replaces by name) files; every other record is copied byte for byte.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var added = FilesOnly(inputs).ToList();
    var names = added.Select(f => f.Name.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
    this.Rewrite(archive, e => !names.Contains(e.FileName), added.Select(f => BgaArchive.BuildRecord(this.Gzip, f.Name, f.Data, DateTime.Now, false)));
  }

  /// <summary>Removes members by name; every other record is copied byte for byte.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    var names = entryNames.Select(n => n.Replace('\\', '/').TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
    this.Rewrite(archive, e => !names.Contains(e.FileName), []);
  }

  private void Rewrite(Stream archive, Func<BgaEntry, bool> keep, IEnumerable<byte[]> appended) {
    var data = ReadAll(archive);
    var entries = BgaArchive.Read(data, out var truncated);
    if (truncated)
      throw new NotSupportedException("The GZA/BZA archive has a damaged record; refusing to edit it.");
    if (entries.Count > 0 && entries[0].Method != (this.Gzip ? "GZIP" : "BZ2"))
      throw new NotSupportedException("An archive keeps one compressor; this one does not match the format chosen.");

    using var rebuilt = new MemoryStream();
    var first = entries.Count == 0 ? data.Length : (int)entries[0].RecordOffset;
    rebuilt.Write(data.AsSpan(0, first)); // self-extractor stub, if any
    foreach (var e in entries.Where(keep)) {
      var start = (int)e.RecordOffset;
      rebuilt.Write(data.AsSpan(start, (int)(e.DataOffset + e.CompressedSize) - start));
    }

    foreach (var record in appended)
      rebuilt.Write(record);
    archive.Position = 0;
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.SetLength(rebuilt.Length);
    archive.Flush();
  }
}

/// <summary>
/// GZA archive (BGA32.DLL, Toshinobu Kimura): files compressed one by one as gzip members.
///
/// References:
/// <list type="bullet">
///   <item><description><c>http://www.kmonos.net/repos/qbga32</c> — QBga32 (NYSL), converted for the record layout and checksum</description></item>
///   <item><description><c>https://www.madobe.net/archiver/lib/bga32.html</c> — BGA32.DLL 0.37, the oracle</description></item>
/// </list>
/// </summary>
public sealed class GzaFormatDescriptor : BgaFormatDescriptorBase {
  private protected override bool Gzip => true;
  public override string Id => "Gza";
  public override string DisplayName => "GZA (BGA)";
  public override string DefaultExtension => ".gza";
  public override string Description => "GZA archive (BGA32.DLL): files compressed individually with gzip";
}

/// <summary>
/// BZA archive (BGA32.DLL, Toshinobu Kimura): files compressed one by one as bzip2 streams.
///
/// References:
/// <list type="bullet">
///   <item><description><c>http://www.kmonos.net/repos/qbga32</c> — QBga32 (NYSL), converted for the record layout and checksum</description></item>
///   <item><description><c>https://www.madobe.net/archiver/lib/bga32.html</c> — BGA32.DLL 0.37, the oracle</description></item>
/// </list>
/// </summary>
public sealed class BzaFormatDescriptor : BgaFormatDescriptorBase {
  private protected override bool Gzip => false;
  public override string Id => "Bza";
  public override string DisplayName => "BZA (BGA)";
  public override string DefaultExtension => ".bza";
  public override string Description => "BZA archive (BGA32.DLL): files compressed individually with bzip2";
}
