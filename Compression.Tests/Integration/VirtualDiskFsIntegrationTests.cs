#pragma warning disable CS1591
using System.Text;
using Compression.Core.DiskImage;
using Compression.Registry;
using FileFormat.Vhd;
using FileSystem.ExFat;
using FileSystem.Fat;

namespace Compression.Tests.Integration;

/// <summary>
/// End-to-end integration tests exercising the full chain:
/// virtual-disk container → partition table → filesystem → defrag.
/// These tests stitch together independently-tested components
/// (VHD writer/stream, PartitionEditor, FAT/ExFAT descriptors,
/// planner-driven defrag) and verify they cooperate at the seams.
/// </summary>
[TestFixture, Category("Integration")]
public class VirtualDiskFsIntegrationTests {

  private const int SectorSize = 512;
  private const int OneMiB = 1024 * 1024;

  private readonly List<string> _tempFiles = [];
  private readonly List<string> _tempDirs = [];

  [SetUp]
  public void EnsureRegistered() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
  }

  [TearDown]
  public void CleanupTempFiles() {
    foreach (var path in this._tempFiles) {
      try {
        if (File.Exists(path))
          File.Delete(path);
      } catch {
        // best-effort cleanup; ignore locked files
      }
    }
    this._tempFiles.Clear();
    foreach (var dir in this._tempDirs) {
      try {
        if (Directory.Exists(dir))
          Directory.Delete(dir, recursive: true);
      } catch {
        // best-effort cleanup
      }
    }
    this._tempDirs.Clear();
  }

  /// <summary>
  /// Materialises a list of in-memory (name, data) samples into a temp
  /// directory and returns the matching <see cref="ArchiveInputInfo"/> list.
  /// The temp dir is tracked for [TearDown] cleanup.
  /// </summary>
  private List<ArchiveInputInfo> MaterialiseInputs(List<(string Name, byte[] Data)> samples) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-fsit-in-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    this._tempDirs.Add(dir);

    var result = new List<ArchiveInputInfo>(samples.Count);
    foreach (var (name, data) in samples) {
      var path = Path.Combine(dir, name);
      File.WriteAllBytes(path, data);
      result.Add(new ArchiveInputInfo(FullPath: path, ArchiveName: name, IsDirectory: false));
    }
    return result;
  }

  // ────────────────────────────────────────────────────────────────────
  // Test 1 — full chain over a dynamic VHD with 2 FS partitions.
  // ────────────────────────────────────────────────────────────────────

  [Test, Category("Integration")]
  public void FullChain_DynamicVhd_TwoFsPartitions_DefragBothEnds() {
    // ── 1. Create a fresh dynamic VHD (~100 MB virtual; sparse on disk). ──
    const long virtualSize = 100L * OneMiB;
    var tempPath = Path.GetTempFileName();
    this._tempFiles.Add(tempPath);

    {
      // Build a sparse raw disk with just an MBR boot signature so the
      // PartitionEditor recognises it as MBR right away.
      var raw = new byte[virtualSize];
      raw[510] = 0x55;
      raw[511] = 0xAA;
      var w = new VhdWriter();
      w.SetDiskData(raw);
      File.WriteAllBytes(tempPath, w.BuildDynamic());
    }

    var desc = new VhdFormatDescriptor();

    // Partition layout (one MiB aligned, two big enough for FAT/exFAT defaults):
    //   [   1 MiB ..  17 MiB)  FAT32  (16 MiB)
    //   [  17 MiB ..  33 MiB)  exFAT  (16 MiB)
    const long fatStart = 1L * OneMiB, fatLen = 16L * OneMiB;
    const long exfatStart = 17L * OneMiB, exfatLen = 16L * OneMiB;

    // ── 2-4. Open editor via container, add MBR partitions, format each. ──
    using (var file = File.Open(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    using (var guest = ((IPartitionEditable)desc).OpenGuestDiskStream(file)) {
      var editor = new PartitionEditor(guest);
      editor.AddPartition(fatStart, fatLen, PartitionType.Fat32Lba, null);
      editor.AddPartition(exfatStart, exfatLen, PartitionType.NtfsExfat, null);

      var partsBefore = editor.ListPartitions();
      Assert.That(partsBefore, Has.Count.EqualTo(2));

      // Format each partition with the matching FS.
      editor.FormatPartition(0, "Fat", new FormatCreateOptions());
      editor.FormatPartition(1, "ExFat", new FormatCreateOptions());
    }

    // ── 5. Per-partition: open window stream, add files, defrag, verify. ──
    var fatSamples = MakeSampleFiles("FAT");
    var exfatSamples = MakeSampleFiles("XF");

    var (fatAfterAdd, fatAfterDefrag) = this.ExerciseFs(
      tempPath, desc, fatStart, fatLen,
      fsDesc: new FatFormatDescriptor(),
      samples: fatSamples,
      expectedCount: fatSamples.Count,
      defragMode: DefragMode.ConsolidateAtEnd);

    var (exfatAfterAdd, exfatAfterDefrag) = this.ExerciseFs(
      tempPath, desc, exfatStart, exfatLen,
      fsDesc: new ExFatFormatDescriptor(),
      samples: exfatSamples,
      expectedCount: exfatSamples.Count,
      defragMode: DefragMode.ConsolidateAtEnd);

    // Round-trip verification: at least one sample must come back byte-identical
    // from each partition. We split into Add-phase and Defrag-phase so failures
    // are attributable to one step or the other.
    AssertAtLeastOneRoundTripped(fatSamples, fatAfterAdd, "FAT (after Add)");
    AssertAtLeastOneRoundTripped(exfatSamples, exfatAfterAdd, "exFAT (after Add)");

    // Post-defrag byte-equality: both FAT and exFAT must round-trip a sample
    // byte-equal. Task #149 fixed the exFAT planner-driven defrag — it was
    // sizing the planner's image bound at archive.Length (the 16 MiB partition
    // window) rather than the VBR-declared volume size (8 MiB), so
    // ConsolidateAtEnd targeted offsets past the end of the cluster heap and
    // corrupted file contents.
    AssertAtLeastOneRoundTripped(fatSamples, fatAfterDefrag, "FAT (after Defrag)");
    AssertAtLeastOneRoundTripped(exfatSamples, exfatAfterDefrag, "exFAT (after Defrag)");

    // ── 6. Re-open VHD, verify both partitions + FS signatures intact. ──
    using (var file = File.Open(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    using (var guest = ((IPartitionEditable)desc).OpenGuestDiskStream(file)) {
      var editor = new PartitionEditor(guest);
      var parts = editor.ListPartitions();
      Assert.That(parts, Has.Count.EqualTo(2), "Both partitions persist across reopen.");
      Assert.That(parts[0].TypeCode, Is.EqualTo("0x0C"), "FAT32-LBA");
      Assert.That(parts[1].TypeCode, Is.EqualTo("0x07"), "NTFS/exFAT");

      // exFAT VBR sentinel ("EXFAT   " at offset 3 of the partition's first sector).
      using var exfatWindow = new WindowedStream(guest, exfatStart, exfatLen, leaveOpen: true);
      var vbrHead = new byte[16];
      exfatWindow.ReadExactly(vbrHead);
      Assert.That(Encoding.ASCII.GetString(vbrHead, 3, 8), Is.EqualTo("EXFAT   "),
        "exFAT partition still has VBR magic after defrag/reopen.");

      // FAT partition: the boot sector's last two bytes are the 0x55AA
      // signature. (We don't pin the exact FAT type — FatWriter.Build chooses
      // FAT12/16/32 from the resulting cluster count.)
      using var fatWindow = new WindowedStream(guest, fatStart, fatLen, leaveOpen: true);
      var bootSector = new byte[SectorSize];
      fatWindow.ReadExactly(bootSector);
      Assert.That(bootSector[510], Is.EqualTo((byte)0x55),
        "FAT partition boot signature byte 510 still 0x55 after defrag/reopen.");
      Assert.That(bootSector[511], Is.EqualTo((byte)0xAA),
        "FAT partition boot signature byte 511 still 0xAA after defrag/reopen.");
    }
  }

  // ────────────────────────────────────────────────────────────────────
  // Test 2 — MBR→GPT conversion preserves FS contents.
  // ────────────────────────────────────────────────────────────────────

  [Test, Category("Integration")]
  public void FullChain_RawImage_MbrToGptConversion_PreservesFsContents() {
    // ── 1. Raw 50 MB MBR-signed image in MemoryStream. ──
    const long diskSize = 50L * OneMiB;
    var raw = new byte[diskSize];
    raw[510] = 0x55;
    raw[511] = 0xAA;
    using var ms = new MemoryStream();
    ms.Write(raw);
    ms.SetLength(diskSize);

    // Two partitions. Both formatted FAT — one as FAT12 type (legacy small),
    // the other as FAT32-LBA. Sizes chosen so the default FatWriter image
    // (1.44 MB) fits in both.
    const long p1Start = 1L * OneMiB, p1Len = 8L * OneMiB;
    const long p2Start = 9L * OneMiB, p2Len = 8L * OneMiB;

    // ── 2. Add MBR partitions and format. ──
    {
      var editor = new PartitionEditor(ms);
      editor.AddPartition(p1Start, p1Len, PartitionType.Fat12, null);
      editor.AddPartition(p2Start, p2Len, PartitionType.Fat32Lba, null);
      editor.FormatPartition(0, "Fat", new FormatCreateOptions());
      editor.FormatPartition(1, "Fat", new FormatCreateOptions());
    }

    // ── 3. Add files to each partition. ──
    var p1Samples = MakeSampleFiles("P1");
    var p2Samples = MakeSampleFiles("P2");

    this.AddFilesToWindow(ms, p1Start, p1Len, new FatFormatDescriptor(), p1Samples);
    this.AddFilesToWindow(ms, p2Start, p2Len, new FatFormatDescriptor(), p2Samples);

    // ── 4. Convert MBR → GPT. ──
    {
      var editor = new PartitionEditor(ms);
      Assume.That(editor.Scheme, Is.EqualTo(PartitionScheme.Mbr));
      editor.ConvertMbrToGpt();
      Assert.That(editor.Scheme, Is.EqualTo(PartitionScheme.Gpt),
        "After ConvertMbrToGpt, the scheme is GPT.");
    }

    // ── 5. Re-open and confirm both partitions + verification. ──
    {
      ms.Position = 0;
      var editor = new PartitionEditor(ms);
      Assert.That(editor.Scheme, Is.EqualTo(PartitionScheme.Gpt));
      var parts = editor.ListPartitions();
      Assert.That(parts, Has.Count.EqualTo(2), "Both partitions survive conversion.");
      Assert.That(parts[0].StartOffset, Is.EqualTo(p1Start));
      Assert.That(parts[0].Size, Is.EqualTo(p1Len));
      Assert.That(parts[1].StartOffset, Is.EqualTo(p2Start));
      Assert.That(parts[1].Size, Is.EqualTo(p2Len));

      var verification = editor.Verify();
      Assert.That(verification.IsValid, Is.True,
        $"GPT verification reports issues: {string.Join("; ", verification.Issues)}");
    }

    // ── 6. Extract files from each — at least one byte-equal round-trip per partition. ──
    var p1Extracted = ListAndExtract(ms, p1Start, p1Len, new FatFormatDescriptor());
    var p2Extracted = ListAndExtract(ms, p2Start, p2Len, new FatFormatDescriptor());

    AssertAtLeastOneRoundTripped(p1Samples, p1Extracted, "P1 (post MBR→GPT)");
    AssertAtLeastOneRoundTripped(p2Samples, p2Extracted, "P2 (post MBR→GPT)");
  }

  // ────────────────────────────────────────────────────────────────────
  // Helpers
  // ────────────────────────────────────────────────────────────────────

  private static List<(string Name, byte[] Data)> MakeSampleFiles(string prefix) {
    // Five small files. Names kept short and 8.3-compatible so the round-trip
    // assertion isn't tripped by FAT name mangling. Data sizes vary so the
    // defrag planner has interesting layout to work with.
    return [
      ($"{prefix}A.TXT", Encoding.ASCII.GetBytes("alpha sample payload")),
      ($"{prefix}B.TXT", Encoding.ASCII.GetBytes("bravo sample payload, slightly larger")),
      ($"{prefix}C.TXT", new byte[2048]),     // 2 KB zero-filled
      ($"{prefix}D.TXT", BuildDeterministicData(seed: 0x4D, length: 4096)),
      ($"{prefix}E.TXT", Encoding.ASCII.GetBytes("echo")),
    ];
  }

  private static byte[] BuildDeterministicData(int seed, int length) {
    var buf = new byte[length];
    var v = (byte)seed;
    for (var i = 0; i < length; ++i) {
      buf[i] = v;
      v = (byte)(v * 17 + 1);
    }
    return buf;
  }

  /// <summary>
  /// Performs the per-partition exercise: open a window onto the partition,
  /// add files, list, defrag, list again, extract. Returns the extracted
  /// dictionary keyed by (case-insensitive) filename. Also captures the
  /// extracted content immediately after Add (before defrag) so callers can
  /// distinguish add-vs-defrag failures.
  /// </summary>
  private (Dictionary<string, byte[]> AfterAdd, Dictionary<string, byte[]> AfterDefrag) ExerciseFs(
    string vhdPath,
    VhdFormatDescriptor containerDesc,
    long start,
    long length,
    IArchiveFormatOperations fsDesc,
    List<(string Name, byte[] Data)> samples,
    int expectedCount,
    DefragMode defragMode) {

    Dictionary<string, byte[]> afterAdd;
    Dictionary<string, byte[]> afterDefragExtract;

    using (var file = File.Open(vhdPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    using (var guest = ((IPartitionEditable)containerDesc).OpenGuestDiskStream(file))
    using (var window = new WindowedStream(guest, start, length, leaveOpen: true)) {

      // a. Add 5 files via the FS modifier.
      var inputs = this.MaterialiseInputs(samples);
      ((IArchiveModifiable)fsDesc).Add(window, inputs);

      // b. List entries — verify count matches the number we added.
      window.Position = 0;
      var listed = fsDesc.List(window, null);
      var fileCount = listed.Count(e => !e.IsDirectory);
      Assert.That(fileCount, Is.GreaterThanOrEqualTo(expectedCount),
        $"Expected {expectedCount} files after Add; got {fileCount}.");

      // Capture pre-defrag content so we can blame Add vs Defrag separately.
      window.Position = 0;
      afterAdd = ExtractAllToMemory(fsDesc, window);

      // c. Defragment via planner-driven path.
      window.Position = 0;
      ((IArchiveDefragmentable)fsDesc).Defragment(window, new DefragOptions {
        Mode = defragMode,
        Profile = LayoutProfile.Performance,
      });

      // d. List again — verify all still present.
      window.Position = 0;
      var listedAfter = fsDesc.List(window, null);
      var fileCountAfter = listedAfter.Count(e => !e.IsDirectory);
      Assert.That(fileCountAfter, Is.EqualTo(fileCount),
        "File count unchanged across defragmentation.");

      // e. Extract and stash for the byte-equality check at the call site.
      window.Position = 0;
      afterDefragExtract = ExtractAllToMemory(fsDesc, window);
    }

    return (afterAdd, afterDefragExtract);
  }

  /// <summary>
  /// Adds files to an existing FS image positioned inside a partition window.
  /// </summary>
  private void AddFilesToWindow(
    Stream disk,
    long start,
    long length,
    IArchiveFormatOperations fsDesc,
    List<(string Name, byte[] Data)> samples) {

    using var window = new WindowedStream(disk, start, length, leaveOpen: true);
    var inputs = this.MaterialiseInputs(samples);
    ((IArchiveModifiable)fsDesc).Add(window, inputs);
  }

  private static Dictionary<string, byte[]> ListAndExtract(
    Stream disk,
    long start,
    long length,
    IArchiveFormatOperations fsDesc) {

    using var window = new WindowedStream(disk, start, length, leaveOpen: true);
    return ExtractAllToMemory(fsDesc, window);
  }

  /// <summary>
  /// Extracts every non-directory entry to memory via the FS descriptor's
  /// <c>Extract</c> by piping a temporary directory.
  /// </summary>
  private static Dictionary<string, byte[]> ExtractAllToMemory(
    IArchiveFormatOperations fsDesc,
    Stream fsImage) {

    var tempDir = Path.Combine(Path.GetTempPath(),
      "cwb-fsit-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);
    try {
      fsImage.Position = 0;
      fsDesc.Extract(fsImage, tempDir, password: null, files: null);

      var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
      foreach (var path in Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories)) {
        var name = Path.GetFileName(path);
        result[name] = File.ReadAllBytes(path);
      }
      return result;
    } finally {
      try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
    }
  }

  /// <summary>
  /// Asserts that at least one of the samples is present byte-identically in
  /// the extracted set. FAT name mangling (upper-case, 8.3 truncation) can
  /// change the key; we accept any case-insensitive match.
  /// <para>
  /// When <paramref name="lenient"/> is <c>true</c>, the byte-equality check
  /// is downgraded to a name-equality check (every sample name must appear in
  /// the extracted set) so we still catch missing files but tolerate known
  /// content quirks the surrounding test doesn't isolate.
  /// </para>
  /// </summary>
  private static void AssertAtLeastOneRoundTripped(
    List<(string Name, byte[] Data)> samples,
    Dictionary<string, byte[]> extracted,
    string label,
    bool lenient = false) {

    Assert.That(extracted, Is.Not.Empty, $"{label}: at least one file extracted.");

    foreach (var (name, data) in samples) {
      // Try the exact name (case-insensitive map already), then a fuzzy
      // upper-case match for FAT 8.3 normalisation.
      if (extracted.TryGetValue(name, out var bytes) && bytes.SequenceEqual(data))
        return;
      var upper = name.ToUpperInvariant();
      if (extracted.TryGetValue(upper, out var b2) && b2.SequenceEqual(data))
        return;
    }

    // Fallback: any sample's bytes appearing somewhere in the extracted set.
    foreach (var (_, data) in samples)
      foreach (var bytes in extracted.Values)
        if (bytes.SequenceEqual(data))
          return;

    if (lenient) {
      // Downgrade: require only that every sample name appears.
      foreach (var (name, _) in samples) {
        var upper = name.ToUpperInvariant();
        if (!extracted.ContainsKey(name) && !extracted.ContainsKey(upper))
          Assert.Fail(
            $"{label}: file '{name}' missing from extracted set " +
            $"(have: {string.Join(", ", extracted.Keys)}).");
      }
      return;
    }

    Assert.Fail(
      $"{label}: none of the {samples.Count} added samples round-tripped byte-equal. " +
      $"Extracted files: {string.Join(", ", extracted.Keys)}.");
  }
}

/// <summary>
/// Minimal Stream wrapper exposing a fixed [offset .. offset+length) window of
/// an inner stream as if it were a standalone stream. Used so FS readers,
/// writers, and defrag executors operate on a single partition's byte range
/// without writing past the partition boundary.
/// <para>
/// <see cref="SetLength"/> is a no-op when the requested value equals the
/// fixed window length (callers like FatFormatDescriptor.Add issue this after
/// rebuilding an image at the original size) and throws otherwise.
/// </para>
/// </summary>
internal sealed class WindowedStream : Stream {
  private readonly Stream _inner;
  private readonly long _offset;
  private readonly long _length;
  private readonly bool _leaveOpen;
  private long _position;

  public WindowedStream(Stream inner, long offset, long length, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(inner);
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
    if (!inner.CanSeek) throw new ArgumentException("Inner stream must be seekable.", nameof(inner));

    this._inner = inner;
    this._offset = offset;
    this._length = length;
    this._leaveOpen = leaveOpen;
  }

  public override bool CanRead => this._inner.CanRead;
  public override bool CanSeek => true;
  public override bool CanWrite => this._inner.CanWrite;
  public override long Length => this._length;

  public override long Position {
    get => this._position;
    set {
      if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
      this._position = value;
    }
  }

  public override int Read(byte[] buffer, int offset, int count) {
    if (this._position >= this._length) return 0;
    var toRead = (int)Math.Min(count, this._length - this._position);
    this._inner.Position = this._offset + this._position;
    var read = this._inner.Read(buffer, offset, toRead);
    this._position += read;
    return read;
  }

  public override void Write(byte[] buffer, int offset, int count) {
    if (this._position + count > this._length)
      throw new IOException(
        $"Write would exceed window bounds (pos={this._position}, count={count}, window={this._length}).");
    this._inner.Position = this._offset + this._position;
    this._inner.Write(buffer, offset, count);
    this._position += count;
  }

  public override long Seek(long offset, SeekOrigin origin) {
    var newPos = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => this._position + offset,
      SeekOrigin.End => this._length + offset,
      _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    if (newPos < 0) throw new IOException("Seek before beginning of stream.");
    this._position = newPos;
    return this._position;
  }

  public override void SetLength(long value) {
    if (value == this._length) return; // no-op: window is fixed-size
    throw new NotSupportedException(
      $"WindowedStream is fixed-size ({this._length} bytes); cannot SetLength to {value}.");
  }

  public override void Flush() => this._inner.Flush();

  protected override void Dispose(bool disposing) {
    if (disposing && !this._leaveOpen)
      this._inner.Dispose();
    base.Dispose(disposing);
  }
}
