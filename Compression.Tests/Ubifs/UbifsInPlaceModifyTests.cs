#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Ubifs;

namespace Compression.Tests.Ubifs;

/// <summary>
/// Tests that <see cref="UbifsInPlaceModifier"/> appends Add / Replace / Remove
/// nodes at the journal head while keeping every previously written byte at its
/// original offset — the kernel UBIFS invariant for committed nodes.
/// </summary>
[TestFixture]
public class UbifsInPlaceModifyTests {

  private static byte[] BuildBaseImage(params (string Name, string Content)[] files) {
    var writer = new UbifsWriter();
    foreach (var (name, content) in files)
      writer.AddFile(name, Encoding.UTF8.GetBytes(content));
    using var ms = new MemoryStream();
    writer.WriteTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Returns the position past the last UBIFS node header (= the journal-head
  /// 8-byte-aligned offset). Bytes before this are committed-node territory and
  /// must stay byte-identical across mutations.
  /// </summary>
  private static long FindFirstFreeOffset(ReadOnlySpan<byte> image) {
    var past = 0;
    for (var off = 0; off + 24 <= image.Length; ++off) {
      if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(off, 4)) != 0x06101831u) continue;
      var nodeLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(off + 16, 4));
      if (nodeLen < 24 || nodeLen > image.Length - off) continue;
      past = off + nodeLen;
      off += nodeLen - 1;
    }
    return (past + 7) & ~7;
  }

  // ── Add ───────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_AppendsFile_AndPreservesCommittedNodesByteIdentical() {
    var baseImage = BuildBaseImage(("a.txt", "alpha"));
    var firstFree = (int)FindFirstFreeOffset(baseImage);

    using var ms = new MemoryStream();
    ms.Write(baseImage, 0, baseImage.Length);
    ms.Position = 0;

    UbifsInPlaceModifier.AddFiles(ms, [
      ArchiveInputInfo.InMemory("b.txt", "beta"u8.ToArray()),
    ]);

    var mutated = ms.ToArray();

    // Every byte BEFORE firstFree is committed-node territory — must be identical.
    Assert.That(mutated.AsSpan(0, firstFree).ToArray(),
      Is.EqualTo(baseImage.AsSpan(0, firstFree).ToArray()),
      "Add must not rewrite any committed-node byte (UBIFS log-structured invariant).");

    // Reader must surface both files with correct content.
    var reader = new UbifsFileReader(mutated);
    var byName = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, e => e);
    Assert.That(byName.ContainsKey("a.txt"), Is.True);
    Assert.That(byName.ContainsKey("b.txt"), Is.True);
    Assert.That(Encoding.UTF8.GetString(reader.Extract(byName["a.txt"])), Is.EqualTo("alpha"));
    Assert.That(Encoding.UTF8.GetString(reader.Extract(byName["b.txt"])), Is.EqualTo("beta"));
  }

  [Test, Category("HappyPath")]
  public void Add_DescriptorSurface_RoundTrips() {
    var baseImage = BuildBaseImage(("a.txt", "alpha"));
    using var ms = new MemoryStream();
    ms.Write(baseImage, 0, baseImage.Length);

    var d = new UbifsFormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("c.txt", "gamma"u8.ToArray())]);

    ms.Position = 0;
    var entries = d.List(ms, null).Select(e => e.Name).ToList();
    Assert.That(entries, Does.Contain("a.txt"));
    Assert.That(entries, Does.Contain("c.txt"));

    ms.Position = 0;
    var extracted = d.ExtractEntryToMemory(ms, "c.txt", null);
    Assert.That(extracted, Is.EqualTo("gamma"u8.ToArray()));
  }

  // ── Replace ───────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Replace_KeepsOldDataNodes_ButReaderPicksHigherSqnumContent() {
    var baseImage = BuildBaseImage(("doc.txt", "original-content"));
    var firstFree = (int)FindFirstFreeOffset(baseImage);

    using var ms = new MemoryStream();
    ms.Write(baseImage, 0, baseImage.Length);

    UbifsInPlaceModifier.ReplaceFile(ms, "doc.txt", "REPLACED-PAYLOAD"u8.ToArray());

    var mutated = ms.ToArray();

    // Old DATA + INO nodes for the original-content payload must stay byte-identical.
    Assert.That(mutated.AsSpan(0, firstFree).ToArray(),
      Is.EqualTo(baseImage.AsSpan(0, firstFree).ToArray()),
      "Replace must not overwrite the original DATA / INO nodes — only append fresh ones.");

    var reader = new UbifsFileReader(mutated);
    var doc = reader.Entries.Single(e => !e.IsDirectory && e.Name == "doc.txt");
    Assert.That(Encoding.UTF8.GetString(reader.Extract(doc)), Is.EqualTo("REPLACED-PAYLOAD"));
  }

  [Test, Category("HappyPath")]
  public void Replace_ViaAdd_OnExistingName_ReusesInode() {
    // Add() on an existing leaf name routes through Replace — same inode #, fresh sqnum.
    var baseImage = BuildBaseImage(("doc.txt", "v1"));
    using var ms = new MemoryStream();
    ms.Write(baseImage, 0, baseImage.Length);

    UbifsInPlaceModifier.AddFiles(ms, [
      ArchiveInputInfo.InMemory("doc.txt", "v2-overwrite"u8.ToArray()),
    ]);

    var mutated = ms.ToArray();
    var reader = new UbifsFileReader(mutated);
    var docs = reader.Entries.Where(e => !e.IsDirectory && e.Name == "doc.txt").ToList();
    Assert.That(docs, Has.Count.EqualTo(1), "Replace-via-Add must not duplicate the dentry by leaf name.");
    Assert.That(Encoding.UTF8.GetString(reader.Extract(docs[0])), Is.EqualTo("v2-overwrite"));
  }

  // ── Remove ────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Remove_AppendsTombstoneDentry_OldNodesStayByteIdentical() {
    var baseImage = BuildBaseImage(("keep.txt", "stays"), ("drop.txt", "goes"));
    var firstFree = (int)FindFirstFreeOffset(baseImage);

    using var ms = new MemoryStream();
    ms.Write(baseImage, 0, baseImage.Length);

    UbifsInPlaceModifier.RemoveFiles(ms, ["drop.txt"]);

    var mutated = ms.ToArray();

    // Old DENT for drop.txt must stay byte-identical — Remove only appends a tombstone.
    Assert.That(mutated.AsSpan(0, firstFree).ToArray(),
      Is.EqualTo(baseImage.AsSpan(0, firstFree).ToArray()),
      "Remove must not overwrite the original DENT — only append a tombstone with inum=0.");

    var reader = new UbifsFileReader(mutated);
    var names = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("keep.txt"));
    Assert.That(names, Does.Not.Contain("drop.txt"),
      "Reader's last-sqnum-wins must surface the tombstone over the original dentry.");
  }

  [Test, Category("Sad")]
  public void Remove_UnknownName_IsNoOp() {
    var baseImage = BuildBaseImage(("only.txt", "data"));
    using var ms = new MemoryStream();
    ms.Write(baseImage, 0, baseImage.Length);

    UbifsInPlaceModifier.RemoveFiles(ms, ["nonexistent.bin"]);

    // No tombstone written → image bytes unchanged.
    Assert.That(ms.ToArray(), Is.EqualTo(baseImage),
      "Remove on a missing name must be a no-op (no tombstone needed).");
  }

  // ── End-to-end: mutate then extract ───────────────────────────────────────

  [Test, Category("Spec")]
  public void MutateThenExtract_AllOpsCompose() {
    var baseImage = BuildBaseImage(
      ("keep.txt", "still-here"),
      ("doc.txt", "v1-content"),
      ("drop.txt", "tombstone-me"));
    using var ms = new MemoryStream();
    ms.Write(baseImage, 0, baseImage.Length);

    var d = new UbifsFormatDescriptor();
    // Add a new file.
    d.Add(ms, [ArchiveInputInfo.InMemory("new.txt", "freshly-added"u8.ToArray())]);
    // Replace via Add on existing leaf name.
    d.Add(ms, [ArchiveInputInfo.InMemory("doc.txt", "v2-replaced"u8.ToArray())]);
    // Tombstone the drop entry.
    d.Remove(ms, ["drop.txt"]);

    ms.Position = 0;
    var entries = d.List(ms, null).Where(e => e.Name.EndsWith(".txt")).Select(e => e.Name).ToHashSet();
    Assert.That(entries, Does.Contain("keep.txt"));
    Assert.That(entries, Does.Contain("doc.txt"));
    Assert.That(entries, Does.Contain("new.txt"));
    Assert.That(entries, Does.Not.Contain("drop.txt"));

    ms.Position = 0;
    Assert.That(d.ExtractEntryToMemory(ms, "keep.txt", null), Is.EqualTo("still-here"u8.ToArray()));
    ms.Position = 0;
    Assert.That(d.ExtractEntryToMemory(ms, "doc.txt", null), Is.EqualTo("v2-replaced"u8.ToArray()));
    ms.Position = 0;
    Assert.That(d.ExtractEntryToMemory(ms, "new.txt", null), Is.EqualTo("freshly-added"u8.ToArray()));
  }

  // ── Boundary / error cases ────────────────────────────────────────────────

  [Test, Category("Sad")]
  public void Replace_UnknownName_Throws() {
    var baseImage = BuildBaseImage(("present.txt", "x"));
    using var ms = new MemoryStream();
    ms.Write(baseImage, 0, baseImage.Length);

    Assert.Throws<FileNotFoundException>(() =>
      UbifsInPlaceModifier.ReplaceFile(ms, "missing.txt", "y"u8.ToArray()));
  }

  [Test, Category("Sad")]
  public void Add_NonSeekableStream_ThrowsArgumentException() {
    var baseImage = BuildBaseImage(("a.txt", "x"));
    // Wrap in a non-seekable forward-only stream.
    using var forward = new ForwardOnlyStream(baseImage);
    Assert.Throws<ArgumentException>(() =>
      UbifsInPlaceModifier.AddFiles(forward, [ArchiveInputInfo.InMemory("b.txt", [0x01])]));
  }

  [Test, Category("HappyPath")]
  public void Add_LargeFile_StraddlingLebBoundary_RoundTrips() {
    // Force multi-block / multi-LEB append path.
    var baseImage = BuildBaseImage(("a.txt", "tiny"));
    using var ms = new MemoryStream();
    ms.Write(baseImage, 0, baseImage.Length);

    // 24 KiB of varied content → 6 DATA nodes after compression.
    var big = new byte[24 * 1024];
    for (var i = 0; i < big.Length; ++i)
      big[i] = (byte)((i * 31) ^ (i >> 7));

    UbifsInPlaceModifier.AddFiles(ms, [ArchiveInputInfo.InMemory("big.bin", big)]);

    var reader = new UbifsFileReader(ms.ToArray());
    var entry = reader.Entries.Single(e => e.Name == "big.bin");
    var extracted = reader.Extract(entry);
    Assert.That(extracted, Is.EqualTo(big));
  }

  /// <summary>Read-only-seek wrapper used to assert <see cref="UbifsInPlaceModifier"/> rejects non-seekable streams.</summary>
  private sealed class ForwardOnlyStream(byte[] data) : Stream {
    private readonly MemoryStream _inner = new(data, writable: false);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => this._inner.Length;
    public override long Position { get => this._inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => this._inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
