using Compression.Registry;
using FileSystem.F2fs;

namespace Compression.Tests.F2fs;

/// <summary>
/// Exercises the F2FS modifier paths that the post-leaf-only iteration unlocked:
/// regular (block-based) dentry blocks after inline-region overflow, NAT/SIT
/// journal overflow falling through to on-disk entries, and mixed add/remove
/// patterns across both storage layouts. Self-round-trip only — external
/// <c>fsck.f2fs</c> validation lives in <see cref="F2fsPostMutationExternalTests"/>.
/// </summary>
[TestFixture]
public class F2fsFullScopeMutationTests {

  private static MemoryStream BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new F2fsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;
    return ms;
  }

  private static Dictionary<string, byte[]> ReadAll(MemoryStream image) {
    image.Position = 0;
    var r = new F2fsReader(image);
    return r.Entries
            .Where(e => !e.IsDirectory)
            .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
  }

  // ── Given ──────────────────────────────────────────────────────────────
  // a fresh F2FS image with one seed file.
  // ── When ───────────────────────────────────────────────────────────────
  // 200 root-level files are added — past both the 38-entry NAT journal
  // capacity and the 182-slot inline-dentry capacity, forcing both the
  // journal-overflow-falls-through path AND the inline-to-regular-dentry
  // conversion path.
  // ── Then ──────────────────────────────────────────────────────────────
  // every Add succeeds and the reader sees all 201 files with intact bytes.
  [Test, Category("RoundTrip")]
  public void Add_PastInlineAndJournalCapacity_RoundTrips() {
    using var img = BuildImage(("seed.txt", "s"u8.ToArray()));
    var m = (IArchiveModifiable)new F2fsFormatDescriptor();

    const int extraFiles = 200;
    for (var i = 0; i < extraFiles; ++i)
      m.Add(img, [ArchiveInputInfo.InMemory($"x{i:D4}.bin",
        new byte[] { (byte)(i & 0xFF), (byte)((i >> 8) & 0xFF), 0xAB })]);

    var files = ReadAll(img);
    Assert.Multiple(() => {
      Assert.That(files, Has.Count.EqualTo(extraFiles + 1));
      Assert.That(files["seed.txt"], Is.EqualTo("s"u8.ToArray()));
      for (var i = 0; i < extraFiles; ++i) {
        var name = $"x{i:D4}.bin";
        var expected = new byte[] { (byte)(i & 0xFF), (byte)((i >> 8) & 0xFF), 0xAB };
        Assert.That(files[name], Is.EqualTo(expected), $"{name} bytes intact");
      }
    });
  }

  // ── Given ──────────────────────────────────────────────────────────────
  // a fresh F2FS image with one seed file and 250 added entries (well past
  // the inline-dentry conversion threshold).
  // ── When ───────────────────────────────────────────────────────────────
  // every other added file is removed (125 removals) and another 50 entries
  // are added afterwards.
  // ── Then ──────────────────────────────────────────────────────────────
  // the surviving 125 originals AND the 50 new ones all read back correctly,
  // and the removed files are gone — exercises Remove against regular dentry
  // blocks, then add-into-existing-block, then add-via-new-allocation.
  [Test, Category("RoundTrip")]
  public void Mixed_AddRemoveAdd_AcrossBlockDentry_RoundTrips() {
    using var img = BuildImage(("seed.txt", "s"u8.ToArray()));
    var m = (IArchiveModifiable)new F2fsFormatDescriptor();

    // Seed past inline conversion.
    var initial = new List<string>();
    for (var i = 0; i < 250; ++i) {
      var n = $"a{i:D4}.dat";
      m.Add(img, [ArchiveInputInfo.InMemory(n, new byte[] { (byte)i, 0xCD })]);
      initial.Add(n);
    }

    // Remove the even-indexed ones.
    var toRemove = initial.Where((_, i) => i % 2 == 0).ToArray();
    m.Remove(img, toRemove);

    // Add more afterwards (should reuse a regular dentry block slot or allocate a new one).
    for (var i = 0; i < 50; ++i)
      m.Add(img, [ArchiveInputInfo.InMemory($"b{i:D4}.dat", new byte[] { (byte)i, 0xEE })]);

    var files = ReadAll(img);
    Assert.Multiple(() => {
      // seed + 125 surviving "a" files + 50 "b" files.
      Assert.That(files, Has.Count.EqualTo(1 + 125 + 50));
      Assert.That(files.ContainsKey("seed.txt"), Is.True);
      // Removed names absent.
      foreach (var name in toRemove)
        Assert.That(files.ContainsKey(name), Is.False, $"{name} removed");
      // Surviving "a" names present.
      foreach (var name in initial.Where((_, i) => i % 2 != 0))
        Assert.That(files.ContainsKey(name), Is.True, $"{name} survives remove");
      // Newly added "b" names present.
      for (var i = 0; i < 50; ++i) {
        var n = $"b{i:D4}.dat";
        Assert.That(files[n], Is.EqualTo(new byte[] { (byte)i, 0xEE }), $"{n} bytes intact");
      }
    });
  }

  // ── Given ──────────────────────────────────────────────────────────────
  // a fresh F2FS image holding files large enough that 10 Adds use ~200
  // data blocks (each file uses ~20 blocks via per-byte randomization).
  // ── When ───────────────────────────────────────────────────────────────
  // 50 such files are added — well past the NAT journal's 38-entry capacity.
  // ── Then ──────────────────────────────────────────────────────────────
  // every Add succeeds and the reader returns every file's exact bytes.
  [Test, Category("RoundTrip")]
  public void Add_ManyMediumFiles_ExceedsNatJournal_RoundTrips() {
    using var img = BuildImage(("seed.txt", "s"u8.ToArray()));
    var m = (IArchiveModifiable)new F2fsFormatDescriptor();

    var rnd = new Random(1234);
    var added = new Dictionary<string, byte[]>();
    for (var i = 0; i < 50; ++i) {
      var data = new byte[4096 * 4]; // 4 blocks per file.
      rnd.NextBytes(data);
      var name = $"med{i:D3}.bin";
      m.Add(img, [ArchiveInputInfo.InMemory(name, data)]);
      added[name] = data;
    }

    var files = ReadAll(img);
    Assert.Multiple(() => {
      Assert.That(files, Has.Count.EqualTo(added.Count + 1));
      foreach (var (name, data) in added)
        Assert.That(files[name], Is.EqualTo(data), $"{name} bytes intact");
    });
  }

  // ── Given ──────────────────────────────────────────────────────────────
  // a fresh F2FS image with a seed file.
  // ── When ───────────────────────────────────────────────────────────────
  // 200 files are added (forcing inline→block conversion), then all 200 are
  // removed, then 200 fresh ones are added again.
  // ── Then ──────────────────────────────────────────────────────────────
  // the final image contains the seed + the second wave of 200, with no
  // first-wave bytes leaking through (Remove wipes the data blocks).
  [Test, Category("RoundTrip")]
  public void AddThenRemoveAllThenAdd_AcrossConversionBoundary_RoundTrips() {
    using var img = BuildImage(("seed.txt", "s"u8.ToArray()));
    var m = (IArchiveModifiable)new F2fsFormatDescriptor();

    var firstWave = new List<string>();
    for (var i = 0; i < 200; ++i) {
      var n = $"wave1-{i:D4}";
      m.Add(img, [ArchiveInputInfo.InMemory(n, new byte[] { (byte)i, 0x11 })]);
      firstWave.Add(n);
    }

    m.Remove(img, firstWave.ToArray());

    for (var i = 0; i < 200; ++i) {
      var n = $"wave2-{i:D4}";
      m.Add(img, [ArchiveInputInfo.InMemory(n, new byte[] { (byte)i, 0x22 })]);
    }

    var files = ReadAll(img);
    Assert.Multiple(() => {
      Assert.That(files, Has.Count.EqualTo(1 + 200));
      Assert.That(files["seed.txt"], Is.EqualTo("s"u8.ToArray()));
      foreach (var n in firstWave)
        Assert.That(files.ContainsKey(n), Is.False, $"{n} gone");
      for (var i = 0; i < 200; ++i) {
        var n = $"wave2-{i:D4}";
        Assert.That(files[n], Is.EqualTo(new byte[] { (byte)i, 0x22 }));
      }
    });
  }
}
