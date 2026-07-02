#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.JuiceFs;

/// <summary>
/// Stage 0 detection-only descriptor for JuiceFS artefacts.
/// JuiceFS has no standalone on-disk image format: a volume is the
/// combination of an external metadata engine (Redis / MySQL / TiKV /
/// SQLite / PostgreSQL / etcd / FoundationDB / BadgerDB) plus chunks
/// living in an S3-compatible object store. None of these surfaces are
/// resolvable from a single local file, so R/O extraction is genuinely
/// impossible without those external endpoints; staying Stage 0 is the
/// honest treatment.
/// Surfaces only a synthetic <c>metadata.ini</c> and the raw image bytes;
/// no real file-walk is attempted.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://juicefs.com</c> — official JuiceFS site and architecture documentation (metadata engine + object-store chunks)</description></item>
///   <item><description><c>https://github.com/juicedata/juicefs</c> — canonical source</description></item>
/// </list>
/// </summary>
public sealed class JuiceFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "JuiceFs";
  public string DisplayName => "JuiceFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".juicefs";
  public IReadOnlyList<string> Extensions => [".juicefs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Wrapper-convention tag: ASCII "JuiceFS" (7 bytes) at offset 0.
    // Note: real JuiceFS artefacts have NO offset-0 magic. The binary
    // backup (juicefs dump --binary, JuiceFS 1.3+) stores its BakMagic
    // 0x00747083 (4 bytes BE) in the BakEOS marker + protobuf footer
    // at end-of-file (juicedata/juicefs pkg/meta/backup.go). The JSON
    // dump (juicefs dump) is plain JSON. The SQLite metadata backend
    // is a standard SQLite database (magic "SQLite format 3\0").
    // The offset-0 "JuiceFS" tag here is the project's own wrapper
    // marker for surfacing detection — not a real JuiceFS signature.
    new("JuiceFS"u8.ToArray(), Offset: 0, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "JuiceFS — detection-only — distributed POSIX FS with NO standalone on-disk image " +
    "format: a volume = external metadata DB (Redis/MySQL/TiKV/SQLite/PostgreSQL/etcd/" +
    "FoundationDB/BadgerDB) + chunks in S3-compatible object storage. R/O is structurally " +
    "impossible from a single local file because (a) inode→chunk-id resolution lives in " +
    "the metadata engine and (b) chunk bytes live behind an object-store endpoint. The " +
    "binary backup's real signature is the BakMagic 0x00747083 (4 bytes BE) in the EOS " +
    "marker + protobuf footer at end-of-file (juicefs 1.3+); the JSON dump is plain JSON; " +
    "the SQLite backend uses the standard SQLite header. The offset-0 'JuiceFS' tag is a " +
    "wrapper convention for surfacing detection only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new JuiceFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new JuiceFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new JuiceFsReader(archive);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"JuiceFS entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }
}
