#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Nss;

namespace Compression.Tests.Nss;

/// <summary>
/// The NSS container this project writes carries the anchors a real pool
/// carries, and the layout pass moves the files inside it.
/// </summary>
/// <remarks>
/// The point of the anchors is that nothing about detection changes: an image
/// written here is found by the same scan that finds a real pool. The point of
/// the magic behind them is that nothing about a real pool changes either — it
/// has no files this can name, and the pass says so rather than guessing.
/// </remarks>
[TestFixture]
public class NssWriteAndDefragTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 29 + seed * 13) % 251);
    return data;
  }

  private static Dictionary<string, byte[]> Contents() {
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var k = 0; k < 5; ++k) files[$"FILE{k}.DAT"] = Payload(k, 1000 + k * 5000);
    return files;
  }

  private static byte[] Volume(Dictionary<string, byte[]> files) {
    var writer = new NssWriter();
    foreach (var (name, data) in files) writer.AddFile(name, data);
    return writer.Build();
  }

  private static void AssertReadsBack(byte[] image, IReadOnlyDictionary<string, byte[]> expected) {
    using var ms = new MemoryStream(image, writable: false);
    var volume = new NssVolume(ms);
    Assert.That(volume.Valid, Is.True, volume.Status);
    Assert.That(volume.Files.Select(f => f.Name), Is.EquivalentTo(expected.Keys));
    foreach (var file in volume.Files)
      Assert.That(volume.Read(file), Is.EqualTo(expected[file.Name]), $"{file.Name} must be intact");
  }

  /// <summary>
  /// Drops a file's entry the way removing it would, leaving its blocks
  /// unclaimed in the middle of the container.
  /// </summary>
  private static byte[] WithAHole(byte[] image, string name) {
    using var source = new MemoryStream(image, writable: false);
    var volume = new NssVolume(source);
    var kept = volume.Files.Where(f => f.Name != name).ToList();
    Assert.That(kept, Has.Count.EqualTo(volume.Files.Count - 1), $"{name} must have been there");

    var holed = (byte[])image.Clone();
    var writer = new NssWriter();
    // Rebuilt directory only — the payloads stay exactly where they are, which
    // is what leaves the gap.
    var cursor = (int)NssDirectoryStart;
    holed.AsSpan(cursor, 4096).Clear();
    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(holed.AsSpan(24), kept.Count);

    foreach (var file in kept) {
      var nameBytes = System.Text.Encoding.UTF8.GetBytes(file.Name);
      System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(holed.AsSpan(cursor), file.Offset);
      System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(holed.AsSpan(cursor + 8), file.Size);
      System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(holed.AsSpan(cursor + 16), (ushort)nameBytes.Length);
      nameBytes.CopyTo(holed.AsSpan(cursor + 18));
      cursor += 18 + nameBytes.Length;
    }

    _ = writer;
    return holed;
  }

  private const long NssDirectoryStart = 3 * 4096;

  /// <summary>
  /// A container this writes must not pass itself off as an NSS pool. NSS's
  /// object tree has no public spec, so nothing here can act as a pool; an
  /// image carrying a pool's anchors would be identified by anything that knows
  /// NSS and would then fail to read, which is worse than carrying none.
  /// </summary>
  [Test, Category("HappyPath")]
  public void AContainerWeWrite_CarriesNoAnchorOfARealPool() {
    var image = Volume(Contents());
    using var ms = new MemoryStream(image, writable: false);
    var reader = new NssReader(ms);

    Assert.Multiple(() => {
      Assert.That(reader.Headers.PoolFound, Is.False, "it must not claim to be a pool");
      Assert.That(reader.Headers.VolumeFound, Is.False, "nor carry a volume anchor");
      Assert.That(reader.Headers.SuperblockFound, Is.False, "nor a superblock anchor");
      Assert.That(reader.AnyValid, Is.False, "nothing here is a real NSS structure");
    });
  }

  /// <summary>
  /// None of the strings a scanner keys on may appear anywhere in what is
  /// written, not merely at the offsets a pool carries them.
  /// </summary>
  [Test, Category("HappyPath")]
  public void AContainerWeWrite_HasNoNovellMarkingAnywhereInIt() {
    var image = Volume(Contents());

    foreach (var marking in new[] {
               NssHeaders.NssPoolMagic, NssHeaders.NssVolumeMagic, NssHeaders.NssSuperblockMagic,
               NssHeaders.NovellMagic, NssHeaders.NetWareMagic,
             })
      Assert.That(image.AsSpan().IndexOf(marking), Is.LessThan(0),
                  $"'{System.Text.Encoding.ASCII.GetString(marking)}' must not appear");
  }

  /// <summary>Its own magic is what says whose container it is.</summary>
  [Test, Category("HappyPath")]
  public void AContainerWeWrite_IsFoundByItsOwnMagic() {
    var image = Volume(Contents());

    using var ms = new MemoryStream(image, writable: false);
    Assert.That(new NssVolume(ms).Valid, Is.True);
  }

  [Test, Category("HappyPath")]
  public void AContainerWeWrite_ReadsItsFilesBack() {
    var files = Contents();
    AssertReadsBack(Volume(files), files);
  }

  /// <summary>
  /// A pool this did not write has no container magic, so it has no files this
  /// can name — which is the state every NSS image was in before, and must
  /// stay in rather than being guessed at.
  /// </summary>
  [Test, Category("Sad")]
  public void ARealPoolIsNotMistakenForOneOfOurs() {
    var pool = new byte[64 * 1024];
    NssHeaders.NssPoolMagic.CopyTo(pool, 0);
    NssHeaders.NssVolumeMagic.CopyTo(pool, 4096);

    using var ms = new MemoryStream(pool, writable: false);
    var volume = new NssVolume(ms);
    Assert.That(volume.Valid, Is.False, "a pool without our magic is not ours to read");
    Assert.That(volume.Status, Does.Contain("no public spec"));

    using var again = new MemoryStream(pool, writable: false);
    Assert.That(new NssReader(again).AnyValid, Is.True,
      "and it is still detected as NSS, exactly as before");
  }

  [Test, Category("Sad")]
  public void Defragment_OfARealPool_SaysWhyItCannot() {
    var pool = new byte[64 * 1024];
    NssHeaders.NssPoolMagic.CopyTo(pool, 0);

    using var ms = new MemoryStream(pool);
    Assert.That(() => new NssFormatDescriptor().Defragment(ms, new DefragOptions()),
      Throws.TypeOf<NotSupportedException>().With.Message.Contains("no verifiable public spec"));
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_OfAHoledContainer_KeepsEveryRemainingFile(DefragMode mode) {
    var files = Contents();
    var holed = WithAHole(Volume(files), "FILE1.DAT");
    files.Remove("FILE1.DAT");

    using var image = new MemoryStream();
    image.Write(holed, 0, holed.Length);
    image.Position = 0;
    new NssFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });

    AssertReadsBack(image.ToArray(), files);
  }

  /// <summary>The gap a removal left is actually closed up.</summary>
  [Test]
  public void Defragment_ClosesTheGapARemovalLeft() {
    var files = Contents();
    var holed = WithAHole(Volume(files), "FILE1.DAT");

    using var image = new MemoryStream();
    image.Write(holed, 0, holed.Length);
    image.Position = 0;
    new NssFormatDescriptor().Defragment(
      image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    image.Position = 0;
    var volume = new NssVolume(image);
    Assert.That(volume.Valid, Is.True, volume.Status);

    var runs = volume.Files.OrderBy(f => f.Offset).ToList();
    var cursor = runs[0].Offset;
    foreach (var run in runs) {
      Assert.That(run.Offset, Is.EqualTo(cursor), "the files must follow each other with no gap");
      cursor += volume.BlocksOf(run) * volume.BlockSize;
    }
  }

  /// <summary>The container's own header and its directory survive a defragment.</summary>
  [Test]
  public void Defragment_LeavesTheHeaderAndDirectoryAlone() {
    var files = Contents();
    var holed = WithAHole(Volume(files), "FILE1.DAT");

    using var image = new MemoryStream();
    image.Write(holed, 0, holed.Length);
    image.Position = 0;
    new NssFormatDescriptor().Defragment(
      image, new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });

    image.Position = 0;
    Assert.That(new NssVolume(image).Valid, Is.True, "the container header must survive");

    image.Position = 0;
    var volume = new NssVolume(image);
    foreach (var file in volume.Files)
      Assert.That(file.Offset, Is.GreaterThanOrEqualTo(4 * 4096),
        $"{file.Name} must stay clear of the anchors and the directory");
  }

  [Test]
  public void Defragment_OfAPackedContainer_ChangesNoByte() {
    var before = Volume(Contents());

    using var image = new MemoryStream();
    image.Write(before, 0, before.Length);
    image.Position = 0;
    new NssFormatDescriptor().Defragment(
      image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    Assert.That(image.ToArray(), Is.EqualTo(before), "a packed container comes back byte for byte");
  }

  [Test, Category("RoundTrip")]
  public void Create_ThenList_AndExtract_RoundTrips() {
    var files = Contents();
    var inputs = files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value)).ToList();

    using var image = new MemoryStream();
    var descriptor = new NssFormatDescriptor();
    descriptor.Create(image, inputs, new FormatCreateOptions());

    image.Position = 0;
    var listed = descriptor.List(image, null).Select(e => e.Name).ToList();
    foreach (var name in files.Keys)
      Assert.That(listed, Does.Contain(name), $"{name} must be listed");

    var outDir = Path.Combine(Path.GetTempPath(), "cwb_nssx_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      image.Position = 0;
      descriptor.Extract(image, outDir, null, null);
      foreach (var (name, data) in files)
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, name)), Is.EqualTo(data),
          $"{name} must extract byte for byte");
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* the scratch directory is gone already */ }
    }
  }
}
