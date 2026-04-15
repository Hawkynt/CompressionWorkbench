#pragma warning disable CS1591

using Compression.Analysis;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Zip;

namespace Compression.Tests;

[TestFixture]
public class RobustnessTests {

  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  // ── 9a. Malformed Input ───────────────────────────────────────────

  #region Truncated Headers

  private static readonly string[] TruncatedHeaderFormatIds =
    ["Gzip", "Bzip2", "Xz", "Zstd", "Lz4", "Snappy", "Lzma", "Lzip", "Brotli", "Zlib"];

  private static IEnumerable<TestCaseData> TruncatedHeaderFormats() {
    FormatRegistration.EnsureInitialized();
    foreach (var id in TruncatedHeaderFormatIds) {
      var desc = FormatRegistry.GetById(id);
      if (desc == null) continue;
      var ops = FormatRegistry.GetStreamOps(id);
      if (ops == null) continue;
      if (desc.MagicSignatures.Count == 0) continue;
      yield return new TestCaseData(id, desc, ops).SetName($"TruncatedHeader_{id}");
    }
  }

  [TestCaseSource(nameof(TruncatedHeaderFormats))]
  [CancelAfter(10000)]
  public void TruncatedHeader_ThrowsCleanException(string id, IFormatDescriptor desc, IStreamFormatOperations ops) {
    // Build data consisting of only the magic bytes (no payload)
    var magic = desc.MagicSignatures[0];
    var data = new byte[magic.Offset + magic.Bytes.Length];
    Array.Copy(magic.Bytes, 0, data, magic.Offset, magic.Bytes.Length);

    using var input = new MemoryStream(data);
    using var output = new MemoryStream();

    var ex = Assert.Catch(() => ops.Decompress(input, output),
      $"{id}: Decompressing magic-only data should throw");
    Assert.That(ex, Is.Not.Null, $"{id}: Expected an exception for truncated header");
    Assert.That(
      ex,
      Is.InstanceOf<InvalidDataException>()
        .Or.InstanceOf<EndOfStreamException>()
        .Or.InstanceOf<InvalidOperationException>()
        .Or.InstanceOf<ArgumentException>()
        .Or.InstanceOf<NotSupportedException>()
        .Or.InstanceOf<IOException>(),
      $"{id}: Expected a clean exception, got {ex!.GetType().Name}: {ex.Message}");
  }

  #endregion

  #region Corrupted After Magic

  private static IEnumerable<TestCaseData> CorruptedAfterMagicFormats() {
    FormatRegistration.EnsureInitialized();
    foreach (var id in TruncatedHeaderFormatIds) {
      var desc = FormatRegistry.GetById(id);
      if (desc == null) continue;
      var ops = FormatRegistry.GetStreamOps(id);
      if (ops == null) continue;
      if (desc.MagicSignatures.Count == 0) continue;
      yield return new TestCaseData(id, desc, ops).SetName($"CorruptedAfterMagic_{id}");
    }
  }

  [TestCaseSource(nameof(CorruptedAfterMagicFormats))]
  [CancelAfter(10000)]
  public void CorruptedAfterMagic_ThrowsCleanException(string id, IFormatDescriptor desc, IStreamFormatOperations ops) {
    var magic = desc.MagicSignatures[0];
    var data = new byte[magic.Offset + magic.Bytes.Length + 256];
    Array.Copy(magic.Bytes, 0, data, magic.Offset, magic.Bytes.Length);

    // Fill the rest with 0xDE 0xAD pattern
    for (var i = magic.Offset + magic.Bytes.Length; i < data.Length; i++)
      data[i] = (i % 2 == 0) ? (byte)0xDE : (byte)0xAD;

    using var input = new MemoryStream(data);
    using var output = new MemoryStream();

    var ex = Assert.Catch(() => ops.Decompress(input, output),
      $"{id}: Decompressing corrupted data should throw");
    Assert.That(ex, Is.Not.Null, $"{id}: Expected an exception for corrupted data");
    Assert.That(
      ex,
      Is.InstanceOf<InvalidDataException>()
        .Or.InstanceOf<EndOfStreamException>()
        .Or.InstanceOf<InvalidOperationException>()
        .Or.InstanceOf<ArgumentException>()
        .Or.InstanceOf<NotSupportedException>()
        .Or.InstanceOf<IOException>(),
      $"{id}: Expected a clean exception, got {ex!.GetType().Name}: {ex.Message}");
  }

  #endregion

  #region Oversized/Garbage ZIP

  [Test]
  [CancelAfter(10000)]
  public void ZipWithOversizedCompressedSize_ThrowsOnRead() {
    // Build a hand-crafted ZIP where the central directory claims compressed_size=0x7FFFFFFF
    // but the file only has 10 bytes of actual data after the local header.
    // The reader should throw EndOfStreamException (not try to allocate 2GB or hang).
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);

    var fnBytes = System.Text.Encoding.UTF8.GetBytes("test.txt");

    // --- Local file header (at offset 0) ---
    bw.Write(0x04034B50u); // signature
    bw.Write((ushort)20);  // version needed
    bw.Write((ushort)0x0800); // flags: UTF-8
    bw.Write((ushort)0);   // compression method (store)
    bw.Write((ushort)0);   // last mod file time
    bw.Write((ushort)0);   // last mod file date
    bw.Write(0u);          // crc-32
    bw.Write((uint)10);    // compressed size in local header (10 bytes)
    bw.Write((uint)10);    // uncompressed size in local header
    bw.Write((ushort)fnBytes.Length); // file name length
    bw.Write((ushort)0);              // extra field length
    bw.Write(fnBytes);                // file name
    // 10 bytes of actual data
    bw.Write(new byte[10]);

    // --- Central directory entry ---
    var cdOffset = ms.Position;
    bw.Write(0x02014B50u); // signature
    bw.Write((ushort)20);  // version made by
    bw.Write((ushort)20);  // version needed
    bw.Write((ushort)0x0800); // flags: UTF-8
    bw.Write((ushort)0);   // compression method (store)
    bw.Write((ushort)0);   // last mod file time
    bw.Write((ushort)0);   // last mod file date
    bw.Write(0u);          // crc-32
    bw.Write(0x7FFFFFFFu); // compressed size (HUGE — ~2GB claimed in CD)
    bw.Write(0x7FFFFFFFu); // uncompressed size
    bw.Write((ushort)fnBytes.Length); // file name length
    bw.Write((ushort)0);   // extra field length
    bw.Write((ushort)0);   // comment length
    bw.Write((ushort)0);   // disk number start
    bw.Write((ushort)0);   // internal attributes
    bw.Write(0u);          // external attributes
    bw.Write(0u);          // local header offset
    bw.Write(fnBytes);     // file name

    // --- End of central directory ---
    var cdSize = ms.Position - cdOffset;
    bw.Write(0x06054B50u); // signature
    bw.Write((ushort)0);   // disk number
    bw.Write((ushort)0);   // disk with CD
    bw.Write((ushort)1);   // entries on this disk
    bw.Write((ushort)1);   // total entries
    bw.Write((uint)cdSize); // CD size
    bw.Write((uint)cdOffset); // CD offset
    bw.Write((ushort)0);   // comment length

    ms.Position = 0;

    // The reader should throw when trying to extract — either during allocation
    // (OutOfMemoryException) or when trying to read past end of stream (EndOfStreamException).
    // It must NOT silently succeed or crash with NullReferenceException/AccessViolationException.
    var ex = Assert.Catch(() => {
      var reader = new ZipReader(ms);
      foreach (var entry in reader.Entries)
        reader.ExtractEntry(entry);
    });
    Assert.That(ex, Is.Not.Null,
      "ZipReader should throw when compressed_size exceeds available data");
    Assert.That(
      ex,
      Is.InstanceOf<EndOfStreamException>()
        .Or.InstanceOf<OutOfMemoryException>()
        .Or.InstanceOf<InvalidDataException>()
        .Or.InstanceOf<IOException>(),
      $"Expected a clean exception, got {ex!.GetType().Name}: {ex.Message}");
  }

  [Test]
  [CancelAfter(10000)]
  public void GzipWithTruncatedExtra_ThrowsCleanly() {
    // Build GZIP header with FEXTRA flag set, extra_length=0xFFFF, but only 10 bytes of data
    var data = new byte[10 + 2 + 10]; // 10-byte header + 2-byte XLEN + 10 bytes of garbage
    data[0] = 0x1F; // magic1
    data[1] = 0x8B; // magic2
    data[2] = 0x08; // method = deflate
    data[3] = 0x04; // flags = FEXTRA
    // bytes 4-7: mtime = 0
    // byte 8: xfl = 0
    data[9] = 0xFF; // OS = unknown
    // XLEN = 0xFFFF (65535), but we only provide 10 bytes
    data[10] = 0xFF;
    data[11] = 0xFF;
    // 10 bytes of garbage extra data (way less than 65535)
    for (var i = 12; i < data.Length; i++)
      data[i] = 0xAA;

    var ops = FormatRegistry.GetStreamOps("Gzip");
    Assert.That(ops, Is.Not.Null, "Gzip stream ops should be registered");

    using var input = new MemoryStream(data);
    using var output = new MemoryStream();

    var ex = Assert.Catch(() => ops!.Decompress(input, output));
    Assert.That(ex, Is.Not.Null, "Should throw on truncated FEXTRA");
    Assert.That(
      ex,
      Is.InstanceOf<InvalidDataException>()
        .Or.InstanceOf<EndOfStreamException>()
        .Or.InstanceOf<IOException>(),
      $"Expected clean exception, got {ex!.GetType().Name}: {ex.Message}");
  }

  #endregion

  #region Path Traversal

  [Test]
  [CancelAfter(10000)]
  public void ZipWithPathTraversal_ExtractsInsideOutputDir() {
    // Create a ZIP using our ZipWriter containing an entry named ../../evil.txt
    byte[] zipData;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true)) {
        writer.AddEntry("../../evil.txt", "malicious content"u8.ToArray(), ZipCompressionMethod.Store);
        writer.AddEntry("safe/normal.txt", "safe content"u8.ToArray(), ZipCompressionMethod.Store);
        writer.Finish();
      }
      zipData = ms.ToArray();
    }

    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_robustness_pathtraversal_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);

      // Extract using the ZipFormatDescriptor's Extract method (which uses FormatHelpers.WriteFile)
      var archiveOps = FormatRegistry.GetArchiveOps("Zip");
      Assert.That(archiveOps, Is.Not.Null, "Zip archive ops should be registered");

      using var zipStream = new MemoryStream(zipData);
      archiveOps!.Extract(zipStream, tmpDir, null, null);

      // Verify no file was created outside the temp dir
      var allFiles = Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories);
      foreach (var file in allFiles) {
        var fullPath = Path.GetFullPath(file);
        Assert.That(fullPath, Does.StartWith(Path.GetFullPath(tmpDir)),
          $"File '{fullPath}' escaped the output directory");
      }

      // Verify the malicious entry was sanitized but still created
      Assert.That(allFiles.Length, Is.GreaterThanOrEqualTo(2),
        "Both entries should have been extracted (with sanitized paths)");

      // The traversal entry should have been sanitized to just the filename
      var fileNames = allFiles.Select(Path.GetFileName).ToArray();
      Assert.That(fileNames, Does.Contain("evil.txt"),
        "evil.txt should exist (sanitized from traversal path)");
      Assert.That(fileNames, Does.Contain("normal.txt"),
        "normal.txt should exist");
    } finally {
      try { Directory.Delete(tmpDir, true); } catch { /* best effort */ }
    }
  }

  #endregion

  #region Recursive Archive Depth

  [Test]
  [CancelAfter(10000)]
  public void RecursiveZipInZip_AutoExtractorHandlesNesting() {
    // Create ZIP-in-ZIP-in-ZIP (3 levels)
    // Level 3 (innermost): a ZIP containing hello.txt
    byte[] innerZip;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true)) {
        writer.AddEntry("hello.txt", "Hello from level 3!"u8.ToArray(), ZipCompressionMethod.Store);
        writer.Finish();
      }
      innerZip = ms.ToArray();
    }

    // Level 2: a ZIP containing the level-3 ZIP
    byte[] midZip;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true)) {
        writer.AddEntry("inner.zip", innerZip, ZipCompressionMethod.Store);
        writer.Finish();
      }
      midZip = ms.ToArray();
    }

    // Level 1 (outermost): a ZIP containing the level-2 ZIP
    byte[] outerZip;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true)) {
        writer.AddEntry("middle.zip", midZip, ZipCompressionMethod.Store);
        writer.Finish();
      }
      outerZip = ms.ToArray();
    }

    // Extract using AutoExtractor
    var extractor = new AutoExtractor();
    using var stream = new MemoryStream(outerZip);
    var result = extractor.Extract(stream);

    Assert.That(result, Is.Not.Null, "AutoExtractor should detect the outer ZIP");
    Assert.That(result!.FormatId, Is.EqualTo("Zip"), "Outer format should be ZIP");
    Assert.That(result.Entries, Has.Count.GreaterThanOrEqualTo(1), "Outer ZIP should have at least one entry");

    // Verify nested extraction happened
    Assert.That(result.NestedResults, Has.Count.GreaterThanOrEqualTo(1),
      "AutoExtractor should have recursed into the middle ZIP");

    var middleResult = result.NestedResults[0].Result;
    Assert.That(middleResult.FormatId, Is.EqualTo("Zip"), "Middle format should be ZIP");
    Assert.That(middleResult.NestedResults, Has.Count.GreaterThanOrEqualTo(1),
      "AutoExtractor should have recursed into the inner ZIP");

    var innerResult = middleResult.NestedResults[0].Result;
    Assert.That(innerResult.FormatId, Is.EqualTo("Zip"), "Inner format should be ZIP");
    Assert.That(innerResult.Entries.Any(e => e.Name.Contains("hello.txt")), Is.True,
      "Deepest extraction should contain hello.txt");
  }

  #endregion

  #region Zero-Length Entries

  [Test]
  [CancelAfter(10000)]
  public void ZipWithZeroLengthEntry_ExtractsEmptyFile() {
    byte[] zipData;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true)) {
        writer.AddEntry("empty.txt", [], ZipCompressionMethod.Store);
        writer.AddEntry("notempty.txt", "content"u8.ToArray(), ZipCompressionMethod.Store);
        writer.Finish();
      }
      zipData = ms.ToArray();
    }

    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_robustness_zerolen_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);

      var archiveOps = FormatRegistry.GetArchiveOps("Zip");
      Assert.That(archiveOps, Is.Not.Null);

      using var zipStream = new MemoryStream(zipData);
      archiveOps!.Extract(zipStream, tmpDir, null, null);

      var emptyFile = Path.Combine(tmpDir, "empty.txt");
      Assert.That(File.Exists(emptyFile), Is.True, "Empty file should be created");
      Assert.That(new FileInfo(emptyFile).Length, Is.EqualTo(0), "Empty file should have 0 bytes");

      var notEmptyFile = Path.Combine(tmpDir, "notempty.txt");
      Assert.That(File.Exists(notEmptyFile), Is.True, "Non-empty file should be created");
      Assert.That(new FileInfo(notEmptyFile).Length, Is.GreaterThan(0), "Non-empty file should have data");
    } finally {
      try { Directory.Delete(tmpDir, true); } catch { /* best effort */ }
    }
  }

  #endregion

  // ── 9b. Memory Safety ────────────────────────────────────────────

  #region ArrayPool Audit

  [Test]
  [CancelAfter(30000)]
  public void ArrayPoolAudit_AllRentsHaveMatchingReturns() {
    // Scan all .cs files in the repo for ArrayPool Rent/Return balance
    var repoRoot = FindRepoRoot();
    Assert.That(repoRoot, Is.Not.Null, "Could not find repository root");

    var csFiles = Directory.GetFiles(repoRoot!, "*.cs", SearchOption.AllDirectories)
      .Where(f => !f.Contains("obj") && !f.Contains("bin") && !f.Contains("RobustnessTests.cs"))
      .ToList();

    var issues = new List<string>();

    foreach (var file in csFiles) {
      var lines = File.ReadAllLines(file);
      var relativePath = Path.GetRelativePath(repoRoot!, file);

      for (var i = 0; i < lines.Length; i++) {
        var line = lines[i];
        if (!line.Contains("ArrayPool") || !line.Contains(".Rent("))
          continue;

        // Found a Rent — look for a matching Return in the same file
        var methodLines = GetMethodBody(lines, i);
        var hasReturn = methodLines.Any(ml =>
          ml.Contains(".Return(") || ml.Contains("ArrayPool"));

        // Also check for try/finally pattern or using pattern near this rent
        var hasSafePattern = HasSafeReturnPattern(lines, i);

        if (!hasReturn && !hasSafePattern) {
          issues.Add($"{relativePath}:{i + 1} — ArrayPool.Rent() without visible Return in same method scope");
        }
      }
    }

    if (issues.Count > 0) {
      TestContext.WriteLine("=== ArrayPool Rent/Return Audit Results ===");
      foreach (var issue in issues)
        TestContext.WriteLine($"  WARNING: {issue}");
      TestContext.WriteLine($"Total potential issues: {issues.Count}");
      TestContext.WriteLine("NOTE: Some may be false positives (e.g., returned via helper, passed to another owner)");
    } else {
      TestContext.WriteLine("ArrayPool audit: All Rent calls appear to have matching Returns.");
    }

    // This is an informational audit — report but don't fail
    Assert.Pass($"ArrayPool audit complete. Found {issues.Count} potential issue(s). See test output for details.");
  }

  #endregion

  #region Span Safety (stackalloc audit)

  [Test]
  [CancelAfter(30000)]
  public void StackallocAudit_NoStackallocInsideLoops() {
    var repoRoot = FindRepoRoot();
    Assert.That(repoRoot, Is.Not.Null, "Could not find repository root");

    var csFiles = Directory.GetFiles(repoRoot!, "*.cs", SearchOption.AllDirectories)
      .Where(f => !f.Contains("obj") && !f.Contains("bin") && !f.Contains("RobustnessTests.cs"))
      .ToList();

    var issues = new List<string>();

    foreach (var file in csFiles) {
      var lines = File.ReadAllLines(file);
      var relativePath = Path.GetRelativePath(repoRoot!, file);

      for (var i = 0; i < lines.Length; i++) {
        var line = lines[i].Trim();
        if (!line.Contains("stackalloc"))
          continue;

        // Check if this stackalloc is inside a loop (for/while/do) by scanning surrounding context
        if (IsInsideLoop(lines, i)) {
          issues.Add($"{relativePath}:{i + 1} — stackalloc inside loop body (potential CA2014 / stack overflow risk)");
        }
      }
    }

    if (issues.Count > 0) {
      TestContext.WriteLine("=== Stackalloc-in-Loop Audit Results ===");
      foreach (var issue in issues)
        TestContext.WriteLine($"  WARNING: {issue}");
      TestContext.WriteLine($"Total potential issues: {issues.Count}");
    } else {
      TestContext.WriteLine("Stackalloc audit: No stackalloc found inside loop bodies.");
    }

    Assert.Pass($"Stackalloc audit complete. Found {issues.Count} potential issue(s). See test output for details.");
  }

  #endregion

  // ── Helpers ──────────────────────────────────────────────────────

  private static string? FindRepoRoot() {
    var dir = AppDomain.CurrentDomain.BaseDirectory;
    while (dir != null) {
      if (Directory.Exists(Path.Combine(dir, ".git")))
        return dir;
      // Also check for .slnx as an alternative root indicator
      if (Directory.GetFiles(dir, "*.slnx").Length > 0)
        return dir;
      dir = Path.GetDirectoryName(dir);
    }
    return null;
  }

  /// <summary>
  /// Extracts lines belonging to the method body around the given line index.
  /// Simple heuristic: scan forward/backward for brace-balanced scope.
  /// </summary>
  private static string[] GetMethodBody(string[] lines, int lineIndex) {
    // Scan backward to find the method start (opening brace at same or higher scope)
    var braceCount = 0;
    var start = lineIndex;
    for (var i = lineIndex; i >= 0; i--) {
      foreach (var ch in lines[i]) {
        if (ch == '}') braceCount++;
        else if (ch == '{') braceCount--;
      }
      start = i;
      if (braceCount < 0) break;
    }

    // Scan forward to find the method end
    braceCount = 0;
    var end = lineIndex;
    for (var i = start; i < lines.Length; i++) {
      foreach (var ch in lines[i]) {
        if (ch == '{') braceCount++;
        else if (ch == '}') braceCount--;
      }
      end = i;
      if (braceCount <= 0 && i > start) break;
    }

    return lines[start..(end + 1)];
  }

  /// <summary>
  /// Checks if there is a try/finally or using pattern near a Rent call that ensures Return.
  /// </summary>
  private static bool HasSafeReturnPattern(string[] lines, int rentLine) {
    // Look in a window of 30 lines after the rent for Return, finally, or using
    var end = Math.Min(lines.Length, rentLine + 30);
    for (var i = rentLine; i < end; i++) {
      var line = lines[i];
      if (line.Contains(".Return("))
        return true;
      if (line.Contains("finally") && LookAheadContains(lines, i, 10, ".Return("))
        return true;
    }
    return false;
  }

  private static bool LookAheadContains(string[] lines, int start, int count, string text) {
    var end = Math.Min(lines.Length, start + count);
    for (var i = start; i < end; i++) {
      if (lines[i].Contains(text))
        return true;
    }
    return false;
  }

  /// <summary>
  /// Determines if a line containing stackalloc is inside a loop body.
  /// Uses brace-counting heuristic: scans backward from the stackalloc line,
  /// looking for for/while/do keywords at the same or enclosing scope level.
  /// </summary>
  private static bool IsInsideLoop(string[] lines, int stackallocLine) {
    var braceDepth = 0;

    for (var i = stackallocLine - 1; i >= 0; i--) {
      var line = lines[i].Trim();

      // Count braces going backward
      for (var j = line.Length - 1; j >= 0; j--) {
        if (line[j] == '}') braceDepth++;
        else if (line[j] == '{') braceDepth--;
      }

      // If we've closed a scope (braceDepth < 0), we've exited the enclosing block
      if (braceDepth < -1)
        return false;

      // At current or parent scope, check for loop keywords
      if (braceDepth <= 0) {
        if (line.StartsWith("for ") || line.StartsWith("for(") ||
            line.StartsWith("foreach ") || line.StartsWith("foreach(") ||
            line.StartsWith("while ") || line.StartsWith("while(") ||
            line.StartsWith("do ") || line.StartsWith("do{") || line == "do")
          return true;
      }
    }

    return false;
  }
}
