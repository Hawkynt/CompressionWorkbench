using System.Buffers.Binary;
using System.Text;
using FileSystem.Ocfs2;

namespace Compression.Tests.Ocfs2;

/// <summary>
/// In-place edits in extent-backed directories: entries are found, added, replaced and removed
/// in the directory's 4 KiB blocks, a full directory takes one more cluster, and a full inline
/// directory is expanded into its first block. The kernel and fsck.ocfs2 judge the same edits in
/// <see cref="Ocfs2KernelMountTests"/>.
/// </summary>
[TestFixture]
public sealed class Ocfs2ExtentDirectoryEditTests {

  private const int Id2Offset = 0xC0;

  private static Dictionary<string, byte[]> Many(int count, string prefix = "many/") {
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var i = 0; i < count; ++i) files[$"{prefix}file-with-a-longish-name-{i:D4}.txt"] = Encoding.ASCII.GetBytes($"payload {i}\n");
    return files;
  }

  private static MemoryStream Build(Dictionary<string, byte[]> files) {
    var w = new Ocfs2Writer();
    w.SetMinimumSize(32L * 1024 * 1024);
    foreach (var (name, data) in files) w.AddFile(name, data);
    var ms = new MemoryStream();
    ms.Write(w.Build());
    return ms;
  }

  private static Dictionary<string, byte[]> Read(MemoryStream ms)
    => Ocfs2Reader.ReadFiles(ms.ToArray()).ToDictionary(static f => f.Name, static f => f.Data, StringComparer.Ordinal);

  private static void AssertTree(MemoryStream ms, Dictionary<string, byte[]> expected) {
    var seen = Read(ms);
    Assert.That(seen.Keys.Order(StringComparer.Ordinal), Is.EqualTo(expected.Keys.Order(StringComparer.Ordinal)));
    foreach (var (name, data) in expected) Assert.That(seen[name], Is.EqualTo(data), name);
  }

  /// <summary>The directory dinode at <paramref name="path"/> of the volume, read through the root.</summary>
  private static byte[] DirDinode(MemoryStream ms, string path) {
    var image = ms.ToArray();
    var blkno = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(Ocfs2Writer.SuperBlockBlkno * 4096 + Id2Offset + 0x28));   // s_root_blkno
    foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
      var dinode = image.AsSpan((int)(blkno * 4096), 4096).ToArray();
      blkno = FindChild(image, dinode, part);
    }
    return image.AsSpan((int)(blkno * 4096), 4096).ToArray();
  }

  private static long FindChild(byte[] image, byte[] dinode, string name) {
    var inline = (BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(0x76)) & 1) != 0;
    IEnumerable<(byte[] Area, int Start, int End)> Areas() {
      if (inline) { yield return (dinode, Id2Offset + 8, 4096); yield break; }
      var used = BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(Id2Offset + 4));
      for (var r = 0; r < used; ++r) {
        var rec = Id2Offset + 0x10 + r * 16;
        var clusters = BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(rec + 4));
        var start = (long)BinaryPrimitives.ReadUInt64LittleEndian(dinode.AsSpan(rec + 8));
        for (var c = 0; c < clusters; ++c) yield return (image.AsSpan((int)((start + c) * 4096), 4096).ToArray(), 0, 4096);
      }
    }
    var wanted = Encoding.UTF8.GetBytes(name);
    foreach (var (area, start, end) in Areas())
      for (var at = start; at + 12 <= end;) {
        var recLen = BinaryPrimitives.ReadUInt16LittleEndian(area.AsSpan(at + 8));
        if (recLen < 12) break;
        var inode = (long)BinaryPrimitives.ReadUInt64LittleEndian(area.AsSpan(at));
        if (inode != 0 && area[at + 10] == wanted.Length && area.AsSpan(at + 12, wanted.Length).SequenceEqual(wanted)) return inode;
        at += recLen;
      }
    throw new AssertionException($"no entry {name}");
  }

  private static bool IsInline(byte[] dinode) => (BinaryPrimitives.ReadUInt16LittleEndian(dinode.AsSpan(0x76)) & 1) != 0;
  private static long Size(byte[] dinode) => (long)BinaryPrimitives.ReadUInt64LittleEndian(dinode.AsSpan(0x20));

  [Test, Category("HappyPath")]
  public void GivenAnExtentBackedDirectory_WhenFilesAreAddedReplacedAndRemoved_ThenTheTreeReadsBack() {
    var files = Many(300);
    using var ms = Build(files);
    Assert.That(IsInline(DirDinode(ms, "many")), Is.False, "precondition: 300 names do not fit inline");

    Ocfs2InPlaceModifier.AddFile(ms, "many/added.txt", "added\n"u8.ToArray());
    Ocfs2InPlaceModifier.AddOrReplaceFile(ms, "many/file-with-a-longish-name-0007.txt", "replaced\n"u8.ToArray());
    Assert.That(Ocfs2InPlaceModifier.RemoveFile(ms, "many/file-with-a-longish-name-0000.txt"), Is.True);
    Assert.That(Ocfs2InPlaceModifier.RemoveFile(ms, "many/file-with-a-longish-name-0150.txt"), Is.True);
    Ocfs2InPlaceModifier.AddFile(ms, "many/sub/deeper/new.bin", new byte[10_000]);

    files["many/added.txt"] = "added\n"u8.ToArray();
    files["many/file-with-a-longish-name-0007.txt"] = "replaced\n"u8.ToArray();
    files.Remove("many/file-with-a-longish-name-0000.txt");
    files.Remove("many/file-with-a-longish-name-0150.txt");
    files["many/sub/deeper/new.bin"] = new byte[10_000];
    AssertTree(ms, files);
  }

  [Test, Category("HappyPath")]
  public void GivenAFullExtentBackedDirectory_WhenMoreFilesAreAdded_ThenItTakesAnotherBlock() {
    var files = Many(300);
    using var ms = Build(files);
    var before = Size(DirDinode(ms, "many"));
    for (var i = 0; i < 200; ++i) {
      var name = $"many/added-with-an-even-longer-name-to-fill-blocks-{i:D4}.txt";
      Ocfs2InPlaceModifier.AddFile(ms, name, Encoding.ASCII.GetBytes($"added {i}"));
      files[name] = Encoding.ASCII.GetBytes($"added {i}");
    }
    var dinode = DirDinode(ms, "many");
    Assert.That(Size(dinode), Is.GreaterThan(before), "the directory grew by whole blocks");
    Assert.That(Size(dinode) % 4096, Is.Zero);
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(dinode.AsSpan(0x14)) * 4096L, Is.EqualTo(Size(dinode)), "i_clusters follows i_size");
    AssertTree(ms, files);
  }

  [Test, Category("HappyPath")]
  public void GivenAFullInlineDirectory_WhenAnotherFileIsAdded_ThenItBecomesExtentBackedWithEveryName() {
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["small/seed.txt"] = "seed"u8.ToArray() };
    using var ms = Build(files);
    var i = 0;
    while (IsInline(DirDinode(ms, "small"))) {
      var name = $"small/name-number-{i:D4}.txt";
      Ocfs2InPlaceModifier.AddFile(ms, name, Encoding.ASCII.GetBytes($"n{i}"));
      files[name] = Encoding.ASCII.GetBytes($"n{i}");
      ++i;
      Assert.That(i, Is.LessThan(500), "the inline area should fill long before this");
    }
    var dinode = DirDinode(ms, "small");
    Assert.That(Size(dinode), Is.EqualTo(4096));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(dinode.AsSpan(0x14)), Is.EqualTo(1u));
    AssertTree(ms, files);
  }

  [Test, Category("Exception")]
  public void GivenAnIndexedDirectory_WhenEdited_ThenItIsRefusedAndTheImageUnchanged() {
    using var ms = Build(Many(300));
    var image = ms.ToArray();
    var root = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(Ocfs2Writer.SuperBlockBlkno * 4096 + Id2Offset + 0x28));
    var many = FindChild(image, image.AsSpan((int)(root * 4096), 4096).ToArray(), "many");
    var dyn = (int)(many * 4096 + 0x76);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(dyn), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(dyn)) | 0x0008));
    using var flagged = new MemoryStream();
    flagged.Write(image);
    Assert.Catch(() => Ocfs2InPlaceModifier.AddFile(flagged, "many/x.txt", [1]));
    Assert.Catch(() => Ocfs2InPlaceModifier.RemoveFile(flagged, "many/file-with-a-longish-name-0001.txt"));
    Assert.That(flagged.ToArray(), Is.EqualTo(image));
  }
}
