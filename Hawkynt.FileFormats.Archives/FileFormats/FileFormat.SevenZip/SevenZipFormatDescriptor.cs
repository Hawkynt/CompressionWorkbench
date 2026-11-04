#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.SevenZip;

/// <summary>
/// 7-Zip (.7z) archive — LZMA/LZMA2-based container with solid compression and encrypted-header support.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.7-zip.org/7z.html</c> — official 7z format page (Igor Pavlov)</description></item>
///   <item><description><c>7zFormat.txt</c> in the 7-Zip / LZMA SDK sources — the structural reference</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/7z</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class SevenZipFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IFormatValidator, IArchiveCreatable, IArchiveModifiable, IArchiveLayoutMap, IWipeEmpty, IFormatOptionsSchema, ICompressionOptimizable {

  /// <inheritdoc />
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("Method", "Compression method", FormatOptionKind.Enum, "lzma2",
      AllowedValues: ["lzma2", "lzma", "ppmd", "bzip2", "deflate", "copy"]),
    new("Level", "Compression level", FormatOptionKind.Integer, "5",
      AllowedValues: ["1", "3", "5", "7", "9"]),
    new("DictSize", "Dictionary size", FormatOptionKind.Integer, "16777216",
      AllowedValues: ["65536", "1048576", "16777216", "67108864", "268435456"],
      Description: "LZMA/LZMA2 dictionary size in bytes (64 KiB - 256 MiB)."),
    new("SolidSize", "Solid block size", FormatOptionKind.Integer, "16777216",
      AllowedValues: ["0", "1048576", "16777216", "67108864", "1073741824"],
      Description: "Max solid block size in bytes. 0 = non-solid (each file independent)."),
    new("Password", "Password", FormatOptionKind.String, ""),
    new("HeaderCompression", "Compress the header", FormatOptionKind.Boolean, "false",
      Description: "Store the header LZMA-packed, as 7-Zip's -mhc=on does, when that is smaller. A plain header keeps the archive open to in-place edits; an encrypted header is always packed."),
  ];

  /// <summary>
  /// Adds new names through the genuine changed-byte append path: new files are
  /// compressed into one fresh solid block at the old next-header position and
  /// only the trailing descriptive header plus 32-byte signature header are
  /// replaced. Same-name updates and unsupported archive profiles fall back to
  /// verified rebuild. The in-place writer completes all profile checks and
  /// replacement-header serialization before its first archive write, so no
  /// O(total bytes) rollback snapshot is required around a supported append.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    var newFiles = new List<(string Name, byte[] Data, bool IsDirectory)>();
    foreach (var input in inputs) {
      if (string.IsNullOrEmpty(input.ArchiveName)) continue;
      newFiles.Add((input.ArchiveName, input.IsDirectory ? [] : input.ReadContent(), input.IsDirectory));
    }
    if (newFiles.Count == 0)
      return;

    try {
      archive.Position = 0;
      SevenZipInPlaceAdder.Add(archive, newFiles);
      return;
    } catch (NotSupportedException) {
      if (archive.CanSeek)
        archive.Position = 0;
    }

    // Same-name updates and archive profiles the in-place adder does not take
    // (an encoded header, BCJ2/AES folders) are rewritten with every entry's own
    // times, attributes (Unix modes and links included) and directories kept.
    var additions = inputs.Where(i => !i.IsDirectory && !string.IsNullOrEmpty(i.ArchiveName))
      .Select(i => (Entry: new SevenZipEntry {
        Name = i.ArchiveName.Replace('\\', '/'),
        LastWriteTime = i.InMemoryContent == null && File.Exists(i.FullPath) ? File.GetLastWriteTimeUtc(i.FullPath) : DateTime.UtcNow,
      }, Data: i.ReadContent()))
      .ToList();
    RebuildPreservingMetadata(archive, _ => true, additions);
  }

  /// <summary>
  /// Removes complete solid folders and empty-stream entries directly. The remover
  /// validates the layout and serializes the replacement next-header/signature
  /// before compacting packed streams, so unsupported profiles can fall back before
  /// mutation without cloning the whole archive. Cost is metadata plus bytes that
  /// physically follow removed packed streams.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    if (entryNames.Length == 0)
      return;

    try {
      archive.Position = 0;
      SevenZipInPlaceRemover.Remove(archive, entryNames);
      return;
    } catch (NotSupportedException) {
      if (archive.CanSeek)
        archive.Position = 0;
    }

    // Removing part of a solid folder means compressing it again; the archive is
    // rewritten with every surviving entry's own times, attributes and directories.
    // A name matches its exact path, or everything beneath it for a folder.
    var names = entryNames.Select(n => n.Replace('\\', '/').Trim('/')).Where(n => n.Length > 0).ToArray();
    RebuildPreservingMetadata(archive,
      e => !names.Any(n => string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase)
                           || e.Name.StartsWith(n + "/", StringComparison.OrdinalIgnoreCase)),
      []);
  }

  /// <summary>
  /// Rewrites the archive keeping every entry <paramref name="keep" /> accepts —
  /// with its own modification and creation time and attributes, directories and
  /// empty files included — and appending <paramref name="additions" /> (which
  /// replace entries of the same name). The result is checked before it replaces
  /// the original.
  /// </summary>
  private static void RebuildPreservingMetadata(Stream archive, Func<SevenZipEntry, bool> keep,
      IReadOnlyList<(SevenZipEntry Entry, byte[] Data)> additions) {
    archive.Position = 0;
    var reader = new SevenZipReader(archive);
    if (reader.UncarriedMetadata is { Count: > 0 } uncarried)
      throw new NotSupportedException(
        $"7z: the archive holds {string.Join(", ", uncarried)}, which a rewrite cannot carry. Refused, nothing was changed.");
    var replaced = additions.Select(a => a.Entry.Name).ToHashSet(StringComparer.Ordinal);
    using var staged = new MemoryStream();
    var expected = new List<string>();
    var writer = new SevenZipWriter(staged, SevenZipCodec.Lzma2, leaveOpen: true);
    for (var i = 0; i < reader.Entries.Count; i++) {
      var e = reader.Entries[i];
      if (!keep(e) || replaced.Contains(e.Name)) continue;
      var copy = new SevenZipEntry {
        Name = e.Name, LastWriteTime = e.LastWriteTime, CreationTime = e.CreationTime,
        LastAccessTime = e.LastAccessTime, Attributes = e.Attributes,
      };
      if (e.IsDirectory) writer.AddDirectory(copy);
      else writer.AddEntry(copy, reader.Extract(i));
      expected.Add(e.Name);
    }
    foreach (var (entry, data) in additions) {
      writer.AddEntry(entry, data);
      expected.Add(entry.Name);
    }
    writer.Finish();

    staged.Position = 0;
    var check = new SevenZipReader(staged).Entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal);
    if (!check.SequenceEqual(expected.OrderBy(n => n, StringComparer.Ordinal), StringComparer.Ordinal))
      throw new InvalidOperationException("7z: the rewritten archive does not list the expected entries; the original was kept.");

    archive.Position = 0;
    staged.Position = 0;
    staged.CopyTo(archive);
    archive.SetLength(staged.Length);
    archive.Flush();
  }

  /// <summary>
  /// Recompresses the archive as one LZMA2 solid block with a dictionary sized to the
  /// payload, keeping every entry's name, modification, creation and access time and attributes
  /// (Unix modes and links included), directories and empty files.
  /// </summary>
  /// <exception cref="NotSupportedException">The archive is encrypted (a rewrite would have to
  /// decide how to encrypt it again), or its header carries properties the reader does not
  /// interpret — anti-items, start positions, archive properties — which a rewrite would drop.</exception>
  public void OptimizeCompression(Stream input, Stream output, string? password = null) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    input.Position = 0;
    var reader = new SevenZipReader(input, leaveOpen: true, password: password);
    if (reader.IsHeaderEncrypted || reader.Entries.Any(static e => e.IsEncrypted))
      throw new NotSupportedException("7z: the archive is encrypted; recompressing it would have to re-encrypt it. Refused, nothing was changed.");
    if (reader.UncarriedMetadata is { Count: > 0 } uncarried)
      throw new NotSupportedException($"7z: the archive holds {string.Join(" and ", uncarried)}, which a rewrite cannot carry. Refused, nothing was changed.");

    var payload = reader.Entries.Where(static e => !e.IsDirectory).Sum(static e => Math.Max(0L, e.Size));
    var dictionary = (int)Math.Clamp(System.Numerics.BitOperations.RoundUpToPowerOf2((ulong)Math.Max(payload, 1L << 16)), 1UL << 16, 1UL << 26);
    output.Position = 0;
    output.SetLength(0);
    var writer = new SevenZipWriter(output, SevenZipCodec.Lzma2, leaveOpen: true, dictionarySize: dictionary);
    for (var i = 0; i < reader.Entries.Count; i++) {
      var e = reader.Entries[i];
      var copy = new SevenZipEntry {
        Name = e.Name, LastWriteTime = e.LastWriteTime, CreationTime = e.CreationTime,
        LastAccessTime = e.LastAccessTime, Attributes = e.Attributes,
      };
      if (e.IsDirectory) writer.AddDirectory(copy);
      else writer.AddEntry(copy, reader.Extract(i));
    }
    writer.Finish();
    output.Flush();
    output.Position = 0;
  }

  // Not IArchiveDefragmentable: an archive has no free-space layout to defragment,
  // and the repack that stood in for it kept only names and bytes — 7z timestamps,
  // attributes/modes, owners, comments, links and methods were reset or dropped.



  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => SevenZipLayoutMap.Enumerate(archive);

  /// <summary>
  /// Zeros every dead byte in the 7z archive: gaps between packed solid blocks
  /// and any junk before the compressed metadata or trailing the file. The
  /// signature header, solid blocks and end-of-archive metadata are live and
  /// preserved, so the archive still extracts byte-identically. Cluster-tip
  /// wiping is N/A (7z packs solid blocks with no per-file slack).
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = SevenZipLayoutMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "SevenZip";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "7z";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: supported plain-header layouts use native changed-byte edits; profiles
  // requiring solid-block recompression or metadata decoding use verified rebuild.
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsPassword | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".7z";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".7z"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C], Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("lzma2", "LZMA2"), new("lzma", "LZMA"), new("ppmd", "PPMd"),
    new("bzip2", "BZip2"), new("deflate", "Deflate"), new("copy", "Store")
  ];
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
  public string Description => "7-Zip archive with LZMA2, high compression ratio";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  /// <remarks>
  /// Anti-items — the deletion markers of a differential update archive — are not listed: they
  /// name something that no longer exists and are never extracted.
  /// </remarks>
  /// <remarks>
  /// A Unix symbolic link (Unix mode type <c>S_IFLNK</c>, written by <c>7z a -snl</c> on Unix)
  /// is listed as a link with its target, which is the entry's data.
  /// </remarks>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new SevenZipReader(stream, leaveOpen: true, password: password);
    var list = new List<ArchiveEntryInfo>();
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (e.IsAnti)
        continue;
      var target = e.IsSymlink && e.Size <= 1 << 16 ? System.Text.Encoding.UTF8.GetString(r.Extract(i)) : null;
      list.Add(new ArchiveEntryInfo(i, e.Name, e.Size, e.CompressedSize,
        string.IsNullOrEmpty(e.Method) ? "7z" : e.Method, e.IsDirectory, e.IsEncrypted || r.IsHeaderEncrypted, e.LastWriteTime,
        IsSymlink: e.IsSymlink, LinkTarget: target));
    }
    return list;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  /// <remarks>
  /// Each folder is decoded once and its files are streamed to disk as they come out, every one
  /// CRC-checked; a file whose data fails is deleted again, so nothing wrong is left behind.
  /// Anti-items are skipped.
  /// </remarks>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new SevenZipReader(stream, leaveOpen: true, password: password);
    bool Wanted(SevenZipEntry e) => !e.IsAnti && (files == null || MatchesFilter(e.Name, files));

    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (!Wanted(e)) continue;
      if (e.IsDirectory) {
        if (SafeDirectory(outputDir, e.Name) is { } dir)
          Directory.CreateDirectory(dir);
        continue;
      }
      if (e.Size == 0)
        CreateEntryFile(outputDir, e.Name).Dispose();
    }

    var open = new Dictionary<int, FileStream>();
    try {
      r.ExtractMany(i => {
        var e = r.Entries[i];
        if (!Wanted(e) || e.IsDirectory) return null;
        var file = CreateEntryFile(outputDir, e.Name);
        open[i] = file;
        return file;
      }, (i, s) => {
        s.Dispose();
        open.Remove(i);
      });
    } catch {
      foreach (var file in open.Values) {
        var path = file.Name;
        file.Dispose();
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
      }
      throw;
    }
  }

  /// <summary>
  /// The directory an entry names below <paramref name="outputDir"/>, or <see langword="null"/>
  /// when the name would climb out of it.
  /// </summary>
  private static string? SafeDirectory(string outputDir, string name) {
    var segments = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length == 0 || segments.Any(static s => s is "." or ".."))
      return null;
    var invalid = Path.GetInvalidFileNameChars();
    var safe = segments.Select(s => new string([.. s.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c)]));
    return Path.Combine([outputDir, .. safe]);
  }

  /// <summary>
  /// Opens a single 7z entry as a read-only <see cref="Stream"/> bounded
  /// to its uncompressed size. 7z is solid-block: the underlying reader
  /// must decompress the whole containing folder to extract any one entry
  /// — the existing in-memory path is preserved — but the returned view
  /// is a <see cref="BoundedEntryStream"/> sized to the single entry's
  /// logical bytes, so neighbouring entries within the same solid block
  /// are physically unreachable through it.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    using var r = new SevenZipReader(archive, leaveOpen: true, password: password);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (e.IsDirectory || e.IsAnti) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(i);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false),
        bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream(System.Array.Empty<byte>(), writable: false),
      0, leaveOpen: false);
  }

  /// <summary>
  /// Native in-memory single-entry extraction. Routed through the bounded
  /// <see cref="OpenEntry"/> so the per-entry isolation contract holds
  /// uniformly across descriptors.
  /// </summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Builds a 7z archive from <paramref name="inputs"/>. Plans solid blocks by
  /// extension similarity, segregates incompressible files, and per-block
  /// recommends BCJ x86 filter for executables.
  /// </summary>
  /// <remarks>
  /// Inputs arrive as bytes here; <see cref="CreateFromStreams"/> takes them as streams and
  /// encodes each solid block as a stream through its coders (the header, written last, records
  /// the sizes and CRCs gathered on the way), so files larger than a buffer go in too.
  /// </remarks>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var (defaultCodec, blockSize, newWriter) = ResolveWriter(options);

    // Detect incompressible files via entropy unless caller already passed a set
    // or explicitly disabled detection with ForceCompress.
    var incompressible = options.ForceCompress
      ? null
      : options.IncompressiblePaths ?? SolidBlockPlanner.DetectIncompressible(inputs);

    var solidBlocks = SolidBlockPlanner.Plan(inputs, blockSize, incompressible);

    var needsMultiCodec = solidBlocks.Any(b =>
      SolidBlockPlanner.RecommendCodec(b, defaultCodec) != defaultCodec ||
      SolidBlockPlanner.RecommendFilter(b) != SevenZipFilter.None);

    var w = newWriter(output);

    foreach (var i in inputs)
      if (i.IsDirectory) w.AddDirectory(EntryFor(i));

    var fileEntryIndex = 0;
    var blockDescs = new List<SevenZipWriter.BlockDescriptor>();
    foreach (var block in solidBlocks) {
      var indices = new int[block.Files.Count];
      for (var j = 0; j < block.Files.Count; j++) {
        var (input, data) = block.Files[j];
        w.AddEntry(EntryFor(input), data);
        indices[j] = fileEntryIndex++;
      }
      if (needsMultiCodec) {
        var blockCodec = SolidBlockPlanner.RecommendCodec(block, defaultCodec);
        var blockFilter = SolidBlockPlanner.RecommendFilter(block);
        blockDescs.Add(new SevenZipWriter.BlockDescriptor {
          EntryIndices = indices,
          Codec = blockCodec != defaultCodec ? blockCodec : null,
          Filter = blockFilter != SevenZipFilter.None ? blockFilter : null,
        });
      }
    }

    if (needsMultiCodec) {
      w.FinishWithBlocks(blockDescs, maxThreads: options.Threads);
    } else {
      w.Finish(maxThreads: options.Threads, maxBlockSize: options.Threads > 1 ? blockSize : 0);
    }
  }

  /// <summary>
  /// The writer settings of <paramref name="options"/>: each knob from the direct property, else
  /// the schema option, else the default; returns the default codec, the solid block size and a
  /// factory for the configured writer.
  /// </summary>
  private static (SevenZipCodec Codec, long BlockSize, Func<Stream, SevenZipWriter> NewWriter) ResolveWriter(FormatCreateOptions options) {
    var methodName = !string.IsNullOrEmpty(options.MethodName) ? options.MethodName : options.GetOption("Method", "lzma2");
    var codec = SevenZipOptionsResolver.ResolveCodec(methodName);

    var directDict = options.DictSize;
    var schemaDict = options.GetOptionInt("DictSize", -1);
    var effectiveDict = directDict > 0 ? directDict : (schemaDict > 0 ? schemaDict : 0L);
    var dictSize = codec == SevenZipCodec.PPMd
      ? SevenZipOptionsResolver.ResolvePpmdMemorySize(effectiveDict)
      : codec == SevenZipCodec.BZip2
        ? SevenZipOptionsResolver.ResolveBzip2BlockSize(effectiveDict) * 100 * 1024
        : SevenZipOptionsResolver.ResolveLzmaDictSize(effectiveDict, options.Optimize);
    var ppmdOrder = SevenZipOptionsResolver.ResolvePpmdOrder(options.WordSize);
    var ppmdMem = SevenZipOptionsResolver.ResolvePpmdMemorySize(effectiveDict);

    var directSolid = options.SolidSize;
    var schemaSolid = (long)options.GetOptionInt("SolidSize", -1);
    var solid = directSolid > 0 ? directSolid : (schemaSolid >= 0 ? schemaSolid : 0L);
    var blockSize = solid > 0 ? solid : SolidBlockPlanner.DefaultMaxBlockSize;

    var passwordFromSchema = options.GetOption("Password", "");
    var password = !string.IsNullOrEmpty(options.Password) ? options.Password
      : !string.IsNullOrEmpty(passwordFromSchema) ? passwordFromSchema : null;
    var compressHeaders = options.GetOption("HeaderCompression", "false").Trim().ToLowerInvariant() is "true" or "on" or "1" or "yes";

    return (codec, blockSize, output => new SevenZipWriter(output, codec, dictionarySize: dictSize,
      ppmdOrder: ppmdOrder, ppmdMemorySize: ppmdMem, password: password,
      encryptHeaders: options.EncryptFilenames, compressHeaders: compressHeaders));
  }

  /// <summary>
  /// Streams the inputs into the archive: every file is read only when its solid block is
  /// encoded, through the coders, so a file of any size — beyond the 2 GiB a buffer holds — goes
  /// in with bounded memory (LZMA2, Deflate, BZip2, Copy; filters but BCJ2).
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var (_, blockSize, newWriter) = ResolveWriter(options);
    var writer = newWriter(output);
    foreach (var input in inputs) {
      var entry = EntryFor(new ArchiveInputInfo("", input.Name, input.IsDirectory, Metadata: input.Metadata));
      if (input.IsDirectory)
        writer.AddDirectory(entry);
      else
        writer.AddEntry(entry, input.OpenStream, input.Size);
    }
    writer.Finish(options.Threads, options.Threads > 1 ? blockSize : 0);
  }

  /// <summary>
  /// The 7z entry for an input, with the times and attributes 7-Zip would record: the input's
  /// own metadata when it carries some, else what the file system says about the file; a Unix
  /// mode goes into the high attribute bits as Unix 7-Zip writes it.
  /// </summary>
  private static SevenZipEntry EntryFor(ArchiveInputInfo input) {
    var meta = input.Metadata;
    var onDisk = input.InMemoryContent == null && (input.IsDirectory ? Directory.Exists(input.FullPath) : File.Exists(input.FullPath));
    var entry = new SevenZipEntry {
      Name = input.ArchiveName.Replace('\\', '/'),
      IsDirectory = input.IsDirectory,
      LastWriteTime = meta?.LastWriteTimeUtc?.UtcDateTime ?? (onDisk ? File.GetLastWriteTimeUtc(input.FullPath) : null),
      CreationTime = meta?.CreationTimeUtc?.UtcDateTime,
      LastAccessTime = meta?.LastAccessTimeUtc?.UtcDateTime,
    };
    if (meta?.UnixMode is { } mode)
      entry.Attributes = SevenZipEntry.AttributesForUnixMode((input.IsDirectory ? 0x4000 : 0x8000) | (mode & 0xFFF));
    else if (onDisk && OperatingSystem.IsWindows())
      // Read-only, hidden, system, directory, archive: the attributes 7-Zip restores.
      entry.Attributes = (uint)File.GetAttributes(input.FullPath) & 0x37u;
    return entry;
  }

  // ── IFormatValidator ─────────────────────────────────────────────

  /// <summary>
  /// Validates the supplied data.
  /// </summary>
  public ValidationResult ValidateHeader(ReadOnlySpan<byte> header, long fileSize) {
    var issues = new List<ValidationIssue>();
    if (header.Length < SevenZipConstants.SignatureHeaderSize) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "7Z_TOO_SHORT",
        $"File too short for 7z signature header (need {SevenZipConstants.SignatureHeaderSize} bytes)"));
      return new() { IsValid = false, Confidence = 0.3, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Header, Issues = issues };
    }
    var majorVersion = header[6];
    if (majorVersion > 0) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Warning, "7Z_UNKNOWN_MAJOR_VERSION",
        $"Unknown major version: {majorVersion} (expected 0)", 6));
    }
    // Verify StartHeaderCRC (CRC of bytes 12..31 = 20 bytes)
    var storedStartCrc = BitConverter.ToUInt32(header[8..]);
    var computedStartCrc = Compression.Core.Checksums.Crc32.Compute(header.Slice(12, 20));
    if (storedStartCrc != computedStartCrc) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "7Z_START_HEADER_CRC",
        $"Start header CRC mismatch: stored=0x{storedStartCrc:X8}, computed=0x{computedStartCrc:X8}", 8));
      return new() { IsValid = false, Confidence = 0.4, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Header, Issues = issues };
    }
    var nextHeaderOffset = BitConverter.ToInt64(header[12..]);
    var nextHeaderSize = BitConverter.ToInt64(header[20..]);
    if (nextHeaderOffset < 0 || nextHeaderSize < 0) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "7Z_NEGATIVE_OFFSET",
        $"Negative next header offset ({nextHeaderOffset}) or size ({nextHeaderSize})"));
      return new() { IsValid = false, Confidence = 0.4, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Header, Issues = issues };
    }
    var nextHeaderEnd = SevenZipConstants.SignatureHeaderSize + nextHeaderOffset + nextHeaderSize;
    if (nextHeaderEnd > fileSize) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Warning, "7Z_HEADER_BEYOND_EOF",
        $"Next header extends beyond file (offset={nextHeaderOffset}, size={nextHeaderSize}, fileSize={fileSize})"));
    }
    var confidence = issues.Count == 0 ? 0.92 : 0.75;
    var health = issues.Any(i => i.Severity >= IssueSeverity.Warning) ? FormatHealth.Degraded : FormatHealth.Good;
    return new() { IsValid = true, Confidence = confidence, Health = health,
      Level = ValidationLevel.Header, Issues = issues };
  }

  /// <summary>
  /// Validates the supplied data.
  /// </summary>
  public ValidationResult ValidateStructure(Stream stream) {
    var issues = new List<ValidationIssue>();
    try {
      stream.Seek(0, SeekOrigin.Begin);
      var headerBytes = new byte[SevenZipConstants.SignatureHeaderSize];
      stream.ReadExactly(headerBytes);
      var nextHeaderOffset = BitConverter.ToInt64(headerBytes, 12);
      var nextHeaderSize = BitConverter.ToInt64(headerBytes, 20);
      var nextHeaderCrc = BitConverter.ToUInt32(headerBytes, 28);
      var nextHeaderPos = SevenZipConstants.SignatureHeaderSize + nextHeaderOffset;
      if (nextHeaderPos + nextHeaderSize > stream.Length) {
        issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "7Z_NEXT_HEADER_TRUNCATED",
          "Next header extends beyond stream"));
        return new() { IsValid = false, Confidence = 0.6, Health = FormatHealth.Damaged,
          Level = ValidationLevel.Structure, Issues = issues };
      }
      stream.Seek(nextHeaderPos, SeekOrigin.Begin);
      var nextHeaderData = new byte[nextHeaderSize];
      stream.ReadExactly(nextHeaderData);
      var actualCrc = Compression.Core.Checksums.Crc32.Compute(nextHeaderData);
      if (actualCrc != nextHeaderCrc) {
        issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "7Z_NEXT_HEADER_CRC",
          $"Next header CRC mismatch: stored=0x{nextHeaderCrc:X8}, computed=0x{actualCrc:X8}"));
        return new() { IsValid = false, Confidence = 0.7, Health = FormatHealth.Damaged,
          Level = ValidationLevel.Structure, Issues = issues };
      }
      return new() { IsValid = true, Confidence = 0.93, Health = FormatHealth.Good,
        Level = ValidationLevel.Structure, Issues = issues };
    } catch (Exception ex) {
      issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "7Z_STRUCTURE_FAILED",
        $"Structure validation failed: {ex.Message}"));
      return new() { IsValid = false, Confidence = 0.5, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Structure, Issues = issues };
    }
  }

  /// <summary>
  /// Validates the supplied data.
  /// </summary>
  public ValidationResult ValidateIntegrity(Stream stream) {
    var issues = new List<ValidationIssue>();
    try {
      stream.Seek(0, SeekOrigin.Begin);
      using var r = new SevenZipReader(stream, leaveOpen: true);
      var validEntries = 0;
      var totalEntries = r.Entries.Count;
      for (var i = 0; i < totalEntries; ++i) {
        var e = r.Entries[i];
        if (e.IsDirectory || e.IsAnti) { ++validEntries; continue; }
        try {
          r.Extract(i, Stream.Null);
          ++validEntries;
        } catch (Exception ex) {
          issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "7Z_ENTRY_EXTRACT_FAILED",
            $"Entry '{e.Name}': {ex.Message}"));
        }
      }
      if (validEntries == totalEntries && issues.Count == 0) {
        return new() { IsValid = true, Confidence = 0.99, Health = FormatHealth.Perfect,
          Level = ValidationLevel.Integrity, Issues = issues,
          ValidEntries = validEntries, TotalEntries = totalEntries };
      }
      var health = validEntries == 0 ? FormatHealth.Damaged
        : validEntries < totalEntries ? FormatHealth.Degraded : FormatHealth.Good;
      return new() { IsValid = validEntries > 0, Confidence = validEntries > 0 ? 0.90 : 0.5,
        Health = health, Level = ValidationLevel.Integrity, Issues = issues,
        ValidEntries = validEntries, TotalEntries = totalEntries };
    } catch (Exception ex) {
      issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "7Z_INTEGRITY_FAILED",
        $"Integrity check failed: {ex.Message}"));
      return new() { IsValid = false, Confidence = 0.5, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Integrity, Issues = issues };
    }
  }
}
