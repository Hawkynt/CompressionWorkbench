#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Bkf;

/// <summary>
/// Microsoft NTBackup (<c>.bkf</c>) — Microsoft Tape Format (MTF) v1.0
/// container. Surfaces FILE/DIRB entries via the <c>STAN</c> (Standard) data
/// streams. Compressed streams are surfaced as "compressed" in the listing;
/// the MTF spec does not name a compression algorithm and most ntbackup.exe
/// writes are uncompressed.
///
/// <para>
/// In-place R/W tier: <see cref="Add"/> appends one FILE DBLK per input at the
/// position of the existing EOTM block (or at EOF when absent) and re-emits a
/// fresh EOTM at the new end, leaving every pre-existing DBLK byte-identical
/// at its original offset. <see cref="Remove"/> tombstones the matching FILE
/// DBLK by overwriting its 4-byte type field with the <c>XXXX</c> sentinel and
/// zero-wiping the rest of that FLB block; the reader's parse loop hits an
/// unknown DBLK type and skips it.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description>"Microsoft Tape Format Specification" v1.00a (Seagate Software, 1998) — the defining MTF document</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/NTBackup</c> — background on the creating tool</description></item>
/// </list>
/// </summary>
public sealed class BkfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveModifiable, IArchiveCreatable {

  public string Id => "Bkf";
  public string DisplayName => "Microsoft NTBackup (MTF)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanModify |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".bkf";
  public IReadOnlyList<string> Extensions => [".bkf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    // First DBLK is always TAPE — 4 ASCII bytes at offset 0.
    [new("TAPE"u8.ToArray(), Offset: 0, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored", "Stored"),
    new("compressed", "Compressed (passthrough)"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Microsoft NTBackup .bkf — MTF DBLK-chain reader (FILE+DATA) plus in-place " +
    "R/W via append-before-EOTM (Add) and XXXX tombstone (Remove).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new BkfReader(stream);
    var result = new List<ArchiveEntryInfo>(r.Entries.Count);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      var method = e.IsDirectory ? "stored" : (e.IsCompressed ? "compressed" : "stored");
      result.Add(new ArchiveEntryInfo(
        i, e.Name, e.Size, e.Size, method, e.IsDirectory, false, null
      ));
    }
    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new BkfReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      // Skip compressed payloads — MTF does not name the algorithm and we
      // refuse to fake content. They show up in List() so callers know.
      if (e.IsCompressed) continue;
      var data = r.Extract(e);
      WriteFile(outputDir, e.Name, data);
    }
  }

  // ── IArchiveCreatable ──────────────────────────────────────────────

  /// <summary>
  /// Produces a fresh MTF backup at <paramref name="output"/> from
  /// <paramref name="inputs"/>. Emits the full TAPE → SSET → VOLB →
  /// (DIRB → FILE*)* → ESET → EOTM DBLK chain via <see cref="BkfWriter"/>.
  /// Files are bucketed by their parent directory; directory inputs become
  /// DIRB blocks. Payloads are stored uncompressed.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var items = new List<BkfWriter.Item>(inputs.Count);
    foreach (var input in inputs)
      items.Add(input.IsDirectory
        ? new BkfWriter.Item(input.ArchiveName, [], IsDirectory: true)
        : new BkfWriter.Item(input.ArchiveName, input.ReadContent(), IsDirectory: false));

    var bytes = BkfWriter.Build(items);
    output.Write(bytes, 0, bytes.Length);
  }

  // ── IArchiveModifiable ─────────────────────────────────────────────

  /// <summary>
  /// Appends one FILE DBLK per non-directory input at the current EOTM
  /// position (or EOF when EOTM is absent), then re-emits a fresh EOTM. All
  /// existing DBLKs stay byte-identical at their original offsets.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FilesOnly(inputs)) {
      var leaf = Path.GetFileName(name);
      if (string.IsNullOrEmpty(leaf)) continue;
      BkfInPlaceModifier.AddFile(archive, leaf, data);
    }
  }

  /// <summary>
  /// Tombstones each named entry's FILE DBLK in place. The DBLK's 4-byte type
  /// field becomes the <c>XXXX</c> sentinel and the rest of that FLB block is
  /// zero-wiped so the file name and STAN payload leave no forensic trace.
  /// Surrounding DBLKs are not touched.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames) {
      var leaf = Path.GetFileName(name);
      if (string.IsNullOrEmpty(leaf)) continue;
      BkfInPlaceModifier.RemoveFile(archive, leaf);
    }
  }
}
