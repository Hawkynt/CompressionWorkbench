#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ha;

/// <summary>
/// HA (Harri Hirvola, 1993–1995): CPY, ASC (LZ77 + arithmetic coding) and HSC (order-4 context
/// model + arithmetic coding) members.
///
/// References:
/// <list type="bullet">
///   <item><description><c>docs/HA-ON-DISK.md</c> — the specification this is implemented from, written from HA 0.999 beta (GPL-2.0, read only)</description></item>
///   <item><description><c>https://github.com/val-khokhlov/ha</c> — HA 0.999b, whose <c>ha.exe</c> is the oracle in both directions</description></item>
/// </list>
/// </summary>
public sealed class HaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable,
  IArchiveDefragmentable, IArchiveLayoutMap {

  public string Id => "Ha";
  public string DisplayName => "HA";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: the writer emits HSC records ha.exe extracts (and byte-identical HSC streams); edits copy
  // every surviving record byte for byte.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".ha";
  public IReadOnlyList<string> Extensions => [".ha"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'H', (byte)'A'], Confidence: 0.60)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("hsc", "HSC")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "HA archive (Harri Hirvola): CPY, ASC and HSC members";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new HaReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.MethodName, e.IsDirectory, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new HaReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      if (e.IsDirectory) {
        Directory.CreateDirectory(Path.Combine(outputDir, e.FileName));
        continue;
      }
      WriteFile(outputDir, e.FileName, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new HaWriter(output, leaveOpen: true);
    foreach (var input in inputs) {
      if (string.IsNullOrEmpty(input.ArchiveName)) continue;
      var modified = input.Metadata?.LastWriteTimeUtc?.UtcDateTime
                     ?? (input.InMemoryContent == null && File.Exists(input.FullPath) ? File.GetLastWriteTimeUtc(input.FullPath) : null);
      if (input.IsDirectory) w.AddDirectory(input.ArchiveName, modified);
      else w.AddFile(input.ArchiveName, input.ReadContent(), modified);
    }
  }

  /// <summary>Adds (or replaces by name) files; every other record is copied byte for byte.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => HaModifier.Add(archive, [.. FilesOnly(inputs).Select(f => (f.Name, f.Data, (DateTime?)null))]);

  /// <summary>Removes named members; every other record is copied byte for byte.</summary>
  public void Remove(Stream archive, string[] entryNames) => HaModifier.Remove(archive, entryNames);

  /// <summary>Compacts away records <c>ha d</c> marked deleted, as <c>ha</c> does on its next write.</summary>
  public void Defragment(Stream archive) => HaModifier.Remove(archive, []);

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    using var r = new HaReader(archive, leaveOpen: true);
    yield return new DefragBlockInfo(0, 4, DefragBlockKind.MetadataReserved, FileName: "HA header");
    foreach (var e in r.Entries) {
      yield return new DefragBlockInfo(e.RecordOffset, e.HeaderLength, DefragBlockKind.MetadataReserved, FileName: $"Header: {e.FileName}");
      if (e.CompressedSize > 0)
        yield return new DefragBlockInfo(e.DataOffset, e.CompressedSize, DefragBlockKind.Used, FileName: e.FileName);
    }
  }
}
