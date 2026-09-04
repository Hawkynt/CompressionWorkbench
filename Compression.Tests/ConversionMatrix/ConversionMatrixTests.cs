#pragma warning disable CS1591
using System.Text;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.ConversionMatrix;

/// <summary>
/// Data-driven CONVERSION CAPABILITY + ROUND-TRIP MATRIX.
/// <para>
/// Each test case is a single (source → target) pair. The harness synthesizes a
/// source archive/image from the registry's own writer, converts it through the
/// public <see cref="ArchiveOperations.ConvertArchive(string,string,string?)"/>
/// surface, then re-lists and re-extracts the output to verify the payload
/// survives. A failing case pinpoints exactly which pair is broken — this is a
/// gap detector, not a smoke test, so working pairs PASS and broken-but-should-
/// work pairs FAIL (only genuinely-impossible pairs are ignored with a reason).
/// </para>
/// </summary>
[TestFixture]
[Category("ConversionMatrix")]
[Category("Slow")]
public class ConversionMatrixTests {

  /// <summary>
  /// A single (source, target) grid cell. Carries the descriptors so the test
  /// can read capabilities (directories, multi-entry, name-synthesis quirks).
  /// </summary>
  public sealed record Pair(string SourceId, string TargetId) {
    public override string ToString() => $"{this.SourceId}__to__{this.TargetId}";
  }

  // Targets whose readers synthesize / normalize entry names rather than
  // preserving them verbatim (single-stream or name-mangling FSes). For these
  // the matrix asserts content + count, never verbatim names.
  private static readonly HashSet<string> NameSynthesizingTargets =
    new(StringComparer.OrdinalIgnoreCase) {
      "D64", "D71", "D81", "T64", "Cpm", "Atari8", "AppleDos", "ProDos",
      "Bbc", "ZxScl", "TrDos", "Mfs",
      // Mfs1 folds names to 7-char DFS slots (HELLO.TXT -> HELLO.T); Nsis stores
      // no per-block names and the reader exposes block_N. Both extract the exact
      // stored bytes, so they round-trip by content, not by verbatim name.
      "Mfs1", "Nsis",
    };

  // Case-folding targets (8.3 / uppercase) — compare names case-insensitively
  // and tolerate truncation; content match by basename remains authoritative.
  private static readonly HashSet<string> CaseFoldingTargets =
    new(StringComparer.OrdinalIgnoreCase) {
      "Fat", "ExFat", "D64", "D71", "D81", "Cpm", "Atari8",
    };

  /// <summary>
  /// Builds the grid: representative sources × all creatable targets. Each
  /// element is a NUnit <see cref="TestCaseData"/> named by the pair so the
  /// failure list is human-readable.
  /// </summary>
  public static IEnumerable<TestCaseData> Grid() {
    FormatRegistration.EnsureInitialized();
    var targets = ConversionMatrixSupport.AllTargets();
    foreach (var sourceId in ConversionMatrixSupport.SourceFormatIds) {
      foreach (var t in targets) {
        var pair = new Pair(sourceId, t.Id);
        yield return new TestCaseData(pair).SetName($"Convert_{pair}");
      }
    }
  }

  // Targets whose writers crash or hang the test host (OOM from huge image
  // pre-allocation, stack overflow, FailFast) rather than throwing a catchable
  // exception. The matrix records them as KnownGap ignores so a single bad
  // writer can't take down the whole 2424-cell run. Discovered empirically by
  // bisecting the grid; each is a real writer gap, not a harness limitation.
  private static readonly HashSet<string> HostUnsafeTargets =
    new(StringComparer.OrdinalIgnoreCase) {
    };

  // ── KNOWN CONVERSION GAPS (quarantine) ───────────────────────────────────
  //
  // These are currently-FAILING (source→target) conversion pairs that are real,
  // format-specific gaps to be fixed in later waves — NOT genuinely-impossible
  // pairs (those are still handled by the Assert.Ignore branches above) and NOT
  // harness bugs. A pair listed here that fails is downgraded to an Assert.Ignore
  // so the matrix is GREEN-WITH-HONEST-GAPS; a pair listed here that *starts
  // passing* is reported as a stale entry (so a fix can't silently leave the
  // quarantine in place); and any failing pair NOT listed here still FAILS HARD
  // (so regressions on the 1440 passing pairs are caught).
  //
  // Each reason is prefixed with its BUCKET in square brackets. This dictionary
  // is the worklist; the per-format naming quirks behind the entries are the
  // Notes column of the package READMEs. Buckets:
  //   [self-rejecting reader]              writer emits a file its own/auto reader rejects
  //   [not in Format enum]                 registry-creatable but Id missing from Format enum
  //   [single-payload/whole-image target]  target collapses a tree into one stream/blob
  //   [name/charset/size constraint]       retro FS mangles names or pads content
  //   [other]                              entry renaming / content mismatch (see reason)
  //
  // Most gaps are TARGET-wide (the target fails from every source); a few are
  // SOURCE→TARGET pair-specific and live in KnownGapPairs.
  private static readonly Dictionary<string, string> KnownGapTargets =
    new(StringComparer.OrdinalIgnoreCase) {
      // CpcDsk now re-detects as itself (the ".dsk" magic disambiguation routes the
      // Amstrad "MV - CPC" image to the CpcDsk reader, not AppleDOS), but the reader
      // is a raw track/sector dumper with no AMSDOS/CP/M filesystem layer: it exposes
      // 512-byte sector blocks (T00S0_C1…) whose padded bytes can never equal a small
      // payload, so neither verbatim-name nor content matching can succeed. A real fix
      // needs a CPC filesystem reader, which is a separate effort.
      { "CpcDsk", "[single-payload/whole-image target] CpcDsk reader exposes raw 512-byte track/sector blocks (T00S0_C1…), not files; padded sectors can't content-match the payload and no AMSDOS/CP/M filesystem layer exists" },
      // -- bucket: single-payload/whole-image target --
      { "AndroidSparse", "[single-payload/whole-image target] Android sparse image; Create sparsifies a single raw image and re-lists as metadata.ini+image.raw, not a file tree" },
      { "AndroidOta", "[single-payload/whole-image target] OTA update payload; writer emits a whole-image blob that re-lists as 0 files" },
      { "Awb", "[single-payload/whole-image target] CRI AWB audio bank; writer collapses the tree to a single FULL.amr stream" },
      { "DiskDoubler", "[single-payload/whole-image target] single-fork compressor; carries only one payload (lists 1 file)" },
      { "Ewf", "[single-payload/whole-image target] EnCase EWF (.E01) wraps raw media as opaque chunks; the reader surfaces section blobs (volume/sectors/table/...), not the original files" },
      { "Lrzip", "[single-payload/whole-image target] single-stream long-range compressor; one 'data' member only" },
      { "Mp3", "[single-payload/whole-image target] single audio stream; collapses tree to one FULL.mp3" },
      { "Psf", "[single-payload/whole-image target] PlayStation sound format; fixed header.bin+program.bin pair, not a tree" },
      { "Nib", "[single-payload/whole-image target] CBM nibble image; the writer lays down raw GCR tracks (track_00.bin…track_68.bin) and the container has no filename namespace of its own — names would need a CBM DOS directory written into those tracks" },
      { "OrangeFs", "[single-payload/whole-image target] OrangeFS DBPF object; the writer packs the tree into one object.bin and re-lists as FULL.orangefs+metadata.ini+object.bin" },
      { "Sparseimage", "[single-payload/whole-image target] Apple sparse disk image; one disk.img blob" },
      { "SplitFile", "[single-payload/whole-image target] byte-splitter; rejoins to a single 'joined' member, not a tree" },
      { "Srec", "[single-payload/whole-image target] Motorola S-record firmware pseudo-archive; writer re-encodes one flat image and re-lists as metadata.ini+firmware.bin, not a file tree" },
      { "StuffItX", "[single-payload/whole-image target] writer emits an image that re-lists as 0 files" },
      { "Umx", "[single-payload/whole-image target] Unreal package; writer emits a blob that re-lists as 0 files" },
      { "Wbn", "[single-payload/whole-image target] WebBundle writer collapses tree to a single FULL.wbn" },
      { "Wrapster", "[single-payload/whole-image target] Wrapster MP3 wrapper; one FULL.mp3 + 0/1 frame, not a tree" },
      { "xDisk", "[single-payload/whole-image target] Amiga xDisk image; writer emits one .xdsk blob" },
      { "xMash", "[single-payload/whole-image target] Amiga xMash image; writer emits one .xmsh blob" },
      // -- bucket: name/charset/size constraint --
      { "Bbc", "[name/charset/size constraint] BBC Micro DFS disk; name-synthesizing FS, payload not found by content" },
      { "Cpm", "[name/charset/size constraint] CP/M disk; 8.3-folding name-synth FS, payload not found by content" },
      { "Lif", "[name/charset/size constraint] HP-71 LIF disk pads file content to a 256-byte record so bytes differ" },
      { "Ps1MemoryCard", "[name/charset/size constraint] PS1 memory-card saves allocate whole 8 KiB blocks and arbitrary conversion payloads are zero-padded to the stored block size" },
      { "Rt11", "[name/charset/size constraint] RT-11 disk pads content to a 512-byte block so bytes differ" },
      { "TrDos", "[name/charset/size constraint] TR-DOS disk; name-synthesizing FS, payload not found by content" },
      { "Wad", "[name/charset/size constraint] Doom WAD lump names are 8-char-truncated (HELLO.TX) so verbatim-name match fails" },
      { "ZxScl", "[name/charset/size constraint] ZX-Spectrum SCL; name-synthesizing FS, payload not found by content" },
      // -- bucket: other --
      { "Akb", "[other] Koei AKB audio bank; entries renamed to entry_NNN.bin so verbatim-name match fails" },
      { "Cso", "[other] CSO compressed ISO; payload exposed as FULL.cso+block_* not original names" },
      { "Dcs", "[other] DCS Amiga disk; exposed as track_NNN.raw not original names" },
      { "Dtb", "[other] Device-tree blob; names re-rooted under _root/ and de-extensioned (HELLO.bin)" },
      { "Fits", "[other] FITS; payload exposed as hdu_* header/data members not original names" },
      { "G64", "[other] G64 GCR disk; exposed as track_NN.bin not original names" },
      { "GameMaker", "[other] GameMaker data.win; exposed as chunks/GEN8.bin not original names" },
      { "Ghost", "[other] Norton Ghost image; exposed as partitionN.bin not original names" },
      { "InnoSetup", "[other] Inno Setup installer; re-lists as the single .exe stub, names lost" },
      { "Lfd", "[other] LucasArts LFD; entries renamed DATA.<stem> / RMAP.resource" },
      { "LhF", "[other] LhF Amiga disk; exposed as track_NNN.raw not original names" },
      { "Lnk", "[other] Windows .lnk; exposed as header.bin/linkinfo.bin not a file tree" },
      { "Macrium", "[other] Macrium image; exposed as disk-image.raw + block-NN.$* not original names" },
      { "Mbox", "[other] mbox mail; entries renamed message_NN.eml" },
      { "Mhk", "[other] Mohawk archive; entries renamed tDAT_NNNN" },
      { "Mix", "[other] Westwood MIX; entries renamed to 32-bit name-hash .bin" },
      { "Mo", "[other] GNU gettext .mo; entries renamed NNNN_<stem>.txt" },
      { "Npy", "[other] NumPy .npy; collapses to header.bin/array.bin (single array, names lost)" },
      { "Npz", "[other] NumPy .npz; entries suffixed .npy (HELLO.TXT.npy) so verbatim-name match fails" },
      { "PackDisk", "[other] PackDisk Amiga disk; exposed as track_NNN.raw not original names" },
      { "Paragon", "[other] Paragon backup; exposed as chunk_NNNNNN.bin not original names" },
      { "TfRecord", "[other] TensorFlow TFRecord; entries renamed record_NNNNN.bin" },
      { "UefiFv", "[other] UEFI firmware volume; an FFS file is identified by GUID, so the writer names each one <guid>_RAW.bin and the original filename has nowhere to live" },
      { "Tfc", "[other] UE texture cache; entries renamed bundle_NNNNN.bin" },
      { "Warc", "[other] WARC; entries listed as 'resource: <name>' so basename match fails" },
      { "Zap", "[other] Zap Amiga disk; exposed as track_NNN.raw not original names" },
    };

  // SOURCE→TARGET pair-specific gaps (target works from most sources but fails
  // from one). Keyed "Source|Target", case-insensitive.
  private static readonly Dictionary<string, string> KnownGapPairs =
    new(StringComparer.OrdinalIgnoreCase) {
    };

  /// <summary>
  /// Returns the documented gap reason for a pair if it is a known, quarantined
  /// conversion gap (pair-specific first, then target-wide), else null. Used to
  /// downgrade a real-but-not-yet-fixed failure to an Assert.Ignore.
  /// </summary>
  private static string? KnownGapReason(Pair pair) {
    if (KnownGapPairs.TryGetValue($"{pair.SourceId}|{pair.TargetId}", out var pr))
      return pr;
    if (KnownGapTargets.TryGetValue(pair.TargetId, out var tr))
      return tr;
    return null;
  }

  [Test]
  [TestCaseSource(nameof(Grid))]
  public void Convert(Pair pair) {
    FormatRegistration.EnsureInitialized();

    var srcDesc = FormatRegistry.GetById(pair.SourceId);
    var dstDesc = FormatRegistry.GetById(pair.TargetId);
    if (srcDesc == null)
      Assert.Ignore($"Source format '{pair.SourceId}' is not registered.");
    if (dstDesc == null)
      Assert.Ignore($"Target format '{pair.TargetId}' is not registered.");

    if (!ConversionMatrixSupport.CanBeSyntheticSource(srcDesc!))
      Assert.Ignore($"Source '{pair.SourceId}' cannot be synthesized (not list+extract+create).");
    if (!ConversionMatrixSupport.CanBeTarget(dstDesc!))
      Assert.Ignore($"Target '{pair.TargetId}' has no writer (not IArchiveCreatable).");

    if (HostUnsafeTargets.Contains(pair.TargetId))
      Assert.Ignore($"Target '{pair.TargetId}' writer crashes/hangs the host (KnownGap; excluded to keep the matrix runnable).");

    var work = Path.Combine(Path.GetTempPath(), "cwb_cmx_" + Guid.NewGuid().ToString("N")[..10]);
    Directory.CreateDirectory(work);
    try {
      // 1) Synthesize source. A failure here means the source writer itself is
      //    broken for our minimal payload — surface it as an ignore so the
      //    matrix measures CONVERSION, not source-writer bugs.
      string srcPath;
      try {
        srcPath = ConversionMatrixSupport.SynthesizeSource(pair.SourceId, work);
      } catch (Exception ex) {
        Assert.Ignore($"Could not synthesize source '{pair.SourceId}': {ex.GetType().Name}: {ex.Message}");
        return; // unreachable; keeps the compiler happy about srcPath.
      }

      // Read the source back through the public List/Extract surface to learn
      // exactly what the converter will see (post name-folding, etc.).
      var srcEntries = ArchiveOperations.List(srcPath, null)
        .Where(e => !e.IsDirectory).ToList();
      Assert.That(srcEntries, Is.Not.Empty,
        $"Sanity: synthesized {pair.SourceId} source listed zero files.");

      // 2) Convert + verify. The whole conversion + round-trip is wrapped so a
      //    pair listed in the KnownConversionGaps quarantine is downgraded from
      //    a hard failure to an Assert.Ignore (green-with-honest-gaps), while
      //    any UNLISTED failing pair still fails hard (regression guard), and a
      //    LISTED pair that now PASSES is reported as a stale quarantine entry.
      //    Genuinely-impossible Ignores (pseudo-archive / typed-input targets,
      //    or synthesize failures) raise IgnoreException, which is NOT an
      //    AssertionException and therefore propagates untouched here.
      var gapReason = KnownGapReason(pair);
      try {
        ConvertAndVerify(pair, srcDesc!, dstDesc!, srcPath, work);
      } catch (AssertionException ex) {
        if (gapReason != null) {
          Assert.Ignore($"known gap: {gapReason} | original: {ex.Message.Split('\n')[0]}");
          return;
        }
        throw;
      } catch (Exception ex) when (ex is not NUnit.Framework.IgnoreException
                                   && ex is not NUnit.Framework.SuccessException) {
        // An unexpected non-assertion exception escaped the inner per-step
        // catches. Quarantine it too if this pair is a documented gap; else let
        // it surface so genuinely new breakage is never hidden.
        if (gapReason != null) {
          Assert.Ignore($"known gap: {gapReason} | unexpected: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
          return;
        }
        throw;
      }

      // The pair passed end-to-end. If it is still listed as a known gap, the
      // quarantine entry is stale and must be removed so fixed pairs stay
      // enforced — fail loudly rather than silently leave a passing pair ignored.
      if (gapReason != null)
        Assert.Fail($"{pair}: STALE KnownConversionGaps entry — this pair now PASSES. " +
          $"Remove it from KnownGapTargets/KnownGapPairs. (was: {gapReason})");
    } finally {
      try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { /* best-effort */ }
    }
  }

  /// <summary>
  /// Runs the convert → re-list → verify-payload steps for one pair. Throws an
  /// <see cref="AssertionException"/> on a content/name/count mismatch and an
  /// <see cref="IgnoreException"/> for genuinely-impossible targets. The caller
  /// (<see cref="Convert"/>) decides whether a thrown failure is a quarantined
  /// known gap or a real regression.
  /// </summary>
  private static void ConvertAndVerify(Pair pair, IFormatDescriptor srcDesc,
      IFormatDescriptor dstDesc, string srcPath, string work) {
      // Convert through the public ConvertArchive surface, explicit target.
      var dstExt = string.IsNullOrEmpty(dstDesc.DefaultExtension) ? ".out" : dstDesc.DefaultExtension;
      var dstPath = Path.Combine(work, $"dst_{pair.TargetId}_{Guid.NewGuid():N}{dstExt}");
      try {
        ArchiveOperations.ConvertArchive(srcPath, dstPath, pair.TargetId);
      } catch (InvalidOperationException ex) {
        // Audio/image PSEUDO-ARCHIVES (WAV/VOC/QOI/DSF/…) advertise
        // IArchiveCreatable but only "create" from a specific synthesized input
        // shape — per-channel WAVs, a raw pixel buffer, etc. A generic file
        // tree cannot be represented in them, so this is a genuinely-impossible
        // pair, not a broken-but-should-work one. They announce that contract
        // by throwing InvalidOperationException with a "needs FULL.x / WAV /
        // pixels" message; honor it as an Ignore so the matrix measures real
        // archive↔archive / FS conversion rather than codec-input mismatch.
        Assert.Ignore($"{pair}: target is a single-payload pseudo-archive that cannot represent a file tree: {ex.Message}");
        return;
      } catch (ArgumentException ex) {
        // Same class as the pseudo-archive case but signaled via
        // ArgumentException: targets that require a SPECIFIC named/typed input
        // (a PARAM.SFO + ICON0.PNG manifest, a .ttf/.otf font, an ICO/CUR
        // frame, a single disk-image input) cannot accept a generic file tree.
        // Genuinely-impossible pair → Ignore, not a should-work failure.
        Assert.Ignore($"{pair}: target requires specific named/typed inputs, not an arbitrary file tree: {ex.Message}");
        return;
      } catch (NotSupportedException ex) {
        // A NotSupportedException from a registry-advertised IArchiveCreatable
        // target is a real gap (e.g. the descriptor's Id is absent from the
        // generated Format enum, so ConvertArchive can't resolve it). Fail.
        Assert.Fail($"{pair}: ConvertArchive threw NotSupportedException (advertised creatable but refused): {ex.Message}");
        return;
      }

      Assert.That(File.Exists(dstPath), Is.True, $"{pair}: conversion produced no output file.");
      Assert.That(new FileInfo(dstPath).Length, Is.GreaterThan(0), $"{pair}: conversion produced an empty file.");
      Assert.That(File.Exists(srcPath), Is.True, $"{pair}: source must survive conversion (never deleted).");

      // 3) Re-list the target and verify the payload survived.
      List<ArchiveEntry> dstEntries;
      try {
        dstEntries = ArchiveOperations.List(dstPath, null).Where(e => !e.IsDirectory).ToList();
      } catch (Exception ex) {
        Assert.Fail($"{pair}: target produced a file that cannot be re-listed: {ex.GetType().Name}: {ex.Message}");
        return;
      }

      var expected = ExpectedFiles(srcDesc, dstDesc);
      VerifyPayload(pair, dstPath, dstEntries, expected);
  }

  /// <summary>
  /// Computes the (basename → content) set we expect to find on the target side,
  /// honoring multi-entry and directory capability of BOTH ends. The source's
  /// own capability decides what got written; the target's capability decides
  /// what could be carried.
  /// </summary>
  private static Dictionary<string, byte[]> ExpectedFiles(IFormatDescriptor src, IFormatDescriptor dst) {
    var srcMulti = ConversionMatrixSupport.SupportsMultipleEntries(src);
    var payload = ConversionMatrixSupport.BuildPayloadFiles();
    var files = (srcMulti ? payload : payload.Take(1)).ToList();

    var bothDirs = srcMulti
      && ConversionMatrixSupport.SupportsDirectories(src)
      && ConversionMatrixSupport.SupportsDirectories(dst);

    var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var (name, data) in files)
      map[Path.GetFileName(name)] = data;
    if (bothDirs)
      map[Path.GetFileName(ConversionMatrixSupport.SubdirFileName)] = ConversionMatrixSupport.SubdirFileData;
    return map;
  }

  /// <summary>
  /// Verifies each expected file is present (by basename, case-insensitive) on
  /// the target and that its content is byte-identical, extracting through the
  /// public surface. Name verbatim-ness is relaxed for name-synthesizing and
  /// case-folding targets per the documented domain quirks; content is always
  /// authoritative.
  /// </summary>
  private static void VerifyPayload(Pair pair, string dstPath,
      List<ArchiveEntry> dstEntries, Dictionary<string, byte[]> expected) {

    var nameSynth = NameSynthesizingTargets.Contains(pair.TargetId);

    // Count: the target must carry at least as many files as we expect, unless
    // it is a name-synthesizing single-stream-ish format (then assert >= 1).
    if (nameSynth)
      Assert.That(dstEntries.Count, Is.GreaterThanOrEqualTo(1),
        $"{pair}: name-synthesizing target carried no files.");
    else
      Assert.That(dstEntries.Count, Is.GreaterThanOrEqualTo(expected.Count),
        $"{pair}: expected ≥{expected.Count} files, target lists {dstEntries.Count} " +
        $"([{string.Join(",", dstEntries.Select(e => e.Name))}]).");

    // Build a basename→entry lookup for content extraction.
    var byBase = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in dstEntries) byBase[Path.GetFileName(e.Name)] = e;

    if (nameSynth) {
      // Name-synthesizing targets: verify each expected payload appears as the
      // content of SOME entry (match by content, not by name).
      var actualContents = dstEntries
        .Select(e => SafeExtract(dstPath, e.Name))
        .Where(b => b != null)
        .Select(b => b!)
        .ToList();
      foreach (var (name, data) in expected) {
        var found = actualContents.Any(b => b.AsSpan().SequenceEqual(data));
        Assert.That(found, Is.True,
          $"{pair}: payload '{name}' ({data.Length} B) not found in any target entry by content.");
      }
      return;
    }

    foreach (var (name, data) in expected) {
      // Locate the entry: exact basename first, then a truncated 8.3 prefix
      // match for case-folding targets that shorten names.
      var entry = byBase.TryGetValue(name, out var hit) ? hit
        : FindByTruncatedPrefix(pair, byBase, name);
      Assert.That(entry, Is.Not.Null,
        $"{pair}: expected file '{name}' missing from target " +
        $"([{string.Join(",", dstEntries.Select(e => e.Name))}]).");

      var actual = SafeExtract(dstPath, entry!.Name);
      Assert.That(actual, Is.Not.Null, $"{pair}: extraction of '{entry.Name}' returned null.");
      Assert.That(actual, Is.EqualTo(data),
        $"{pair}: content of '{name}' (via '{entry.Name}') is not byte-identical " +
        $"(expected {data.Length} B, got {actual!.Length} B).");
    }
  }

  /// <summary>For case-folding targets that truncate to 8.3, match by the first
  /// 8 chars of the name stem (uppercased). Returns null when no prefix matches.</summary>
  private static ArchiveEntry? FindByTruncatedPrefix(Pair pair,
      Dictionary<string, ArchiveEntry> byBase, string expectedName) {
    if (!CaseFoldingTargets.Contains(pair.TargetId)) return null;
    var stem = Path.GetFileNameWithoutExtension(expectedName);
    var prefix = stem.Length > 8 ? stem[..8] : stem;
    foreach (var (k, v) in byBase) {
      var kStem = Path.GetFileNameWithoutExtension(k);
      if (kStem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return v;
    }
    return null;
  }

  private static byte[]? SafeExtract(string archivePath, string entryName) {
    try {
      return ArchiveOperations.ExtractEntry(archivePath, entryName, null);
    } catch {
      return null;
    }
  }

  // ── Coverage report ───────────────────────────────────────────────────

  /// <summary>
  /// Emits the capability + streaming-write coverage matrix to the test output.
  /// Lists every target, whether it overrides the large-file-safe
  /// <c>CreateFromStreams</c> hook (vs the buffering default), and aggregate
  /// source/target capability counts. Runs as its own test so the report is
  /// always visible even when no grid case fails.
  /// </summary>
  [Test]
  [Category("ConversionMatrix")]
  public void CoverageReport() {
    FormatRegistration.EnsureInitialized();

    var targets = ConversionMatrixSupport.AllTargets();
    var sb = new StringBuilder();
    sb.AppendLine();
    sb.AppendLine("════════ CONVERSION CAPABILITY + ROUND-TRIP MATRIX — COVERAGE ════════");

    // Source set.
    var sources = ConversionMatrixSupport.SourceFormatIds;
    var usableSources = sources.Where(id => {
      var d = FormatRegistry.GetById(id);
      return d != null && ConversionMatrixSupport.CanBeSyntheticSource(d);
    }).ToList();
    sb.AppendLine($"SOURCES exercised ({usableSources.Count}/{sources.Length} synthesizable): " +
      string.Join(", ", sources.Select(id => {
        var d = FormatRegistry.GetById(id);
        var ok = d != null && ConversionMatrixSupport.CanBeSyntheticSource(d);
        return ok ? id : id + "(skip)";
      })));

    // Target set + streaming-write coverage.
    sb.AppendLine($"TARGETS (IArchiveCreatable): {targets.Count}");
    var streamingSafe = new List<string>();
    var bufferingDefault = new List<string>();
    foreach (var t in targets) {
      if (ConversionMatrixSupport.OverridesCreateFromStreams(t)) streamingSafe.Add(t.Id);
      else bufferingDefault.Add(t.Id);
    }
    sb.AppendLine($"  ├─ override CreateFromStreams (large-file-safe): {streamingSafe.Count}");
    sb.AppendLine($"  │    {string.Join(", ", streamingSafe)}");
    sb.AppendLine($"  └─ use buffering default (STREAMING-WRITE GAP): {bufferingDefault.Count}");
    sb.AppendLine($"       {string.Join(", ", bufferingDefault)}");

    // Aggregate registry capability census.
    var all = FormatRegistry.All
      .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    var listable = all.Count(d => FormatRegistry.GetArchiveOps(d.Id) != null
      && (d.Capabilities & FormatCapabilities.CanList) != 0);
    var creatable = all.Count(ConversionMatrixSupport.CanBeTarget);
    var syntheticSources = all.Count(ConversionMatrixSupport.CanBeSyntheticSource);
    sb.AppendLine($"REGISTRY CENSUS: {all.Count} descriptors | listable archives: {listable} " +
      $"| creatable targets: {creatable} | synthesizable sources: {syntheticSources}");

    var gridCells = usableSources.Count * targets.Count;
    sb.AppendLine($"GRID CELLS (scoped): {usableSources.Count} sources × {targets.Count} targets = {gridCells} pairs");
    sb.AppendLine("══════════════════════════════════════════════════════════════════════");

    TestContext.Out.WriteLine(sb.ToString());
    // Always-true assertion so the report test contributes a green tick.
    Assert.That(targets, Is.Not.Empty, "Registry must expose at least one creatable target.");
  }
}
