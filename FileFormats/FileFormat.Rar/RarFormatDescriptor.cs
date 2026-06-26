#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Rar;

public sealed class RarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "RAR defragmentation is not supported — solid blocks, recovery records, and per-archive signatures " +
      "would all need to be regenerated, which is not safe via a generic rebuild path.");
  public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => RarLayoutMap.Enumerate(archive);

  public string Id => "Rar";
  public string DisplayName => "RAR";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable archive. Add/Replace/Remove go through the verified extract -> edit ->
  // re-create rebuild (default IArchiveModifiable), re-emitting a valid RAR5 via RarWriter.
  // The container is repacked (existing data moves) and optional recovery records are not
  // regenerated — acceptable for a read-write archive (unlike defrag, which must preserve
  // bytes and is therefore refused above). See FormatCapabilities.cs (WORM vs R/W).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsPassword | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".rar";
  public IReadOnlyList<string> Extensions => [".rar"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x00], Confidence: 0.95),
    new([(byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x01, 0x00], Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("rar5", "RAR 5"), new("rar4", "RAR 4"), new("store", "Store")
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "RAR archive with solid compression and recovery records. A pure add of new " +
    "files to a non-solid/recovery-free, unencrypted RAR5 archive is a genuine " +
    "O(bytes-added) in-place append: new non-solid FILE blocks are written before a " +
    "rewritten ENDARC, leaving every existing block byte-identical at its original " +
    "offset (RarInPlaceAdder). Anything that is not byte-additive — an encryption " +
    "header, a recovery-record (RR) or quick-open (QO) service block, a name that " +
    "collides with an existing entry, or a RAR4 archive — plus Remove and same-name " +
    "update fall back to the verified extract -> re-create rebuild.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new RarReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.CompressedSize,
      $"Method {e.CompressionMethod}", e.IsDirectory, e.IsEncrypted, e.ModifiedTime?.DateTime)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new RarReader(stream, password: password);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Name)); continue; }
      WriteFile(outputDir, e.Name, r.Extract(i));
    }
  }

  /// <summary>
  /// Opens a single RAR entry as a bounded read-only <see cref="Stream"/>.
  /// The reader's per-entry extractor returns the fully-decompressed bytes;
  /// they are wrapped in a <see cref="BoundedEntryStream"/> sized to the
  /// entry's uncompressed size so the universal per-entry isolation contract
  /// holds even though RAR's decoder produces a byte[].
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new RarReader(archive, leaveOpen: true, password: password);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(i);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false),
        bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream(System.Array.Empty<byte>(), writable: false),
      0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction — routed through the
  /// bounded <see cref="OpenEntry"/> so the per-entry isolation contract holds
  /// uniformly.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Builds a RAR archive from <paramref name="inputs"/>. Selects RAR4 or RAR5
  /// based on <c>options.MethodName</c> and resolves dictionary / level from
  /// <c>options.DictSize</c> / <c>options.Level</c>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var useRar4 = options.MethodName == "rar4";
    var useStore = options.MethodName is "store" or "copy";
    var rarLevel = useStore ? 0 : options.Level switch {
      0 => 0, 1 => 1, 2 => 2, 3 or 4 => 3, 5 or 6 => 4, >= 7 => 5, _ => 3,
    };

    if (useRar4) {
      var windowBits = options.DictSize > 0
        ? Math.Clamp((int)Math.Log2(options.DictSize), 15, 22) : 20;
      var rar4Method = (byte)(0x30 + rarLevel);
      var w4 = new Rar4Writer(output, method: rar4Method, windowBits: windowBits,
        solid: options.SolidSize == 0, password: options.Password);
      foreach (var i in inputs) {
        if (i.IsDirectory) continue; // RAR writer doesn't track empty dirs here
        w4.AddFile(i.ArchiveName, i.ReadContent());
      }
      w4.Finish();
    } else {
      var dictLog = options.DictSize > 0
        ? Math.Clamp((int)Math.Log2(options.DictSize), 17, 28) : 17;
      var w = new RarWriter(output, method: rarLevel, dictionarySizeLog: dictLog,
        solid: options.SolidSize == 0, password: options.Password,
        encryptHeaders: options.EncryptFilenames);
      foreach (var i in inputs) {
        if (i.IsDirectory) continue;
        w.AddFile(i.ArchiveName, i.ReadContent());
      }
      w.Finish();
    }
  }

  /// <summary>
  /// Appends (or same-name updates) <paramref name="inputs"/> in
  /// <paramref name="archive"/>. A pure add of new file names to a
  /// non-solid/recovery-free, unencrypted RAR5 archive takes the genuine in-place
  /// append (<see cref="RarInPlaceAdder"/>): new non-solid FILE blocks are written
  /// before a rewritten ENDARC, leaving every pre-existing block byte-identical at
  /// its original offset. A same-name <em>update</em> is attempted as an in-place
  /// remove of the old block (<see cref="RarInPlaceRemover"/>, only when the old
  /// block is not part of a solid run) followed by an in-place add of the new
  /// content. Any case that cannot be served byte-additively — encryption headers, a
  /// recovery-record (RR) or quick-open (QO) service block, a RAR4 archive, a
  /// directory input, or an update whose old block is part of a solid run — falls
  /// back to the verified extract -> re-create rebuild.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    // Snapshot the original bytes so a failed/aborted in-place attempt never
    // leaves the caller's stream half-written.
    archive.Position = 0;
    using var original = new MemoryStream();
    archive.CopyTo(original);
    var originalBytes = original.ToArray();

    // Directories cannot be appended in place; let the presence of any directory
    // input route the whole operation through the rebuild.
    var hasDirectory = false;
    var newFiles = new List<(string Name, byte[] Data, DateTimeOffset? ModifiedTime)>();
    foreach (var input in inputs) {
      if (string.IsNullOrEmpty(input.ArchiveName)) continue;
      if (input.IsDirectory) { hasDirectory = true; continue; }
      newFiles.Add((input.ArchiveName, input.ReadContent(), null));
    }

    if (!hasDirectory && newFiles.Count > 0) {
      // Same-name updates: excise the colliding old block in place (the adder
      // refuses collisions), then append the new content. A non-byte-additive
      // case throws and routes the whole operation to the rebuild below.
      var existing = ExistingNames(originalBytes);
      var collisions = newFiles.Where(f => existing.Contains(f.Name)).Select(f => f.Name).ToArray();
      try {
        using var work = new MemoryStream();
        work.Write(originalBytes, 0, originalBytes.Length);
        work.Position = 0;
        if (collisions.Length > 0)
          RarInPlaceRemover.Remove(work, collisions);
        RarInPlaceAdder.Add(work, newFiles);

        var result = work.ToArray();
        archive.Position = 0;
        archive.Write(result, 0, result.Length);
        archive.SetLength(result.Length);
        archive.Flush();
        return;
      } catch (NotSupportedException) {
        // Restore the untouched original, then take the verified rebuild path.
        archive.Position = 0;
        archive.Write(originalBytes, 0, originalBytes.Length);
        archive.SetLength(originalBytes.Length);
      }
    }

    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      foreach (var input in inputs) {
        if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
        var dest = Path.Combine(tmpDir, input.ArchiveName.Replace('/', Path.DirectorySeparatorChar));
        var destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
        File.WriteAllBytes(dest, input.ReadContent());
      }
    });
  }

  /// <summary>
  /// Removes the named entries from the RAR5 archive. Removing a non-solid FILE
  /// block from a recovery-free, unencrypted RAR5 archive is a genuine
  /// O(bytes-shifted) in-place remove: the block's <c>[header + data]</c> range is
  /// excised and the following blocks (and ENDARC) shift down to close the gap, so
  /// every surviving block stays byte-identical — the ones before the hole at their
  /// exact offset, the ones after shifted down (<see cref="RarInPlaceRemover"/>).
  /// Any case that cannot be served byte-additively — a target that is part of a
  /// solid run (itself solid, or immediately followed by a solid block that reuses
  /// its dictionary), an encryption header, a recovery-record (RR) or quick-open
  /// (QO) service block, or a RAR4 archive — falls back to the verified extract ->
  /// re-create rebuild.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);

    archive.Position = 0;
    using var original = new MemoryStream();
    archive.CopyTo(original);
    var originalBytes = original.ToArray();

    try {
      using var work = new MemoryStream();
      work.Write(originalBytes, 0, originalBytes.Length);
      work.Position = 0;
      RarInPlaceRemover.Remove(work, entryNames);

      var result = work.ToArray();
      archive.Position = 0;
      archive.Write(result, 0, result.Length);
      archive.SetLength(result.Length);
      archive.Flush();
      return;
    } catch (NotSupportedException) {
      archive.Position = 0;
      archive.Write(originalBytes, 0, originalBytes.Length);
      archive.SetLength(originalBytes.Length);
    }

    var skip = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        if (skip.Contains(rel) || skip.Contains(Path.GetFileName(rel)))
          File.Delete(file);
      }
    });
  }

  /// <summary>The non-directory entry names currently present in the RAR archive.</summary>
  private static HashSet<string> ExistingNames(byte[] archiveBytes) {
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    using var ms = new MemoryStream(archiveBytes, writable: false);
    using var r = new RarReader(ms, leaveOpen: true);
    foreach (var e in r.Entries)
      if (!e.IsDirectory)
        names.Add(e.Name);
    return names;
  }
}
