#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Zip;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.AndroidBundle;

/// <summary>
/// Archive view of an Android App Bundle (<c>.aab</c>) or split-APK set
/// (<c>.apks</c>). The underlying container is a ZIP; this descriptor re-exposes its
/// entries with the split-APK semantics surfaced in the path:
/// <list type="bullet">
///   <item><c>base/</c> sub-tree → <c>base/...</c> (verbatim).</item>
///   <item><c>splits/*.apk</c> top-level APKs → kept at <c>splits/*.apk</c>.</item>
///   <item><c>BundleConfig.pb</c> → kept at root.</item>
/// </list>
/// <para>
/// The actual content is a ZIP, so detection is extension-based; at the raw-magic level
/// this still looks like any other PK-signed ZIP and the Zip / Apk descriptors would
/// also match if routed by magic. This descriptor intentionally declares a lower
/// detection confidence for the ZIP local-file header so Zip/Apk win on ambiguous
/// inputs.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://developer.android.com/guide/app-bundle</c> — official Android App Bundle documentation</description></item>
///   <item><description><c>https://github.com/google/bundletool</c> — bundletool — the canonical .aab / .apks tool</description></item>
///   <item><description><c>https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT</c> — PKWARE APPNOTE — the underlying ZIP container spec</description></item>
/// </list>
/// </summary>
public sealed class AndroidBundleFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IWipeEmpty {

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => ZipLayoutMap.Enumerate(archive);

  /// <summary>Rebuild-based defrag delegating to ZIP (AAB/APKS is a ZIP variant).</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag delegating to ZIP (AAB/APKS is a ZIP variant).</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new ZipReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.FileName, r.ExtractEntry(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new ZipWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddEntry(n, d);
          w.Finish();
        }
        return ms.ToArray();
      });
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing bundle. Routes to
  /// <see cref="ZipModifier"/> for true random-access I/O — only the central
  /// directory, EOCD, and the appended file's local file header + compressed data
  /// are read or written; pre-existing entries stay byte-identical. The synthetic
  /// <c>metadata.ini</c> extraction artifact is a derived view and is skipped.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      if (string.Equals(name, "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
      ZipModifier.RemoveFile(archive, name, wipeData: true);
      ZipModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes named entries via <see cref="ZipModifier"/>. The synthetic
  /// <c>metadata.ini</c> extraction artifact is a derived view and is skipped.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames) {
      if (string.Equals(name, "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
      ZipModifier.RemoveFile(archive, name, wipeData: true);
    }
  }

  /// <summary>
  /// Zeros every dead byte in the bundle: gaps between entries not covered by a
  /// live extent in the ZIP layout map. Local headers, entry data, the central
  /// directory and EOCD are live and preserved. Cluster-tip wiping is N/A (ZIP
  /// packs entries back to back with no per-file slack).
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = ZipLayoutMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "AndroidBundle";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Android App Bundle / split-APK set";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable ZIP-based build artifact (bundles are signed at APK-generation
  // time, not in the .aab itself). Add/Replace/Remove are genuine in-place ZIP
  // edits (ZipModifier), matching the sibling APPX/APK descriptors. See
  // FormatCapabilities.cs (WORM vs R/W).
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".aab";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".aab", ".apks"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // PK local-file header. Intentionally low confidence so Zip/Apk outrank us on
    // extensionless inputs — AAB/APKS detection really wants the file extension.
    new([0x50, 0x4B, 0x03, 0x04], Confidence: 0.15),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate")];
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
    "Android App Bundle (.aab) or split-APK set (.apks) re-surfaced so split boundaries " +
    "are visible (base/, splits/, BundleConfig.pb).";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new ZipReader(stream, leaveOpen: true, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, RewriteName(e.FileName), e.UncompressedSize, e.CompressedSize,
      e.CompressionMethod.ToString(), e.IsDirectory, e.IsEncrypted, e.LastModified
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new ZipReader(stream, leaveOpen: true, password: password);
    foreach (var e in r.Entries) {
      var rewritten = RewriteName(e.FileName);
      if (files != null && !MatchesFilter(rewritten, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, rewritten)); continue; }
      var data = r.ExtractEntry(e);
      WriteFile(outputDir, rewritten, data);
      if (e.FileName.Equals("BundleConfig.pb", StringComparison.OrdinalIgnoreCase))
        WriteFile(outputDir, "metadata.ini", SummarizeBundleConfig(data));
    }
  }

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. The synthetic
  /// <c>metadata.ini</c> entry is materialised on the fly from
  /// <c>BundleConfig.pb</c>; all other entries delegate to the inner
  /// <see cref="ZipReader"/> and are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's uncompressed length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    using var r = new ZipReader(archive, leaveOpen: true, password: password);
    if (string.Equals(entryName, "metadata.ini", StringComparison.OrdinalIgnoreCase)) {
      foreach (var e in r.Entries) {
        if (!e.FileName.Equals("BundleConfig.pb", StringComparison.OrdinalIgnoreCase)) continue;
        var summary = SummarizeBundleConfig(r.ExtractEntry(e));
        return new Compression.Registry.Streaming.BoundedEntryStream(
          new MemoryStream(summary, writable: false), summary.Length, leaveOpen: false);
      }
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
    }
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      var rewritten = RewriteName(e.FileName);
      if (!string.Equals(rewritten, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.ExtractEntry(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>
  /// AAB entry names already carry the split structure (<c>base/</c>, <c>splits/</c>,
  /// <c>BundleConfig.pb</c>); this method is a no-op for recognised shapes and a
  /// passthrough otherwise so malformed bundles still extract.
  /// </summary>
  private static string RewriteName(string zipName) => zipName;

  /// <summary>
  /// Emits a fresh Android App Bundle (<c>.aab</c>) by delegating to
  /// <see cref="ZipWriter"/>. Entry paths are written verbatim; callers are
  /// responsible for naming entries with the AAB split-aware structure
  /// (<c>base/</c>, <c>splits/</c>, <c>BundleConfig.pb</c>). If the caller does
  /// not supply a <c>BundleConfig.pb</c>, a minimal placeholder protobuf is
  /// appended so the produced archive carries the mandatory configuration entry.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    using var w = new ZipWriter(output, leaveOpen: true);
    var hasBundleConfig = false;
    foreach (var i in inputs) {
      var name = i.ArchiveName.Replace('\\', '/');
      if (string.Equals(name, "BundleConfig.pb", StringComparison.OrdinalIgnoreCase))
        hasBundleConfig = true;
      if (i.IsDirectory) { w.AddDirectory(name); continue; }
      w.AddEntry(name, i.ReadContent());
    }
    if (!hasBundleConfig)
      w.AddEntry("BundleConfig.pb", BuildMinimalBundleConfigPlaceholder());
  }

  /// <summary>
  /// Builds a tiny placeholder protobuf payload for <c>BundleConfig.pb</c> when
  /// the caller doesn't supply one. The bytes form a well-typed protobuf message
  /// (one varint tag + length-delimited string field) that <c>protoc --decode_raw</c>
  /// will accept but which carries no real bundletool semantics; we ship it so
  /// produced archives always have the file the AAB spec mandates at the root.
  /// </summary>
  private static byte[] BuildMinimalBundleConfigPlaceholder() {
    // protobuf wire bytes: field 1 (length-delimited), len 12, "placeholder."
    var marker = "placeholder."u8.ToArray();
    var buf = new byte[2 + marker.Length];
    buf[0] = 0x0A; // tag = (1 << 3) | 2 (length-delimited)
    buf[1] = (byte)marker.Length;
    marker.AsSpan().CopyTo(buf.AsSpan(2));
    return buf;
  }

  /// <summary>
  /// Emits a best-effort plain-text summary of <c>BundleConfig.pb</c> (a protobuf).
  /// We don't decode the schema; instead we surface printable ASCII runs ≥4 bytes to
  /// give a readable-ish view of the config without pulling in a protobuf dependency.
  /// </summary>
  private static byte[] SummarizeBundleConfig(byte[] pb) {
    var sb = new StringBuilder();
    sb.Append("# BundleConfig.pb — printable string summary\n");
    sb.Append("# (raw protobuf; use `protoc --decode_raw` for full structure)\n\n");
    var run = new StringBuilder();
    foreach (var b in pb) {
      if (b >= 0x20 && b < 0x7F) {
        run.Append((char)b);
        continue;
      }
      if (run.Length >= 4) {
        sb.Append(run).Append('\n');
      }
      run.Clear();
    }
    if (run.Length >= 4) sb.Append(run).Append('\n');
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
