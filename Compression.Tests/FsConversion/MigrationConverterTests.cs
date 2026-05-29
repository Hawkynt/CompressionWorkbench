#pragma warning disable CS1591
using Compression.Lib.FsConversion;
using Compression.Registry;
using FileSystem.ExFat;
using FileSystem.Fat;

namespace Compression.Tests.FsConversion;

/// <summary>
/// Crash-safety + round-trip tests for <see cref="MigrationConverter"/>. The
/// happy-path test verifies a 5-file FAT → exFAT migration leaves all files on
/// dst with src empty. The remaining tests exercise the recovery paths:
/// resume after a mid-flight crash, recovery from a corrupted manifest, and
/// recovery from a manifest torn in the middle of an entry.
/// </summary>
[TestFixture]
public class MigrationConverterTests {

  [SetUp]
  public void SetUp() => Compression.Lib.FormatRegistration.EnsureInitialized();

  // ── Helpers ────────────────────────────────────────────────────────────

  private static MemoryStream BuildPopulatedFat(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new FatWriter();
    foreach (var (name, data) in files)
      w.AddFile(name, data);
    var img = w.Build();
    var ms = new MemoryStream();
    ms.Write(img);
    ms.SetLength(img.Length);
    ms.Position = 0;
    return ms;
  }

  private static MemoryStream BuildEmptyExFat() {
    var img = new ExFatWriter().Build();
    var ms = new MemoryStream();
    ms.Write(img);
    ms.SetLength(img.Length);
    ms.Position = 0;
    return ms;
  }

  private static (string Name, byte[] Data)[] SampleFiles() => [
    ("ALPHA.TXT", System.Text.Encoding.ASCII.GetBytes("first-payload-alpha")),
    ("BETA.BIN",  Pattern(0xAB, 1500)),
    ("GAMMA.LOG", System.Text.Encoding.ASCII.GetBytes("log entry — γ")),
    ("DELTA.DAT", Pattern(0x42, 250)),
    ("EPS.TXT",   System.Text.Encoding.ASCII.GetBytes("epsilon")),
  ];

  private static byte[] Pattern(byte seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((seed + i * 7) & 0xFF);
    return data;
  }

  private static byte[] ExtractFromFat(Stream src, string name) {
    src.Position = 0;
    var reader = new FatReader(src);
    var entry = reader.Entries.Single(e => e.Name == name);
    return reader.Extract(entry);
  }

  private static byte[] ExtractFromExFat(Stream dst, string name) {
    dst.Position = 0;
    var reader = new ExFatReader(dst);
    var entry = reader.Entries.Single(e => e.Name == name);
    return reader.Extract(entry);
  }

  private static List<string> ListFatNames(Stream src) {
    src.Position = 0;
    return [.. new FatReader(src).Entries.Where(e => !e.IsDirectory).Select(e => e.Name)];
  }

  private static List<string> ListExFatNames(Stream dst) {
    dst.Position = 0;
    return [.. new ExFatReader(dst).Entries.Where(e => !e.IsDirectory).Select(e => e.Name)];
  }

  // ── Happy path: full migration FAT → exFAT ─────────────────────────────

  [Test, Category("Migration")]
  public void FullMigration_FatToExFat_MovesAllFiles() {
    var files = SampleFiles();
    using var src = BuildPopulatedFat(files);
    using var dst = BuildEmptyExFat();

    var migrator = new MigrationConverter(src, dst, "Fat", "ExFat");
    migrator.Run();

    // Every file is now on dst, none on src.
    var dstNames = ListExFatNames(dst);
    var srcNames = ListFatNames(src);
    foreach (var (name, _) in files) {
      Assert.That(dstNames, Does.Contain(name),
        $"After migration, '{name}' must be on dst.");
      Assert.That(srcNames, Does.Not.Contain(name),
        $"After migration, '{name}' must NOT be on src.");
    }

    // Manifest deleted at the end of a clean run.
    Assert.That(dstNames, Does.Not.Contain(ConversionManifest.FileName),
      "Manifest should be removed when migration completes cleanly.");

    // Bytes match.
    foreach (var (name, data) in files)
      Assert.That(ExtractFromExFat(dst, name), Is.EqualTo(data),
        $"Bytes of '{name}' must round-trip exactly.");
  }

  // ── Resume after mid-flight interruption ───────────────────────────────

  [Test, Category("Migration")]
  public void Resume_AfterPartialMigration_CompletesAllFiles() {
    var files = SampleFiles();
    using var src = BuildPopulatedFat(files);
    using var dst = BuildEmptyExFat();

    // First pass: migrate only 3 of the 5 files by tearing down the converter
    // mid-stream. We simulate this by directly invoking the per-file copy +
    // manifest update steps until the 4th file, then aborting.
    var partial = new PartialMigrator(src, dst, stopAfter: 3);
    partial.RunUntilStop();

    // Sanity: 3 done, 2 still on src.
    var srcAfterPartial = ListFatNames(src);
    var dstAfterPartial = ListExFatNames(dst);
    Assert.That(srcAfterPartial.Count, Is.EqualTo(2),
      "After 3-of-5 partial migration, exactly 2 files remain on src.");
    Assert.That(dstAfterPartial.Count(n => n != ConversionManifest.FileName), Is.EqualTo(3),
      "After 3-of-5 partial migration, exactly 3 files are on dst.");
    Assert.That(dstAfterPartial, Does.Contain(ConversionManifest.FileName),
      "Manifest must be present after partial completion.");

    // Resume from the same dst — manifest tells us where to pick up.
    new MigrationConverter(src, dst, "Fat", "ExFat").Resume();

    // After resume: every file on dst, none on src, manifest gone.
    var srcFinal = ListFatNames(src);
    var dstFinal = ListExFatNames(dst);
    foreach (var (name, _) in files) {
      Assert.That(dstFinal, Does.Contain(name), $"Resumed migration should land '{name}' on dst.");
      Assert.That(srcFinal, Does.Not.Contain(name), $"Resumed migration should empty '{name}' from src.");
    }
    Assert.That(dstFinal, Does.Not.Contain(ConversionManifest.FileName),
      "Manifest must be removed at the end of a successful resume.");

    foreach (var (name, data) in files)
      Assert.That(ExtractFromExFat(dst, name), Is.EqualTo(data),
        $"Bytes of '{name}' must round-trip across the crash boundary.");
  }

  // ── Corrupted manifest: trailing CRC mismatch ──────────────────────────

  [Test, Category("Migration")]
  public void Resume_AfterCorruptedManifest_RebuildsFromSrcContents() {
    // Simulate a partial-write that corrupted the last-written manifest:
    // the destination FS has a .conversion-manifest blob whose trailing CRC
    // is wrong (caller saw the manifest byte-extent grow but the data was
    // never flushed atomically).
    var files = SampleFiles();
    using var src = BuildPopulatedFat(files);
    using var dst = BuildEmptyExFat();

    // First: complete a 2-of-5 partial migration so dst has real files +
    // a valid manifest.
    new PartialMigrator(src, dst, stopAfter: 2).RunUntilStop();

    // Now corrupt the manifest by extracting it, flipping a byte in the
    // middle, then re-adding. ParseManifest will reject this on the next
    // resume — and the recovery path treats "no manifest" as "rebuild
    // from src contents" while leaving the already-migrated dst files
    // alone.
    CorruptManifestOnDst(dst);

    // Resume: should rebuild manifest from src, observe the 3 remaining
    // files on src + the 2 already on dst, and migrate the 3 remaining.
    new MigrationConverter(src, dst, "Fat", "ExFat").Resume();

    var srcFinal = ListFatNames(src);
    var dstFinal = ListExFatNames(dst);
    foreach (var (name, _) in files) {
      Assert.That(dstFinal, Does.Contain(name),
        $"Corrupted-manifest recovery must still land '{name}' on dst.");
      Assert.That(srcFinal, Does.Not.Contain(name),
        $"Corrupted-manifest recovery must still empty '{name}' from src.");
    }
    Assert.That(dstFinal, Does.Not.Contain(ConversionManifest.FileName),
      "Manifest must be removed at the end of recovery.");

    foreach (var (name, data) in files)
      Assert.That(ExtractFromExFat(dst, name), Is.EqualTo(data),
        $"Bytes of '{name}' must round-trip across the corrupted-manifest recovery.");
  }

  // ── Torn manifest write: midway through an entry record ────────────────

  [Test, Category("Migration")]
  public void Resume_AfterTornManifestEntry_TreatedAsCopying() {
    // Tear the manifest blob exactly in the middle of an entry record.
    // The CRC won't match → parser returns null → resume treats the dst as
    // "no manifest, but possibly non-empty" and proceeds correctly.
    var files = SampleFiles();
    using var src = BuildPopulatedFat(files);
    using var dst = BuildEmptyExFat();

    // Get to a known intermediate state: 1 file copied, manifest written.
    new PartialMigrator(src, dst, stopAfter: 1).RunUntilStop();

    // Truncate the manifest blob mid-entry by re-adding a half-length copy.
    var fullManifest = ExtractManifestBytes(dst);
    Assert.That(fullManifest, Is.Not.Null, "Pre-condition: dst has a manifest after partial run.");
    var halfLen = fullManifest!.Length / 2;
    var torn = new byte[halfLen];
    Array.Copy(fullManifest, torn, halfLen);
    ReplaceManifestOnDst(dst, torn);

    // Resume should still converge: torn manifest fails CRC → rebuilt from
    // src enumeration → forward pass continues with the remaining files.
    new MigrationConverter(src, dst, "Fat", "ExFat").Resume();

    var srcFinal = ListFatNames(src);
    var dstFinal = ListExFatNames(dst);
    foreach (var (name, _) in files) {
      Assert.That(dstFinal, Does.Contain(name),
        $"Torn-manifest recovery must land '{name}' on dst.");
      Assert.That(srcFinal, Does.Not.Contain(name),
        $"Torn-manifest recovery must empty '{name}' from src.");
    }
    foreach (var (name, data) in files)
      Assert.That(ExtractFromExFat(dst, name), Is.EqualTo(data),
        $"Bytes of '{name}' must round-trip across the torn-manifest recovery.");
  }

  // ── Resume of an already-complete migration is a no-op ────────────────

  [Test, Category("Migration")]
  public void Resume_OnAlreadyComplete_IsNoOp() {
    var files = SampleFiles();
    using var src = BuildPopulatedFat(files);
    using var dst = BuildEmptyExFat();

    new MigrationConverter(src, dst, "Fat", "ExFat").Run();

    // Second Run should not throw, should not re-list src for new files
    // (none there), and should leave dst contents unchanged.
    var dstBytesBefore = dst.ToArray();
    new MigrationConverter(src, dst, "Fat", "ExFat").Run();
    Assert.That(dst.ToArray().Length, Is.EqualTo(dstBytesBefore.Length),
      "Re-running on completed migration must not grow dst.");
  }

  // ── Single-file migration smoke test ──────────────────────────────────

  [Test, Category("Migration")]
  public void Migration_SingleFile_RoundTrips() {
    var files = new (string Name, byte[] Data)[] { ("ONLY.TXT", Pattern(0x11, 42)) };
    using var src = BuildPopulatedFat(files);
    using var dst = BuildEmptyExFat();

    new MigrationConverter(src, dst, "Fat", "ExFat").Run();

    Assert.That(ListExFatNames(dst), Does.Contain("ONLY.TXT"));
    Assert.That(ListFatNames(src), Does.Not.Contain("ONLY.TXT"));
    Assert.That(ExtractFromExFat(dst, "ONLY.TXT"), Is.EqualTo(files[0].Data));
  }

  // ── Constructor validation ─────────────────────────────────────────────

  [Test, Category("Migration")]
  public void Ctor_UnknownFormatId_Throws() {
    using var src = BuildPopulatedFat([("X.TXT", "x"u8.ToArray())]);
    using var dst = BuildEmptyExFat();
    Assert.That(() => new MigrationConverter(src, dst, "NoSuchFs", "ExFat"),
      Throws.InvalidOperationException);
    Assert.That(() => new MigrationConverter(src, dst, "Fat", "NoSuchFs"),
      Throws.InvalidOperationException);
  }

  // ── Manifest tampering helpers ─────────────────────────────────────────

  private static byte[]? ExtractManifestBytes(Stream dst) {
    dst.Position = 0;
    var reader = new ExFatReader(dst);
    var entry = reader.Entries.FirstOrDefault(e => e.Name == ConversionManifest.FileName);
    return entry is null ? null : reader.Extract(entry);
  }

  private static void ReplaceManifestOnDst(Stream dst, byte[] newBytes) {
    // Remove + add with the new payload via the registry interfaces.
    var dstOps = FormatRegistry.GetArchiveOps("ExFat")!;
    var dstMod = (IArchiveModifiable)dstOps;

    dst.Position = 0;
    dstMod.Remove(dst, [ConversionManifest.FileName]);

    var tempPath = Path.Combine(Path.GetTempPath(), "torn-manifest-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
    try {
      File.WriteAllBytes(tempPath, newBytes);
      dst.Position = 0;
      dstMod.Add(dst, [new ArchiveInputInfo(tempPath, ConversionManifest.FileName, false)]);
    } finally {
      try { File.Delete(tempPath); } catch { /* best-effort */ }
    }
  }

  private static void CorruptManifestOnDst(Stream dst) {
    var bytes = ExtractManifestBytes(dst);
    Assert.That(bytes, Is.Not.Null, "Pre-condition for corruption: manifest exists.");
    // Flip a byte inside the body (not the CRC at the tail).
    bytes![bytes.Length / 2] ^= 0xFF;
    ReplaceManifestOnDst(dst, bytes);
  }

  // ── Partial-run harness ────────────────────────────────────────────────
  // Mirrors the inner loop of MigrationConverter.Resume() but stops after N
  // files. Used by Resume_AfterPartialMigration_CompletesAllFiles to simulate
  // a crash.

  private sealed class PartialMigrator {
    private readonly Stream _src;
    private readonly Stream _dst;
    private readonly int _stopAfter;
    private readonly IArchiveFormatOperations _srcOps;
    private readonly IArchiveModifiable _srcMod;
    private readonly IArchiveFormatOperations _dstOps;
    private readonly IArchiveModifiable _dstMod;

    public PartialMigrator(Stream src, Stream dst, int stopAfter) {
      this._src = src;
      this._dst = dst;
      this._stopAfter = stopAfter;
      this._srcOps = FormatRegistry.GetArchiveOps("Fat")!;
      this._srcMod = (IArchiveModifiable)this._srcOps;
      this._dstOps = FormatRegistry.GetArchiveOps("ExFat")!;
      this._dstMod = (IArchiveModifiable)this._dstOps;
    }

    public void RunUntilStop() {
      // Build initial manifest.
      var manifest = new ConversionManifest();
      this._src.Position = 0;
      foreach (var entry in this._srcOps.List(this._src, null))
        if (!entry.IsDirectory && entry.Name != ConversionManifest.FileName)
          manifest.Entries.Add(new ConversionManifestEntry {
            SourcePath = entry.Name,
            Status = ConversionEntryStatus.Pending,
            Size = entry.OriginalSize,
          });
      WriteManifest(this._dst, this._dstMod, manifest);

      var done = 0;
      foreach (var entry in manifest.Entries) {
        if (done >= this._stopAfter) return;

        entry.Status = ConversionEntryStatus.Copying;
        WriteManifest(this._dst, this._dstMod, manifest);

        var data = ExtractFromSrc(this._src, this._srcOps, entry.SourcePath);
        AddToDst(this._dst, this._dstMod, entry.SourcePath, data);
        this._dst.Flush();

        this._src.Position = 0;
        this._srcMod.Remove(this._src, [entry.SourcePath]);
        this._src.Flush();

        entry.Status = ConversionEntryStatus.Done;
        WriteManifest(this._dst, this._dstMod, manifest);
        done++;
      }
    }

    private static byte[] ExtractFromSrc(Stream src, IArchiveFormatOperations ops, string name) {
      var tempDir = Path.Combine(Path.GetTempPath(), "pm-" + Guid.NewGuid().ToString("N")[..8]);
      Directory.CreateDirectory(tempDir);
      try {
        src.Position = 0;
        ops.Extract(src, tempDir, password: null, files: [name]);
        var candidate = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories).First();
        return File.ReadAllBytes(candidate);
      } finally {
        try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
      }
    }

    private static void AddToDst(Stream dst, IArchiveModifiable mod, string name, byte[] data) {
      var tempPath = Path.Combine(Path.GetTempPath(), "pm-add-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
      try {
        File.WriteAllBytes(tempPath, data);
        dst.Position = 0;
        mod.Add(dst, [new ArchiveInputInfo(tempPath, name, false)]);
      } finally {
        try { File.Delete(tempPath); } catch { /* best-effort */ }
      }
    }

    private static void WriteManifest(Stream dst, IArchiveModifiable mod, ConversionManifest manifest) {
      // Replace any existing manifest atomically (remove + add).
      try {
        dst.Position = 0;
        mod.Remove(dst, [ConversionManifest.FileName]);
      } catch { /* idempotent */ }
      var bytes = manifest.Serialize();
      var tempPath = Path.Combine(Path.GetTempPath(), "pm-mf-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
      try {
        File.WriteAllBytes(tempPath, bytes);
        dst.Position = 0;
        mod.Add(dst, [new ArchiveInputInfo(tempPath, ConversionManifest.FileName, false)]);
        dst.Flush();
      } finally {
        try { File.Delete(tempPath); } catch { /* best-effort */ }
      }
    }
  }
}
