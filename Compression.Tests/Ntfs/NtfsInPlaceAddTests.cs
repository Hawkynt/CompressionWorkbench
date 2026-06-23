#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Ntfs;

namespace Compression.Tests.Ntfs;

/// <summary>
/// Genuine in-place NTFS add (<see cref="NtfsInPlaceAdder"/>): a file is inserted by
/// claiming a free MFT record slot, writing a FILE record (resident or non-resident
/// $DATA), allocating clusters from $Bitmap, setting the $MFT:$BITMAP bit and inserting
/// a collation-sorted root $INDEX_ROOT entry — existing files, their MFT records and
/// clusters stay byte-identical (no whole-image re-pack). The
/// <see cref="NtfsInPlaceExternalTests"/> companion proves ntfs-3g (ntfsls/ntfscat/
/// ntfsfix) accepts the result.
/// </summary>
[TestFixture]
public class NtfsInPlaceAddTests {

  private static byte[] BuildSeed(params (string Name, byte[] Data)[] files) {
    var w = new NtfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build(16 * 1024 * 1024);
  }

  private static (List<string> Names, Dictionary<string, byte[]> Content) Read(byte[] image) {
    using var ms = new MemoryStream(image, false);
    var r = new NtfsReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    return (files.Select(e => e.Name).OrderBy(n => n).ToList(),
            files.ToDictionary(e => e.Name, e => r.Extract(e)));
  }

  [Test]
  public void ResidentAdd_RoundTripsAndPreservesExisting() {
    var seedData = Encoding.ASCII.GetBytes("SEED-CONTENT-0123456789");
    var image = BuildSeed(("seed.txt", seedData));
    var before = (byte[])image.Clone();

    NtfsInPlaceAdder.AddFile(image, "added.txt", Encoding.ASCII.GetBytes("HELLO-IN-PLACE"));

    var (names, content) = Read(image);
    Assert.Multiple(() => {
      Assert.That(names, Is.EqualTo(new[] { "added.txt", "seed.txt" }));
      Assert.That(content["added.txt"], Is.EqualTo(Encoding.ASCII.GetBytes("HELLO-IN-PLACE")));
      Assert.That(content["seed.txt"], Is.EqualTo(seedData), "existing file content must survive");
      Assert.That(image.Length, Is.EqualTo(before.Length), "in-place add must not resize the image");
      Assert.That(image.AsSpan(0, 512).SequenceEqual(before.AsSpan(0, 512)), Is.True, "boot sector preserved");
      var changed = 0;
      for (var i = 0; i < image.Length; i++) if (image[i] != before[i]) changed++;
      Assert.That(changed, Is.LessThan(4096), $"in-place add changed {changed} bytes — should be tiny, not a re-pack");
    });
  }

  [Test]
  public void NonResidentAdd_LargeFile_RoundTrips() {
    var image = BuildSeed(("seed.txt", Encoding.ASCII.GetBytes("seed")));
    var big = new byte[9000];
    new Random(7).NextBytes(big);

    NtfsInPlaceAdder.AddFile(image, "big.bin", big);

    var (names, content) = Read(image);
    Assert.That(names, Does.Contain("big.bin"));
    Assert.That(content["big.bin"], Is.EqualTo(big), "non-resident data must round-trip byte-identical");
  }

  [Test]
  public void ReplaceByName_OverwritesWithoutDuplicate() {
    var image = BuildSeed(("seed.txt", Encoding.ASCII.GetBytes("seed")));
    NtfsInPlaceAdder.AddFile(image, "doc.txt", Encoding.ASCII.GetBytes("v1"));
    NtfsInPlaceAdder.AddFile(image, "doc.txt", Encoding.ASCII.GetBytes("v2-longer"));

    var (names, content) = Read(image);
    Assert.That(names.Count(n => n == "doc.txt"), Is.EqualTo(1), "replace must not duplicate");
    Assert.That(content["doc.txt"], Is.EqualTo(Encoding.ASCII.GetBytes("v2-longer")));
  }

  [Test]
  public void Remove_FreesClusters_ThenInPlaceAddReusesThem() {
    // drop.bin occupies data clusters; after remove those clusters + its MFT record are
    // freed in $Bitmap/$MFT:$BITMAP, so a subsequent in-place add can reuse the space
    // (genuine free, not a leak) and the image does not grow.
    var image = BuildSeed(("keep.txt", Encoding.ASCII.GetBytes("keep")),
                          ("drop.bin", RandomBytes(8000, 3)));
    var lengthBefore = image.Length;
    NtfsRemover.Remove(image, "drop.bin");
    NtfsInPlaceAdder.AddFile(image, "new.bin", RandomBytes(6000, 9));

    var (names, content) = Read(image);
    Assert.Multiple(() => {
      Assert.That(names, Is.EqualTo(new[] { "keep.txt", "new.bin" }), "drop gone, new added, keep survives");
      Assert.That(content["new.bin"], Is.EqualTo(RandomBytes(6000, 9)));
      Assert.That(image.Length, Is.EqualTo(lengthBefore), "remove+add must not grow the image");
    });
  }

  private static byte[] RandomBytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

  [Test]
  public void NestedPath_ThrowsForRebuildFallback() {
    var image = BuildSeed(("seed.txt", Encoding.ASCII.GetBytes("seed")));
    Assert.Throws<NotSupportedException>(() => NtfsInPlaceAdder.AddFile(image, "sub/file.txt", new byte[] { 1 }));
  }

  [Test]
  public void DescriptorAdd_UsesInPlace_AndIsModifiable() {
    var d = new NtfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);

    var image = BuildSeed(("seed.txt", Encoding.ASCII.GetBytes("seed")));
    var before = (byte[])image.Clone();
    using var stream = new MemoryStream();
    stream.Write(image); stream.Position = 0;
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, Encoding.ASCII.GetBytes("descriptor-add"));
      ((IArchiveModifiable)d).Add(stream, [new ArchiveInputInfo(tmp, "viaDesc.txt", false)]);
    } finally { File.Delete(tmp); }

    var result = stream.ToArray();
    var (names, content) = Read(result);
    Assert.That(names, Does.Contain("viaDesc.txt").And.Contain("seed.txt"));
    Assert.That(content["viaDesc.txt"], Is.EqualTo(Encoding.ASCII.GetBytes("descriptor-add")));
    // Proof it took the in-place path (not the re-pack fallback): the image kept its
    // length and only a tiny fraction of bytes changed.
    Assert.That(result.Length, Is.EqualTo(before.Length));
    var changed = 0;
    for (var i = 0; i < result.Length; i++) if (result[i] != before[i]) changed++;
    Assert.That(changed, Is.LessThan(4096), $"descriptor Add re-packed ({changed} bytes changed) instead of in-place");
  }
}
