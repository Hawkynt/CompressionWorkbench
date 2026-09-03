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
public sealed class SevenZipFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IFormatValidator, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IWipeEmpty, IFormatOptionsSchema {

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

    var skip = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        if (skip.Contains(rel) || skip.Contains(Path.GetFileName(rel)))
          File.Delete(file);
      }
    });
  }

  /// <summary>Rebuild-based defrag: extracts then re-creates the 7z archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the 7z archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new SevenZipReader(stream);
        var list = new List<(string, byte[])>();
        for (var i = 0; i < r.Entries.Count; ++i) {
          var e = r.Entries[i];
          if (e.IsDirectory) continue;
          list.Add((e.Name, r.Extract(i)));
        }
        return list;
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        var w = new SevenZipWriter(ms, SevenZipCodec.Lzma2);
        foreach (var (n, d) in files)
          w.AddEntry(new SevenZipEntry { Name = n, Size = d.Length }, d);
        w.Finish();
        return ms.ToArray();
      });
  }


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
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new SevenZipReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.CompressedSize,
      string.IsNullOrEmpty(e.Method) ? "7z" : e.Method, e.IsDirectory, false, e.LastWriteTime)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new SevenZipReader(stream, password: password);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Name)); continue; }
      WriteFile(outputDir, e.Name, r.Extract(i));
    }
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
    var r = new SevenZipReader(archive, leaveOpen: true, password: password);
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
  /// This descriptor deliberately keeps the buffering
  /// <see cref="IArchiveCreatable.CreateFromStreams"/> default and does NOT
  /// override it. 7z uses solid compression: multiple files are concatenated
  /// into a single compressed coder stream, and the header (written last)
  /// encodes per-file unpack sizes and the block's total packed size, both of
  /// which are only known after every member of the block has been read and
  /// compressed. There is no per-entry append point at which a bounded
  /// chunk-copy could write a file's bytes independently, so a streaming
  /// override could not honor the bounded-memory contract without effectively
  /// buffering each solid block anyway. Faking a per-entry streaming path here
  /// would be dishonest, so the buffering default stands.
  /// </remarks>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // Three-tier fallback: FormatSpecific (schema) → direct property → hardcoded default.
    var methodName = !string.IsNullOrEmpty(options.MethodName)
      ? options.MethodName
      : options.GetOption("Method", "lzma2");
    var defaultCodec = SevenZipOptionsResolver.ResolveCodec(methodName);

    var directDict = options.DictSize;
    var schemaDict = options.GetOptionInt("DictSize", -1);
    var effectiveDict = directDict > 0 ? directDict : (schemaDict > 0 ? schemaDict : 0L);

    var dictSize = defaultCodec == SevenZipCodec.PPMd
      ? SevenZipOptionsResolver.ResolvePpmdMemorySize(effectiveDict)
      : defaultCodec == SevenZipCodec.BZip2
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

    // Detect incompressible files via entropy unless caller already passed a set
    // or explicitly disabled detection with ForceCompress.
    var incompressible = options.ForceCompress
      ? null
      : options.IncompressiblePaths ?? SolidBlockPlanner.DetectIncompressible(inputs);

    var solidBlocks = SolidBlockPlanner.Plan(inputs, blockSize, incompressible);

    var needsMultiCodec = solidBlocks.Any(b =>
      SolidBlockPlanner.RecommendCodec(b, defaultCodec) != defaultCodec ||
      SolidBlockPlanner.RecommendFilter(b) != SevenZipFilter.None);

    var w = new SevenZipWriter(output, defaultCodec, dictionarySize: dictSize,
      ppmdOrder: ppmdOrder, ppmdMemorySize: ppmdMem, password: password,
      encryptHeaders: options.EncryptFilenames);

    foreach (var i in inputs)
      if (i.IsDirectory) w.AddDirectory(i.ArchiveName);

    var fileEntryIndex = 0;
    var blockDescs = new List<SevenZipWriter.BlockDescriptor>();
    foreach (var block in solidBlocks) {
      var indices = new int[block.Files.Count];
      for (var j = 0; j < block.Files.Count; j++) {
        var (input, data) = block.Files[j];
        w.AddEntry(new SevenZipEntry { Name = input.ArchiveName, Size = data.Length }, data);
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
      var r = new SevenZipReader(stream);
      var validEntries = 0;
      var totalEntries = r.Entries.Count;
      for (var i = 0; i < totalEntries; ++i) {
        var e = r.Entries[i];
        if (e.IsDirectory) { ++validEntries; continue; }
        try {
          _ = r.Extract(i);
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
