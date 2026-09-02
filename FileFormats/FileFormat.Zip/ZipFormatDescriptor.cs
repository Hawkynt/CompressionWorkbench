#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Zip;

/// <summary>
/// ZIP archive — the universal container with per-entry compression methods.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT</c> — PKWARE APPNOTE.TXT — the canonical .ZIP file format specification</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/ZIP_(file_format)</c> — Wikipedia overview</description></item>
///   <item><description>Info-ZIP zip/unzip — long-standing open reference implementations</description></item>
/// </list>
/// </summary>
public sealed class ZipFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IFormatValidator, IArchiveModifiable, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap, IWipeEmpty, IArchiveShrinkable, IFormatOptionsSchema {

  /// <inheritdoc />
  /// <summary>
  /// Gets the options schema.
  /// </summary>
public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("Method", "Compression method", FormatOptionKind.Enum, "deflate",
      AllowedValues: ["deflate", "store", "deflate64", "bzip2", "lzma", "zstd"]),
    new("Level", "Compression level", FormatOptionKind.Integer, "5",
      AllowedValues: ["0", "1", "3", "5", "7", "9"],
      Description: "Compression level. 0 = fastest, 9 = max."),
    new("Password", "Password", FormatOptionKind.String, "",
      Description: "Optional ZipCrypto / AES password."),
    new("EncryptionMethod", "Encryption method", FormatOptionKind.Enum, "none",
      AllowedValues: ["none", "zipcrypto", "aes-128", "aes-192", "aes-256"]),
  ];

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => ZipLayoutMap.Enumerate(archive);

  /// <summary>
  /// A ZIP has no fixed media geometry; the single canonical size is the
  /// archive's minimal terminated length — the last byte of the end-of-central-
  /// directory record (plus its comment). Anything past that is trailing junk.
  /// </summary>
  public IReadOnlyList<long> CanonicalSizes => [0];

  /// <summary>
  /// Drops any bytes trailing the end-of-central-directory record — tape/disk
  /// padding, a stale second EOCD left by an in-place editor, or data appended
  /// after the archive was finalized. The central directory, every local file
  /// entry and the EOCD (including its comment) are copied through
  /// byte-identically, so the shrunk archive lists and extracts identically.
  /// When there is no trailing junk the output is byte-identical to the input.
  /// </summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // ZipEndOfCentralDirectory.Read leaves the stream positioned exactly one
    // byte past the EOCD comment — i.e. the first trailing-junk byte (or EOF
    // when the archive is already tight). That position is the minimal length.
    input.Position = 0;
    long keep;
    try {
      _ = ZipEndOfCentralDirectory.Read(input);
      keep = input.Position;
    } catch (InvalidDataException) {
      // No locatable EOCD: don't risk corrupting — copy everything through.
      keep = input.Length;
    }
    keep = Math.Min(keep, input.Length);

    input.Position = 0;
    var buf = new byte[64 * 1024];
    var remaining = keep;
    while (remaining > 0) {
      var chunk = (int)Math.Min(buf.Length, remaining);
      var read = input.Read(buf, 0, chunk);
      if (read == 0) break;
      output.Write(buf, 0, read);
      remaining -= read;
    }
  }

  /// <summary>Rebuild-based defrag: extracts every entry then re-creates the archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts every entry then re-creates the archive per the requested mode.</summary>
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
  /// Gets the id.
  /// </summary>
public string Id => "Zip";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "ZIP";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsPassword |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories |
    FormatCapabilities.SupportsOptimize;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing ZIP archive. Uses
  /// <see cref="ZipModifier"/> for true O(touched bytes) random-access I/O —
  /// only the central directory, the EOCD, and the appended file's local
  /// file header + compressed data are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      ZipModifier.RemoveFile(archive, name, wipeData: true);
      ZipModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes named entries from an existing ZIP archive. Uses
  /// <see cref="ZipModifier"/> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      ZipModifier.RemoveFile(archive, name, wipeData: true);
  }

  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".zip";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".zip", ".zipx"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'P', (byte)'K', 0x03, 0x04], Confidence: 0.95),
    new([(byte)'P', (byte)'K', 0x05, 0x06], Confidence: 0.90)
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [
    new("deflate", "Deflate", SupportsOptimize: true),
    new("store", "Store"), new("deflate64", "Deflate64"),
    new("bzip2", "BZip2"), new("lzma", "LZMA"), new("zstd", "Zstandard"), new("ppmd", "PPMd")
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
public string Description => "Universal archive with multiple compression methods";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ZipReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.UncompressedSize, e.CompressedSize,
      e.CompressionMethod.ToString(), e.IsDirectory, e.IsEncrypted, e.LastModified)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ZipReader(stream, password: password);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.FileName)); continue; }

      // Stored entries stream straight to disk; an entry past the array limit
      // cannot be materialised at all. Compressed ones still go through the
      // buffered decoder.
      using (var target = CreateEntryFile(outputDir, e.FileName)) {
        if (r.TryCopyEntryTo(e, target)) continue;
        var bytes = r.ExtractEntry(e);
        target.Write(bytes, 0, bytes.Length);
      }
    }
  }

  /// <summary>
  /// Opens a single ZIP entry as a read-only <see cref="Stream"/> bounded
  /// to its uncompressed size. The DEFLATE / store / etc. decoder runs
  /// against the entry's local-header bytes, the result is wrapped in a
  /// <see cref="BoundedEntryStream"/> sized to <c>UncompressedSize</c> so
  /// the next entry's bytes — which immediately follow in the source —
  /// can never bleed into the returned view even if the underlying decoder
  /// over-reads by a chunk boundary.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new ZipReader(archive, leaveOpen: true, password: password);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.FileName, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      // ZipReader.ExtractEntry already produces exactly UncompressedSize
      // bytes (CRC-checked) — wrapping in a bounded view is belt-and-braces
      // so callers can rely on the universal contract.
      var bytes = r.ExtractEntry(e);
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
  /// Builds a ZIP archive from <paramref name="inputs"/>. Honors all of
  /// <see cref="FormatCreateOptions"/>: method, level, dict-size, threads,
  /// password, encryption mode, and incompressibility hints.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // Three-tier fallback: FormatSpecific (schema) → direct property → hardcoded default.
    // The direct properties remain authoritative when set (legacy tests), but
    // when they're null/zero the schema knobs from FormatSpecific take over.
    var methodName = options.MethodName ?? options.GetOption("Method", "deflate");
    var passwordFromSchema = options.GetOption("Password", "");
    var password = !string.IsNullOrEmpty(options.Password) ? options.Password
      : !string.IsNullOrEmpty(passwordFromSchema) ? passwordFromSchema : null;
    var encryptionName = options.EncryptionMethod ?? options.GetOption("EncryptionMethod", "none");
    // Resolve explicit Level: direct property wins; otherwise fall back to schema
    // (sentinel -1 means "use the method default"). Hardcoded default stays in
    // ZipOptionsResolver.ResolveMethod.
    var explicitLevel = options.Level ?? (options.GetOptionInt("Level", -1) is var lv && lv >= 0 ? lv : (int?)null);

    var (zipMethod, level) = ZipOptionsResolver.ResolveMethod(methodName, options.Optimize);
    if (explicitLevel.HasValue)
      level = ZipOptionsResolver.ResolveDeflateLevel(explicitLevel.Value, options.Optimize);
    var encryption = ZipOptionsResolver.ResolveEncryption(encryptionName);

    if (options.Threads > 1 && inputs.Count(i => !i.IsDirectory) > 1) {
      ParallelZipCreator.CreateZipParallel(output, inputs, password, zipMethod, level,
        options.IncompressiblePaths, options.Threads, encryption);
      return;
    }

    var w = new ZipWriter(output, leaveOpen: true, compressionLevel: level,
      password: password, encryptionMethod: encryption);

    if (options.DictSize > 0 && zipMethod == ZipCompressionMethod.Lzma)
      w.LzmaDictionarySize = ZipOptionsResolver.NormalizeDictSize(
        ZipOptionsResolver.ResolveLzmaDictSize(options.DictSize, options.Optimize));
    w.LzmaLevel = ZipOptionsResolver.ResolveLzmaLevel(options.Level, options.Optimize);
    if (options.WordSize.HasValue && zipMethod == ZipCompressionMethod.Ppmd)
      w.PpmdOrder = Math.Clamp(options.WordSize.Value, 2, 16);
    if (options.DictSize > 0 && zipMethod == ZipCompressionMethod.Ppmd)
      w.PpmdMemorySizeMB = Math.Clamp((int)(options.DictSize / (1024 * 1024)), 1, 256);
    if (options.DictSize > 0 && zipMethod == ZipCompressionMethod.BZip2)
      w.Bzip2BlockSize = ZipOptionsResolver.ResolveBzip2BlockSize(options.DictSize);

    foreach (var i in inputs) {
      if (i.IsDirectory) { w.AddDirectory(i.ArchiveName); continue; }
      // ReadContent() transparently handles both on-disk inputs and the
      // in-memory variant fed by the small-image ConvertArchive pipeline.
      var data = i.ReadContent();
      var entryMethod = options.IncompressiblePaths != null && options.IncompressiblePaths.Contains(i.FullPath)
        ? ZipCompressionMethod.Store
        : zipMethod;
      w.AddEntry(i.ArchiveName, data, entryMethod);
    }
    w.Finish();
  }

  /// <summary>
  /// Large-file-safe streaming variant of <see cref="Create"/> for the STORE
  /// method. STORE entries are uncompressed, so the local header can be written
  /// with the pre-known <see cref="StreamingArchiveInput.Size"/> up front and
  /// the payload copied in 64 KB chunks while the CRC is computed incrementally
  /// and patched back into the header — peak memory is the copy buffer
  /// regardless of entry size. Output is byte-identical to <see cref="Create"/>
  /// with <c>Method=store</c>.
  /// </summary>
  /// <remarks>
  /// <para>Streaming applies only when the resolved method is STORE and no
  /// password/encryption is requested. DEFLATE and the other compressing
  /// methods (deflate64, bzip2, lzma, zstd, ppmd) keep the buffering default:
  /// the local header must carry the compressed size and CRC before the
  /// compressed bytes, which for a single-pass compressing writer would require
  /// either buffering the whole entry or emitting a data descriptor (changing
  /// the byte layout vs <see cref="Create"/>). Encrypted entries also buffer
  /// (encryption transforms the byte stream and AES needs a MAC trailer).</para>
  /// </remarks>
  public void CreateFromStreams(Stream target, IEnumerable<StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(inputs);

    var methodName = options.MethodName ?? options.GetOption("Method", "deflate");
    var passwordFromSchema = options.GetOption("Password", "");
    var password = !string.IsNullOrEmpty(options.Password) ? options.Password
      : !string.IsNullOrEmpty(passwordFromSchema) ? passwordFromSchema : null;
    var (zipMethod, _) = ZipOptionsResolver.ResolveMethod(methodName, options.Optimize);

    var canStream = zipMethod == ZipCompressionMethod.Store
      && string.IsNullOrEmpty(password)
      && target.CanSeek;

    if (!canStream) {
      var materialised = inputs as IReadOnlyList<StreamingArchiveInput> ?? inputs.ToList();

      // An entry past the array limit cannot be buffered, and DEFLATE here has no
      // streaming compressor (DeflateCompressor takes the whole input at once).
      // ZIP records a method per entry, so such an entry is streamed as STORE
      // while everything else still compresses normally. Storing is lossless and
      // universally readable; failing outright would not be.
      if (materialised.Any(i => !i.IsDirectory && i.Size > Array.MaxLength)) {
        if (!target.CanSeek)
          throw new NotSupportedException(
            "A ZIP entry larger than 2 GB needs a seekable target so its header can be patched after streaming.");
        if (!string.IsNullOrEmpty(password))
          throw new NotSupportedException(
            "A ZIP entry larger than 2 GB cannot be encrypted: encryption requires buffering the entry.");

        using var mixed = new ZipWriter(target, leaveOpen: true,
          compressionLevel: Compression.Core.Deflate.DeflateCompressionLevel.Default);
        foreach (var input in materialised) {
          if (input.IsDirectory) { mixed.AddDirectory(input.Name); continue; }
          using var src = input.OpenStream();
          if (input.Size > Array.MaxLength) {
            mixed.AddStreamingStoredEntry(input.Name, input.Size, src);
          } else {
            using var ms = new MemoryStream();
            src.CopyTo(ms);
            mixed.AddEntry(input.Name, ms.ToArray(), zipMethod);
          }
        }
        mixed.Finish();
        return;
      }

      // Buffering fallback: identical to the IArchiveCreatable default —
      // materialize each entry then dispatch to the classic Create. Honest
      // because compressing/encrypted entries can't stream a Create-identical
      // header without a data descriptor.
      var buffered = new List<ArchiveInputInfo>();
      foreach (var input in materialised) {
        if (input.IsDirectory) {
          buffered.Add(new ArchiveInputInfo(input.Name, input.Name, IsDirectory: true));
          continue;
        }
        using var src = input.OpenStream();
        using var ms = new MemoryStream();
        src.CopyTo(ms);
        buffered.Add(ArchiveInputInfo.InMemory(input.Name, ms.ToArray()));
      }
      this.Create(target, buffered, options);
      return;
    }

    var w = new ZipWriter(target, leaveOpen: true,
      compressionLevel: Compression.Core.Deflate.DeflateCompressionLevel.Default);
    foreach (var input in inputs) {
      if (input.IsDirectory) { w.AddDirectory(input.Name); continue; }
      using var src = input.OpenStream();
      w.AddStreamingStoredEntry(input.Name, input.Size, src);
    }
    w.Finish();
  }

  // ── IFormatValidator ─────────────────────────────────────────────

  /// <summary>
  /// Validates the supplied data.
  /// </summary>
public ValidationResult ValidateHeader(ReadOnlySpan<byte> header, long fileSize) {
    var issues = new List<ValidationIssue>();
    if (header.Length < 30) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "ZIP_TOO_SHORT",
        "File too short for local file header (need 30 bytes minimum)"));
      return new() { IsValid = false, Confidence = 0.3, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Header, Issues = issues };
    }
    var sig = BitConverter.ToUInt32(header);
    if (sig != ZipConstants.LocalFileHeaderSignature && sig != ZipConstants.EndOfCentralDirectorySignature) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "ZIP_BAD_SIGNATURE",
        $"Invalid ZIP signature: 0x{sig:X8}", 0));
      return new() { IsValid = false, Confidence = 0.2, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Header, Issues = issues };
    }
    if (sig == ZipConstants.LocalFileHeaderSignature) {
      var versionNeeded = BitConverter.ToUInt16(header[4..]);
      if (versionNeeded > 63) {
        issues.Add(new(ValidationLevel.Header, IssueSeverity.Warning, "ZIP_HIGH_VERSION",
          $"Version needed to extract is unusually high: {versionNeeded / 10}.{versionNeeded % 10}", 4));
      }
      var method = BitConverter.ToUInt16(header[8..]);
      if (method != 0 && method != 8 && method != 9 && method != 12 && method != 14 &&
          method != 93 && method != 98 && method != 99) {
        issues.Add(new(ValidationLevel.Header, IssueSeverity.Warning, "ZIP_UNKNOWN_METHOD",
          $"Unknown compression method: {method}", 8));
      }
      var fnLen = BitConverter.ToUInt16(header[26..]);
      var exLen = BitConverter.ToUInt16(header[28..]);
      if (30 + fnLen + exLen > fileSize) {
        issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "ZIP_HEADER_OVERFLOW",
          "Local file header extends beyond file size"));
        return new() { IsValid = false, Confidence = 0.4, Health = FormatHealth.Damaged,
          Level = ValidationLevel.Header, Issues = issues };
      }
    }
    if (fileSize < 22) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Warning, "ZIP_NO_EOCD",
        "File too short to contain end of central directory record (min 22 bytes)"));
    }
    var confidence = issues.Any(i => i.Severity == IssueSeverity.Warning) ? 0.80 : 0.90;
    var health = issues.Any(i => i.Severity >= IssueSeverity.Warning) ? FormatHealth.Degraded : FormatHealth.Good;
    return new() { IsValid = true, Confidence = confidence, Health = health,
      Level = ValidationLevel.Header, Issues = issues };
  }

  /// <summary>
  /// Validates the supplied data.
  /// </summary>
public ValidationResult ValidateStructure(Stream stream) {
    var issues = new List<ValidationIssue>();
    int entryCount;
    try {
      var (cdOffset, cdSize, cdCount, _) = ZipEndOfCentralDirectory.Read(stream);
      entryCount = cdCount;
      if (cdOffset < 0 || cdOffset > stream.Length) {
        issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "ZIP_CD_OFFSET_OOB",
          $"Central directory offset {cdOffset} is outside file bounds"));
        return new() { IsValid = false, Confidence = 0.6, Health = FormatHealth.Damaged,
          Level = ValidationLevel.Structure, Issues = issues };
      }
      if (cdOffset + cdSize > stream.Length) {
        issues.Add(new(ValidationLevel.Structure, IssueSeverity.Warning, "ZIP_CD_TRUNCATED",
          $"Central directory extends beyond file (offset={cdOffset}, size={cdSize}, fileLen={stream.Length})"));
      }
      // Walk central directory entries
      stream.Position = cdOffset;
      var reader = new BinaryReader(stream, System.Text.Encoding.Latin1, leaveOpen: true);
      var validEntries = 0;
      for (var i = 0; i < cdCount; ++i) {
        if (stream.Position + 46 > stream.Length) {
          issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "ZIP_CD_ENTRY_TRUNCATED",
            $"Central directory entry {i} truncated", stream.Position));
          break;
        }
        var entrySig = reader.ReadUInt32();
        if (entrySig != ZipConstants.CentralDirectorySignature) {
          issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "ZIP_CD_BAD_SIG",
            $"Central directory entry {i}: bad signature 0x{entrySig:X8}", stream.Position - 4));
          break;
        }
        // Skip past the fixed fields to get name/extra/comment lengths
        stream.Position += 24; // skip to fnLen (offset 46-4-24 = fields after sig)
        var fnLen = reader.ReadUInt16();
        var exLen = reader.ReadUInt16();
        var cmtLen = reader.ReadUInt16();
        stream.Position += 12; // skip diskStart(2)+internalAttr(2)+externalAttr(4)+localHeaderOffset(4)
        stream.Position += fnLen + exLen + cmtLen;
        ++validEntries;
      }
      var confidence = issues.Count == 0 ? 0.92 : 0.75;
      var health = issues.Any(i => i.Severity >= IssueSeverity.Error) ? FormatHealth.Damaged
        : issues.Any(i => i.Severity >= IssueSeverity.Warning) ? FormatHealth.Degraded : FormatHealth.Good;
      return new() { IsValid = health != FormatHealth.Damaged, Confidence = confidence, Health = health,
        Level = ValidationLevel.Structure, Issues = issues,
        ValidEntries = validEntries, TotalEntries = entryCount };
    } catch (InvalidDataException ex) {
      issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "ZIP_STRUCTURE_FAILED",
        $"Structure parse failed: {ex.Message}"));
      return new() { IsValid = false, Confidence = 0.5, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Structure, Issues = issues };
    }
  }

  /// <summary>
  /// Zeros all dead bytes in the ZIP archive: gaps between local file entries,
  /// orphan data left after <see cref="ZipModifier.RemoveFile"/>, and any
  /// padding regions not covered by the layout map.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = ZipLayoutMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  /// <summary>
  /// Validates the supplied data.
  /// </summary>
public ValidationResult ValidateIntegrity(Stream stream) {
    var issues = new List<ValidationIssue>();
    try {
      var r = new ZipReader(stream);
      var validEntries = 0;
      var totalEntries = r.Entries.Count;
      foreach (var e in r.Entries) {
        if (e.IsDirectory) { ++validEntries; continue; }
        try {
          _ = r.ExtractEntry(e);
          ++validEntries;
        } catch (Exception ex) {
          issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "ZIP_ENTRY_EXTRACT_FAILED",
            $"Entry '{e.FileName}': {ex.Message}"));
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
      issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "ZIP_INTEGRITY_FAILED",
        $"Integrity check failed: {ex.Message}"));
      return new() { IsValid = false, Confidence = 0.5, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Integrity, Issues = issues };
    }
  }
}
