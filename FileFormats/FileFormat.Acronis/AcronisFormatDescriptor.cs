#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Acronis;

/// <summary>
/// Format descriptor for Acronis True Image classic .tib backups.
/// </summary>
/// <remarks>
/// <para>
/// Detects by magic <c>CE 24 B9 A2</c> (LE 0xA2B924CE) at offset 0. Surfaces file names + sizes
/// via the Listing record stream documented in the upstream RE (https://github.com/dennisss/acronis-tib).
/// </para>
/// <para>
/// File extraction is supported via sequential pairing: per-file metadata records (102/1/2/5)
/// remain undocumented across every public source surveyed (dennisss + every fork; TIB-ShellEx
/// is a repackage of Acronis's closed-source DLLs; MultiExtractor / R-Studio / X-Ways are
/// closed-source commercial; no academic forensic paper, no Russian-language RE write-up). But
/// the RecordIndex(108) layout IS fully parsed (8-byte magic + uint48 totalSize + uint32
/// numHandles + handles of {uint48 startOffset, uint48 recordOffset, 16-byte MD5}), Blob(109)
/// is plain zlib, and the dennisss MetaOffset comment ("Reading sequential") plus the natural
/// invariant that backup archives emit one chain per file in Listing order let us pair the Nth
/// Listing entry with the Nth RecordIndex by archive position. Each per-file Listing.FileSize
/// is cross-checked against the paired RecordIndex.TotalSize before extraction; blob MD5s are
/// checked against the handle hashes after decompression. Mismatches at either gate yield a
/// hard failure rather than silent wrong content.
/// </para>
/// <para>
/// Out of scope: encrypted .tib backups, sector-by-sector slices, multi-volume slice chains,
/// and the .tibx format (Acronis True Image 2020+).
/// </para>
/// </remarks>
public sealed class AcronisFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "AcronisTib";
  public string DisplayName => "Acronis True Image (.tib)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".tib";
  public IReadOnlyList<string> Extensions => [".tib"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0xCE, 0x24, 0xB9, 0xA2], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate (record stream)")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Acronis True Image classic .tib backup — R/O listing + R/O file extraction via spec-grounded FileMeta chain walk (Listing.MetaOffset → FirstFileMetaRecord(102) → next RecordIndex(108)) with sequential pairing as fallback; FileMeta 102/1/2/5 body shape reverse-engineered as an InputItem attribute stream (uint32 count + N×{uint32 idAndFlags, uint16 size, body}) with the high-value ids decoded — ItemCommon(0x10)=filename+altname+DosAttributes+4 FILETIMEs (CreationTime/LastWriteTime/LastAccessTime/ChangeTime via BackupCommonAttributes writer) + trailer dword, SourceItem(0x40)=path, HardLinkId(0x14), BackupTime(0x50), TimeZone(0x60), Replica(0x17)=GUID+2 cookies, ItemCommonExtra(0x18)=uint64 cookie, SliceItem(0x80)=GUID+2 cookies+flag+optional name, SliceItemBlob(0x90)=opaque variable bytes; per-entry DecodedName surfaced when the 102 body parses; per-blob MD5 integrity check still gates extraction. Documented-TODO: ACL blob shape on record-5 bodies — the per-id handler was not located in the binary inspection pass; record-5 bodies are still surfaced as attribute streams if they decode as such, otherwise as raw payload.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var r = new AcronisReader(stream);
    return r.Entries.Select((e, i) => {
      var full = string.IsNullOrEmpty(e.Path)
        ? e.Name
        : e.Path.TrimEnd('/', '\\') + "/" + e.Name;
      return new ArchiveEntryInfo(
        Index: i,
        Name: full,
        OriginalSize: e.FileSize,
        CompressedSize: e.FileSize,
        Method: "Deflate",
        IsDirectory: false,
        IsEncrypted: false,
        LastModified: e.Time,
        Kind: null
      );
    }).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(outputDir);

    var r = new AcronisReader(stream);

    // Gate: at least one of the two pairing paths (chain walk or sequential) must hold.
    // Chain walk is authoritative when complete; sequential is the documented fallback.
    var chainOk = r.ChainWalkComplete;
    var sequentialOk = r.CanExtractByPairing(out var pairingReason);
    if (!chainOk && !sequentialOk) {
      // Refuse to write any file — neither pairing path is safe. Honest fallback: never emit
      // data we can't structurally verify.
      throw new NotSupportedException(
        $"Acronis classic .tib extraction: neither FileMeta chain walk nor sequential pairing resolved. "
        + $"Chain walk: {(r.ChainWalkComplete ? "complete" : "incomplete")}. "
        + $"Sequential pairing: {pairingReason ?? "ok"}. "
        + "The per-file FileMeta record (102/1/2/5) bodies are opaque across every public source surveyed "
        + "(dennisss/acronis-tib + all forks; TIB-ShellEx repackages Acronis closed-source DLLs; "
        + "MultiExtractor / R-Studio commercial). Listing is available via List().");
    }

    Directory.CreateDirectory(outputDir);

    // Build a filter set when callers request specific files.
    HashSet<string>? wanted = null;
    if (files is not null && files.Length > 0)
      wanted = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < r.Entries.Count; i++) {
      var entry = r.Entries[i];
      var fullName = string.IsNullOrEmpty(entry.Path)
        ? entry.Name
        : entry.Path.TrimEnd('/', '\\') + "/" + entry.Name;

      if (wanted is not null && !wanted.Contains(fullName) && !wanted.Contains(entry.Name)) continue;

      var result = r.ExtractFile(i);
      if (!result.IntegrityValid)
        throw new InvalidDataException(
          $"Acronis: extracted file '{fullName}' failed MD5 integrity check against RecordIndex handle hashes. "
          + "Sequential pairing assumption may be wrong for this slice — refusing to write potentially corrupt data.");

      // Sanitize the output path to stay inside outputDir (defense against path traversal).
      var safeRel = SanitizeRelativePath(fullName);
      var outPath = Path.Combine(outputDir, safeRel);
      var parent = Path.GetDirectoryName(outPath);
      if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
      File.WriteAllBytes(outPath, result.Data);
      if (entry.Time is { } when) {
        try { File.SetLastWriteTimeUtc(outPath, when.ToUniversalTime()); } catch { /* best-effort */ }
      }
    }
  }

  private static string SanitizeRelativePath(string name) {
    var s = name.Replace('\\', '/');
    while (s.StartsWith('/')) s = s[1..];
    // Strip Windows drive prefixes like "C:" or "C:/"
    if (s.Length >= 2 && s[1] == ':') s = s.Length > 2 ? s[2..].TrimStart('/') : "";
    // Block ".." segments to prevent traversal.
    var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
    var safe = parts.Where(p => p != "..").ToArray();
    return Path.Combine(safe);
  }
}
