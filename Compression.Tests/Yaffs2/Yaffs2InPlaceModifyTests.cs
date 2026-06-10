using System.Text;
using Compression.Registry;

namespace Compression.Tests.Yaffs2;

/// <summary>
/// Locks the log-structured in-place modify contract for YAFFS2: Add / Replace /
/// Remove append fresh chunks at the image tail and never touch bytes in
/// <c>[0, oldLength)</c>. The scanner's seqNumber-max filter then resolves the
/// live view (newest header wins; data chunks bounded by the file's declared size).
/// </summary>
[TestFixture]
public class Yaffs2InPlaceModifyTests {

  private const int ChunkSize = 2048;
  private const int SpareSize = 64;
  private const int Stride = ChunkSize + SpareSize;

  private static byte[] BuildThreeFileImage(out int fileAChunkStart, out int fileAChunkEnd) {
    // Three files. We want a deterministic, mid-image offset for file A so the
    // "untouched bytes" assertion has a real range to compare against.
    var w = new FileSystem.Yaffs2.Yaffs2Writer();
    var contentA = Encoding.UTF8.GetBytes("FILE-A-PAYLOAD-original");
    var contentB = Encoding.UTF8.GetBytes("FILE-B-content");
    var contentC = Encoding.UTF8.GetBytes("FILE-C-content");
    w.AddFile("a.txt", contentA);
    w.AddFile("b.txt", contentB);
    w.AddFile("c.txt", contentC);
    var image = w.Build();

    // The writer emits (in order): root-dir header, then per-file: header + data chunks.
    // File A is the FIRST file written, so its header chunk is at index 1 (after root)
    // and its single data chunk at index 2.
    //   chunk[0]: root dir header
    //   chunk[1]: a.txt header
    //   chunk[2]: a.txt data
    //   chunk[3]: b.txt header
    //   chunk[4]: b.txt data
    //   chunk[5]: c.txt header
    //   chunk[6]: c.txt data
    fileAChunkStart = Stride * 1;          // start of a.txt header
    fileAChunkEnd = Stride * 3;            // end of a.txt data
    return image;
  }

  // ── Add: untouched chunks byte-identical ─────────────────────────────

  [Test, Category("HappyPath"), Category("InPlaceModify")]
  public void Add_DoesNotTouchExistingBytes() {
    var before = BuildThreeFileImage(out var fileAStart, out var fileAEnd);
    var oldLength = before.Length;

    using var ms = new MemoryStream();
    ms.Write(before);
    ms.Position = 0;

    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    var addedContent = Encoding.UTF8.GetBytes("FILE-D-new content via in-place add");
    d.Add(ms, [ArchiveInputInfo.InMemory("d.txt", addedContent)]);

    ms.Position = 0;
    var after = ms.ToArray();

    // The image must have grown (Add appends at the tail).
    Assert.That(after.Length, Is.GreaterThan(oldLength),
      "log-structured Add must grow the image, not rewrite in place");

    // Every byte in [0, oldLength) must be byte-identical.
    Assert.That(after.AsSpan(0, oldLength).ToArray(), Is.EqualTo(before),
      "Add must never touch existing chunk bytes — all of [0, oldLength) must survive");

    // Specifically, file A's chunk range must still match byte-for-byte.
    var beforeSlice = before.AsSpan(fileAStart, fileAEnd - fileAStart).ToArray();
    var afterSlice = after.AsSpan(fileAStart, fileAEnd - fileAStart).ToArray();
    Assert.That(afterSlice, Is.EqualTo(beforeSlice),
      "file A's chunk bytes must be byte-identical at their original offsets");
  }

  [Test, Category("HappyPath"), Category("InPlaceModify"), Category("RoundTrip")]
  public void Add_NewFile_ReadableAlongsideExisting() {
    var before = BuildThreeFileImage(out _, out _);
    using var ms = new MemoryStream();
    ms.Write(before);
    ms.Position = 0;

    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    var added = Encoding.UTF8.GetBytes("freshly-added");
    d.Add(ms, [ArchiveInputInfo.InMemory("d.txt", added)]);

    ms.Position = 0;
    var scan = FileSystem.Yaffs2.Yaffs2Scanner.Scan(ms.ToArray());
    Assert.That(scan.ParseOk, Is.True);

    var fileNames = scan.Objects
      .Where(o => o.Type == FileSystem.Yaffs2.Yaffs2Scanner.YObjectType.File)
      .Select(o => o.Name).ToHashSet();
    Assert.That(fileNames, Does.Contain("a.txt"));
    Assert.That(fileNames, Does.Contain("b.txt"));
    Assert.That(fileNames, Does.Contain("c.txt"));
    Assert.That(fileNames, Does.Contain("d.txt"));
  }

  // ── Replace: old chunks intact, reader picks new ─────────────────────

  [Test, Category("HappyPath"), Category("InPlaceModify")]
  public void Replace_KeepsOldChunksByteIdentical() {
    var before = BuildThreeFileImage(out var fileAStart, out var fileAEnd);
    var oldLength = before.Length;

    using var ms = new MemoryStream();
    ms.Write(before);
    ms.Position = 0;

    var replacement = Encoding.UTF8.GetBytes("FILE-A-PAYLOAD-replaced-and-longer-than-before");
    FileSystem.Yaffs2.Yaffs2InPlaceModifier.Replace(ms, "a.txt", replacement);

    ms.Position = 0;
    var after = ms.ToArray();

    Assert.That(after.Length, Is.GreaterThan(oldLength),
      "Replace appends fresh chunks; image must grow");

    // The old file A chunks (header + data) must stay byte-identical at their
    // original offsets — that's the log-structured invariant.
    var beforeSlice = before.AsSpan(fileAStart, fileAEnd - fileAStart).ToArray();
    var afterSlice = after.AsSpan(fileAStart, fileAEnd - fileAStart).ToArray();
    Assert.That(afterSlice, Is.EqualTo(beforeSlice),
      "old chunks of replaced file must survive byte-for-byte");

    // And the whole [0, oldLength) range is untouched.
    Assert.That(after.AsSpan(0, oldLength).ToArray(), Is.EqualTo(before),
      "Replace must never touch existing chunk bytes");
  }

  [Test, Category("HappyPath"), Category("InPlaceModify"), Category("RoundTrip")]
  public void Replace_ReaderReturnsNewContent_PicksHigherSeqNumber() {
    var before = BuildThreeFileImage(out _, out _);
    using var ms = new MemoryStream();
    ms.Write(before);
    ms.Position = 0;

    var replacement = Encoding.UTF8.GetBytes("REPLACED");
    FileSystem.Yaffs2.Yaffs2InPlaceModifier.Replace(ms, "a.txt", replacement);

    ms.Position = 0;
    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    var bytes = d.ExtractEntryToMemory(ms, "a.txt", null);
    Assert.That(bytes, Is.EqualTo(replacement),
      "scanner must resolve to the highest-seqNumber chunk (the replacement)");
  }

  [Test, Category("HappyPath"), Category("InPlaceModify"), Category("RoundTrip")]
  public void Replace_WithShorterData_BoundsByDeclaredSize() {
    // Original payload spans two chunks; replacement fits in one. The scanner
    // must drop the now-stale tail chunk by bounding chunkId at ceil(size/chunkSize).
    var w = new FileSystem.Yaffs2.Yaffs2Writer();
    var original = new byte[3000];
    for (var i = 0; i < original.Length; i++) original[i] = (byte)(i & 0xFF);
    w.AddFile("big.bin", original);
    var image = w.Build();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var shorter = Encoding.UTF8.GetBytes("tiny");
    FileSystem.Yaffs2.Yaffs2InPlaceModifier.Replace(ms, "big.bin", shorter);

    ms.Position = 0;
    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    var bytes = d.ExtractEntryToMemory(ms, "big.bin", null);
    Assert.That(bytes, Is.EqualTo(shorter),
      "reader must trim to declared size and ignore stale tail chunks of the prior version");
  }

  // ── Remove: tombstone, old chunks intact, object gone ────────────────

  [Test, Category("HappyPath"), Category("InPlaceModify")]
  public void Remove_KeepsOldChunksByteIdentical() {
    var before = BuildThreeFileImage(out var fileAStart, out var fileAEnd);
    var oldLength = before.Length;

    using var ms = new MemoryStream();
    ms.Write(before);
    ms.Position = 0;

    FileSystem.Yaffs2.Yaffs2InPlaceModifier.Remove(ms, "a.txt");

    ms.Position = 0;
    var after = ms.ToArray();

    Assert.That(after.Length, Is.EqualTo(oldLength + Stride),
      "Remove appends exactly one tombstone header chunk (one Stride)");

    // Original chunks survive byte-for-byte.
    Assert.That(after.AsSpan(0, oldLength).ToArray(), Is.EqualTo(before),
      "Remove must never touch existing chunk bytes");
    var beforeSlice = before.AsSpan(fileAStart, fileAEnd - fileAStart).ToArray();
    var afterSlice = after.AsSpan(fileAStart, fileAEnd - fileAStart).ToArray();
    Assert.That(afterSlice, Is.EqualTo(beforeSlice),
      "removed file's chunks stay byte-identical at original offsets");
  }

  [Test, Category("HappyPath"), Category("InPlaceModify"), Category("RoundTrip")]
  public void Remove_ReaderTreatsObjectAsGone() {
    var before = BuildThreeFileImage(out _, out _);
    using var ms = new MemoryStream();
    ms.Write(before);
    ms.Position = 0;

    FileSystem.Yaffs2.Yaffs2InPlaceModifier.Remove(ms, "a.txt");

    ms.Position = 0;
    var scan = FileSystem.Yaffs2.Yaffs2Scanner.Scan(ms.ToArray());
    Assert.That(scan.ParseOk, Is.True);

    var fileNames = scan.Objects
      .Where(o => o.Type == FileSystem.Yaffs2.Yaffs2Scanner.YObjectType.File)
      .Select(o => o.Name).ToHashSet();
    Assert.That(fileNames, Does.Not.Contain("a.txt"),
      "tombstoned object must NOT appear in the live object list");
    Assert.That(fileNames, Does.Contain("b.txt"));
    Assert.That(fileNames, Does.Contain("c.txt"));
  }

  // ── Image growth ─────────────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("InPlaceModify")]
  public void Add_GrowsByExactlyHeaderPlusDataChunks() {
    var before = BuildThreeFileImage(out _, out _);
    var oldLength = before.Length;

    using var ms = new MemoryStream();
    ms.Write(before);
    ms.Position = 0;

    // A single-chunk file adds exactly two strides: 1 header + 1 data chunk.
    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("d.txt", "short"u8.ToArray())]);

    Assert.That(ms.Length, Is.EqualTo(oldLength + Stride * 2),
      "single-chunk Add appends exactly 1 header + 1 data chunk = 2 strides");
  }

  // ── Mixed Add + Remove + Add roundtrip ───────────────────────────────

  [Test, Category("RoundTrip"), Category("InPlaceModify")]
  public void AddRemoveAdd_LiveViewMatchesExpected() {
    var before = BuildThreeFileImage(out _, out _);
    using var ms = new MemoryStream();
    ms.Write(before);
    ms.Position = 0;

    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();

    // Add d.txt
    d.Add(ms, [ArchiveInputInfo.InMemory("d.txt", "first-d"u8.ToArray())]);
    // Remove b.txt
    d.Remove(ms, ["b.txt"]);
    // Add e.txt
    d.Add(ms, [ArchiveInputInfo.InMemory("e.txt", "later-e"u8.ToArray())]);

    ms.Position = 0;
    var scan = FileSystem.Yaffs2.Yaffs2Scanner.Scan(ms.ToArray());
    var fileNames = scan.Objects
      .Where(o => o.Type == FileSystem.Yaffs2.Yaffs2Scanner.YObjectType.File)
      .Select(o => o.Name).ToHashSet();

    Assert.That(fileNames, Does.Contain("a.txt"));
    Assert.That(fileNames, Does.Not.Contain("b.txt"), "b.txt was tombstoned");
    Assert.That(fileNames, Does.Contain("c.txt"));
    Assert.That(fileNames, Does.Contain("d.txt"));
    Assert.That(fileNames, Does.Contain("e.txt"));
  }

  // ── Mutate-then-extract: extracted bytes match the mutation ──────────

  [Test, Category("RoundTrip"), Category("InPlaceModify")]
  public void MutateThenExtract_BytesMatchMutation() {
    var before = BuildThreeFileImage(out _, out _);
    using var ms = new MemoryStream();
    ms.Write(before);
    ms.Position = 0;

    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();

    // Round 1: add d.txt
    var dContent = Encoding.UTF8.GetBytes("hello D");
    d.Add(ms, [ArchiveInputInfo.InMemory("d.txt", dContent)]);

    // Round 2: replace a.txt via the in-place modifier directly
    var aReplaced = Encoding.UTF8.GetBytes("A is replaced");
    FileSystem.Yaffs2.Yaffs2InPlaceModifier.Replace(ms, "a.txt", aReplaced);

    // Round 3: remove c.txt
    d.Remove(ms, ["c.txt"]);

    // Extract every live file and check bytes.
    ms.Position = 0;
    Assert.That(d.ExtractEntryToMemory(ms, "a.txt", null), Is.EqualTo(aReplaced),
      "a.txt extracts to the replaced bytes");
    ms.Position = 0;
    Assert.That(d.ExtractEntryToMemory(ms, "b.txt", null), Is.EqualTo("FILE-B-content"u8.ToArray()),
      "b.txt was never touched — extracts to its original bytes");
    ms.Position = 0;
    Assert.That(d.ExtractEntryToMemory(ms, "d.txt", null), Is.EqualTo(dContent),
      "d.txt extracts to the bytes we added");

    // c.txt is gone — OpenEntry returns an empty BoundedEntryStream per the
    // descriptor's contract for unknown names.
    ms.Position = 0;
    using var cStream = d.OpenEntry(ms, "c.txt", null);
    Assert.That(cStream.Length, Is.EqualTo(0),
      "tombstoned c.txt resolves to an empty stream — object treated as gone");
  }

  // ── ErrorHandling: replace/remove of missing entry throws ────────────

  [Test, Category("ErrorHandling"), Category("InPlaceModify")]
  public void Replace_MissingFile_Throws() {
    var before = BuildThreeFileImage(out _, out _);
    using var ms = new MemoryStream();
    ms.Write(before);
    ms.Position = 0;

    Assert.That(
      () => FileSystem.Yaffs2.Yaffs2InPlaceModifier.Replace(ms, "no-such.txt", [1, 2, 3]),
      Throws.TypeOf<InvalidOperationException>());
  }

  [Test, Category("ErrorHandling"), Category("InPlaceModify")]
  public void Remove_MissingFile_DirectModifierThrows() {
    // The descriptor swallows missing-name Remove (matching rebuild-path behavior),
    // but the modifier itself throws so direct callers fail fast.
    var before = BuildThreeFileImage(out _, out _);
    using var ms = new MemoryStream();
    ms.Write(before);
    ms.Position = 0;

    Assert.That(
      () => FileSystem.Yaffs2.Yaffs2InPlaceModifier.Remove(ms, "no-such.txt"),
      Throws.TypeOf<InvalidOperationException>());
  }

  // ── Nested path on Add is honestly rejected ──────────────────────────

  [Test, Category("ErrorHandling"), Category("InPlaceModify")]
  public void Add_NestedPath_ThrowsNotSupported() {
    var before = BuildThreeFileImage(out _, out _);
    using var ms = new MemoryStream();
    ms.Write(before);
    ms.Position = 0;

    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    Assert.That(
      () => d.Add(ms, [ArchiveInputInfo.InMemory("sub/nested.txt", "x"u8.ToArray())]),
      Throws.TypeOf<NotSupportedException>().With.Message.Contains("nested"));
  }
}
