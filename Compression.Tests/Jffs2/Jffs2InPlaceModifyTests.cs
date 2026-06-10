using System.Text;
using Compression.Registry;
using FileSystem.Jffs2;

namespace Compression.Tests.Jffs2;

/// <summary>
/// True in-place R/W mutation of a JFFS2 image. Each operation appends a
/// fresh node (inode or dirent) at the end of the log; existing nodes must
/// stay byte-identical at their original offsets. The reader then resolves
/// names and inodes to the highest-version node, so the new content (or an
/// unlink) becomes visible without rewriting anything in the prefix of the
/// stream.
/// </summary>
[TestFixture]
public class Jffs2InPlaceModifyTests {

  // ── Fixture helpers ──────────────────────────────────────────────────

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new Jffs2Writer();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  /// <summary>
  /// Scans for the offset immediately past the last live node — i.e. where a
  /// fresh append would land. Mirrors the modifier's own log-end detection.
  /// </summary>
  private static int FindEndOfLogOffset(byte[] image) {
    var off = 0;
    var lastEnd = 0;
    while (off + 12 <= image.Length) {
      var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(off, 2));
      if (magic != 0x1985) {
        if (image[off] == 0xFF) break;
        off += 4;
        continue;
      }
      var totLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 4, 4));
      if (totLen < 12 || off + totLen > image.Length) { off += 4; continue; }
      var aligned = ((int)totLen + 3) & ~3;
      lastEnd = off + aligned;
      off += aligned;
    }
    return lastEnd;
  }

  private static byte[] ExtractByName(byte[] image, string name) {
    var reader = new Jffs2FileReader(image);
    var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name == name);
    Assert.That(entry, Is.Not.Null, $"Entry '{name}' should exist after mutation.");
    return reader.Extract(entry!);
  }

  // ── Descriptor contract ──────────────────────────────────────────────

  [Test, Category("Spec")]
  public void Descriptor_StillAdvertisesCanModify() {
    var d = new Jffs2FormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
  }

  // ── Add: brand new file ──────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_NewFile_AppendsAtTail_PrefixUnchanged() {
    var original = BuildImage(("alpha.txt", "alpha-content"u8.ToArray()));
    var endOfLog = FindEndOfLogOffset(original);
    var prefixSnapshot = original.AsSpan(0, endOfLog).ToArray();

    using var ms = new MemoryStream();
    ms.Write(original);

    var d = new Jffs2FormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("beta.txt", "beta-content"u8.ToArray())]);

    var mutated = ms.ToArray();

    // The original log prefix bytes must be byte-identical.
    Assert.That(mutated.AsSpan(0, endOfLog).ToArray(), Is.EqualTo(prefixSnapshot),
      "Existing log prefix must stay byte-identical under in-place append.");

    // Both files must read back.
    Assert.That(Encoding.UTF8.GetString(ExtractByName(mutated, "alpha.txt")), Is.EqualTo("alpha-content"));
    Assert.That(Encoding.UTF8.GetString(ExtractByName(mutated, "beta.txt")), Is.EqualTo("beta-content"));
  }

  // ── Replace: existing file ───────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Replace_ExistingFile_OldNodesStayByteIdentical_NewContentWins() {
    var original = BuildImage(("doc.txt", "first-version"u8.ToArray()));
    var endOfLog = FindEndOfLogOffset(original);
    var prefixSnapshot = original.AsSpan(0, endOfLog).ToArray();

    using var ms = new MemoryStream();
    ms.Write(original);

    var d = new Jffs2FormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("doc.txt", "second-version!"u8.ToArray())]);

    var mutated = ms.ToArray();

    Assert.That(mutated.AsSpan(0, endOfLog).ToArray(), Is.EqualTo(prefixSnapshot),
      "Replacing a file must leave the original inode node byte-identical at its original offset.");

    var actual = Encoding.UTF8.GetString(ExtractByName(mutated, "doc.txt"));
    Assert.That(actual, Is.EqualTo("second-version!"),
      "Reader must resolve to the highest-version inode node.");
  }

  // ── Remove: unlink dirent ────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Remove_File_OldNodesStayByteIdentical_ReaderTreatsFileAsGone() {
    var original = BuildImage(
      ("keep.txt", "keep-me"u8.ToArray()),
      ("drop.txt", "drop-me"u8.ToArray()));
    var endOfLog = FindEndOfLogOffset(original);
    var prefixSnapshot = original.AsSpan(0, endOfLog).ToArray();

    using var ms = new MemoryStream();
    ms.Write(original);

    var d = new Jffs2FormatDescriptor();
    d.Remove(ms, ["drop.txt"]);

    var mutated = ms.ToArray();

    Assert.That(mutated.AsSpan(0, endOfLog).ToArray(), Is.EqualTo(prefixSnapshot),
      "Removing a file must leave all original nodes byte-identical at their original offsets.");

    var reader = new Jffs2FileReader(mutated);
    var names = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("keep.txt"));
    Assert.That(names, Does.Not.Contain("drop.txt"),
      "Removed file must not surface — reader sees the highest-version dirent with ino=0.");

    Assert.That(Encoding.UTF8.GetString(ExtractByName(mutated, "keep.txt")), Is.EqualTo("keep-me"));
  }

  // ── Add-Remove-Add roundtrip ─────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void AddRemoveAdd_Roundtrip_ResolvesToLatestState() {
    var original = BuildImage(("seed.txt", "seed"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(original);

    var d = new Jffs2FormatDescriptor();

    // Add
    d.Add(ms, [ArchiveInputInfo.InMemory("new.txt", "v1"u8.ToArray())]);
    // Remove the same name
    d.Remove(ms, ["new.txt"]);
    // Add it back with different content
    d.Add(ms, [ArchiveInputInfo.InMemory("new.txt", "v2-after-remove"u8.ToArray())]);

    var mutated = ms.ToArray();
    var reader = new Jffs2FileReader(mutated);
    var byName = reader.Entries.Where(e => !e.IsDirectory)
                                 .ToDictionary(e => e.Name, reader.Extract);

    Assert.That(byName.ContainsKey("seed.txt"), Is.True, "seed.txt survives the round trip.");
    Assert.That(byName.ContainsKey("new.txt"), Is.True, "new.txt is present after remove+re-add.");
    Assert.That(Encoding.UTF8.GetString(byName["new.txt"]), Is.EqualTo("v2-after-remove"),
      "Reader resolves to the latest re-added content, not the removed version.");
  }

  // ── Multiple replaces don't corrupt earlier nodes ────────────────────

  [Test, Category("Boundary")]
  public void MultipleReplaces_AllEarlierNodesStayByteIdentical() {
    var original = BuildImage(("doc.txt", "v0"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(original);

    var d = new Jffs2FormatDescriptor();

    // Capture the prefix after each replacement and make sure it never shrinks
    // or rewrites prior bytes.
    var snapshots = new List<byte[]>();
    snapshots.Add(ms.ToArray());

    for (var i = 1; i <= 4; ++i) {
      d.Add(ms, [ArchiveInputInfo.InMemory("doc.txt", Encoding.UTF8.GetBytes($"v{i}"))]);
      snapshots.Add(ms.ToArray());
    }

    // For each later snapshot, the prior snapshot's live-log prefix must match.
    for (var i = 1; i < snapshots.Count; ++i) {
      var prev = snapshots[i - 1];
      var prevPrefix = FindEndOfLogOffset(prev);
      Assert.That(snapshots[i].AsSpan(0, prevPrefix).ToArray(),
        Is.EqualTo(prev.AsSpan(0, prevPrefix).ToArray()),
        $"After replacement #{i}, the prior log prefix must be byte-identical (no in-place node rewrites).");
    }

    // Final read shows the latest version only.
    var reader = new Jffs2FileReader(snapshots[^1]);
    var entry = reader.Entries.First(e => !e.IsDirectory && e.Name == "doc.txt");
    Assert.That(Encoding.UTF8.GetString(reader.Extract(entry)), Is.EqualTo("v4"));
  }

  // ── Mutate-then-extract end-to-end ──────────────────────────────────

  [Test, Category("RoundTrip")]
  public void MutateThenExtract_MatchesMutation() {
    var original = BuildImage(("a.txt", "A"u8.ToArray()), ("b.txt", "B"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(original);

    var d = new Jffs2FormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("c.txt", "C"u8.ToArray())]);
    d.Add(ms, [ArchiveInputInfo.InMemory("a.txt", "AAA"u8.ToArray())]);
    d.Remove(ms, ["b.txt"]);

    var mutated = ms.ToArray();
    var outDir = Path.Combine(Path.GetTempPath(), "jffs2_inplace_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      ms.Position = 0;
      d.Extract(ms, outDir, null, null);

      Assert.That(File.Exists(Path.Combine(outDir, "a.txt")), Is.True);
      Assert.That(File.ReadAllText(Path.Combine(outDir, "a.txt")), Is.EqualTo("AAA"),
        "Replaced file extracts to new content.");
      Assert.That(File.Exists(Path.Combine(outDir, "c.txt")), Is.True);
      Assert.That(File.ReadAllText(Path.Combine(outDir, "c.txt")), Is.EqualTo("C"));
      Assert.That(File.Exists(Path.Combine(outDir, "b.txt")), Is.False,
        "Removed file does not extract.");
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  // ── Boundary: Remove of unknown name is silently dropped ─────────────

  [Test, Category("Boundary")]
  public void Remove_UnknownName_DoesNotThrow_NoChange() {
    var original = BuildImage(("alpha.txt", "alpha"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(original);

    var d = new Jffs2FormatDescriptor();
    Assert.DoesNotThrow(() => d.Remove(ms, ["does-not-exist.txt"]));

    var reader = new Jffs2FileReader(ms.ToArray());
    Assert.That(reader.Entries.Any(e => !e.IsDirectory && e.Name == "alpha.txt"), Is.True,
      "Unknown-name removal must not affect existing entries.");
  }

  // ── Boundary: Add 0 inputs is a no-op ────────────────────────────────

  [Test, Category("Boundary")]
  public void Add_EmptyInputs_StreamUnchanged() {
    var original = BuildImage(("foo.txt", "foo"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(original);

    var d = new Jffs2FormatDescriptor();
    d.Add(ms, []);

    Assert.That(ms.ToArray(), Is.EqualTo(original),
      "Add with no inputs must leave the image byte-identical.");
  }
}
