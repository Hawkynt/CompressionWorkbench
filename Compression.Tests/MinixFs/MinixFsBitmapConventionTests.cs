#pragma warning disable CS1591
using Compression.Tests.Support;
using FileSystem.MinixFs;

namespace Compression.Tests.MinixFs;

/// <summary>
/// The inode and zone bitmaps have to be the ones a minix system reads, not
/// merely the ones our own writer and modifier agreed on with each other.
/// </summary>
/// <remarks>
/// <para>They were not. Inode N belongs at bit N of the inode bitmap — bit 0 is
/// reserved for the inode number that means "none" — and every volume we wrote
/// put it at bit N-1, so the last inode was never marked at all and
/// <c>fsck.minix</c> said so about every image this project had ever produced.
/// The zone bitmap covers the data zones only and counts from the first of them,
/// and we marked absolute zone numbers instead.</para>
///
/// <para>The zone half stayed hidden because our writer sizes a volume to fit
/// exactly. With every zone in use, "all bits set" is right by accident whichever
/// way they are counted; the mismatch only appears once something is free. So
/// the check below makes free space first and then asks.</para>
///
/// <para>The convention is not read off a document here — it is read off
/// <c>mkfs.minix</c>, which leaves bits 0 and 1 set in both maps on a fresh
/// volume of v1, v2 and v3 alike: the reserved bit, and the root.</para>
/// </remarks>
[TestFixture]
public class MinixFsBitmapConventionTests {

  private static byte[] Solid(int length, int seed) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)(i * seed + 7);
    return data;
  }

  private static Dictionary<string, byte[]> Files() {
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    var seeds = new[] { 17, 19, 23, 29, 31, 37 };
    for (var i = 0; i < seeds.Length; ++i) files[$"F{i}.BIN"] = Solid(4_000, seeds[i]);
    return files;
  }

  private static byte[] Build(Dictionary<string, byte[]> files) {
    using var ms = new MemoryStream();
    using (var writer = new MinixFsWriter(ms, leaveOpen: true)) {
      foreach (var (name, data) in files) writer.AddFile(name, data);
      writer.Finish();
    }
    return ms.ToArray();
  }

  private static void AssertFsckAccepts(byte[] image, string what) {
    var path = Path.Combine(Path.GetTempPath(), "cwb_mfs_" + Guid.NewGuid().ToString("N")[..8] + ".img");
    File.WriteAllBytes(path, image);
    try {
      var checker = ThirdPartyFsCheck.Fsck("MinixFs", path);
      if (!checker.Ran) {
        Assert.Ignore($"fsck.minix is not installed: {checker.Detail}");
        return;
      }

      Assert.That(checker.Ok, Is.True, $"fsck.minix rejected {what}: {checker.Detail}");
    } finally {
      try { File.Delete(path); } catch { /* the scratch image is already gone */ }
    }
  }

  [Test, Category("Interop")]
  public void AFreshVolume_PassesItsOwnChecker() {
    AssertFsckAccepts(Build(Files()), "a volume we had just written");
  }

  [Test, Category("Interop")]
  public void AVolumeWithFreeSpace_PassesItsOwnChecker() {
    // Removing a file and adding a smaller one leaves a zone spare. That is the
    // only state in which the zone bitmap's counting can be seen to be wrong:
    // on a volume with nothing free, every bit is set either way.
    var files = Files();
    using var image = new MemoryStream(Build(files));
    Assert.That(MinixFsInPlaceModifier.RemoveFile(image, "F2.BIN", wipeData: true), Is.True);
    MinixFsInPlaceModifier.AddFile(image, "NEW.BIN", Solid(3_000, 41));

    image.Position = 0;
    using (var reader = new MinixFsReader(image, leaveOpen: true)) {
      var names = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
      Assert.That(names, Does.Not.Contain("F2.BIN"));
      Assert.That(names, Does.Contain("NEW.BIN"));
    }

    AssertFsckAccepts(image.ToArray(), "a volume with a zone free in it");
  }

  [Test, Category("Regression")]
  public void TheInodeBitmapCountsFromOne() {
    // A fresh volume leaves bit 0 set for the reserved inode number and one bit
    // per inode above it. What this guards is the top of the range: the last
    // inode's bit used to fall off the end, so every volume had exactly one
    // inode in use that the bitmap called free.
    var files = Files();
    var image = Build(files);

    const int imapOffset = 2 * 1024;
    bool Bit(int index) => (image[imapOffset + index / 8] & (1 << (index % 8))) != 0;

    // Root plus one inode per file, and nothing above them.
    var inodes = files.Count + 1;
    Assert.That(Bit(0), Is.True, "bit 0 is the reserved inode number and is always set");
    for (var inode = 1; inode <= inodes; ++inode)
      Assert.That(Bit(inode), Is.True, $"inode {inode} is in use and its bit should say so");
    Assert.That(Bit(inodes + 1), Is.False, "nothing above the last inode is in use");
  }
}
