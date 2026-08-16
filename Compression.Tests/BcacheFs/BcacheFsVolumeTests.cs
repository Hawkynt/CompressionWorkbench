#pragma warning disable CS1591
using Compression.Registry;
using Compression.Tests.Support;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

/// <summary>
/// A bcachefs volume written here is a bcachefs volume: its files come back
/// through our own reader, through the kernel driver, and through both again after
/// the layout has been rearranged.
/// </summary>
/// <remarks>
/// Everything a bcachefs volume says about itself is in b-trees — the names in one,
/// the sizes in another, the positions of the bytes in a third — so a volume that
/// reads back at all is one whose keys, checksums and hashes all agree. That is
/// what these check, and the kernel is the judge of it wherever it is installed.
/// </remarks>
[TestFixture]
public class BcacheFsVolumeTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)(i * 37 + seed * 11);
    return data;
  }

  /// <summary>A volume with a nested tree, a large file and an empty one.</summary>
  private static Dictionary<string, byte[]> Contents() {
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["README.TXT"] = Payload(1, 40),
      ["EMPTY.BIN"] = [],
      ["docs/guide.txt"] = Payload(2, 5000),
      ["docs/api/reference.txt"] = Payload(3, 70000),
      ["BIG.BIN"] = Payload(4, 300000),
    };
    for (var i = 0; i < 20; ++i) files[$"many/file{i:D2}.bin"] = Payload(10 + i, 1000 + i * 100);
    return files;
  }

  private static byte[] Build(IReadOnlyDictionary<string, byte[]> files) {
    var writer = new BcacheFsWriter();
    foreach (var (name, data) in files) writer.AddFile(name, data);

    using var buffer = new MemoryStream();
    writer.WriteTo(buffer);
    return buffer.ToArray();
  }

  private static void AssertReadsBack(byte[] image, IReadOnlyDictionary<string, byte[]> expected,
      string context) {
    using var stream = new MemoryStream(image, writable: false);
    using var reader = new BcacheFsReader(stream);
    Assert.That(reader.Valid, Is.True, $"{context}: {reader.Status}");
    Assert.That(reader.Entries.Select(e => e.Name), Is.EquivalentTo(expected.Keys),
      $"{context}: every file must be named");

    foreach (var entry in reader.Entries)
      Assert.That(reader.Read(entry), Is.EqualTo(expected[entry.Name]),
        $"{context}: {entry.Name} must come back byte for byte");
  }

  [Test, Category("RoundTrip")]
  public void AVolumeWeWrite_ReadsItsFilesBack() {
    var files = Contents();
    AssertReadsBack(Build(files), files, "as written");
  }

  /// <summary>Directories are keys too, and a nested path is several of them.</summary>
  [Test, Category("RoundTrip")]
  public void ANestedPath_KeepsEveryComponent() {
    var files = Contents();
    using var stream = new MemoryStream(Build(files), writable: false);
    using var reader = new BcacheFsReader(stream);

    Assert.That(reader.Directories, Does.Contain("docs"));
    Assert.That(reader.Directories, Does.Contain("docs/api"));
    Assert.That(reader.Directories, Does.Contain("many"));
  }

  /// <summary>
  /// Every run of every file sits inside one bucket.
  /// </summary>
  /// <remarks>
  /// A bucket is what bcachefs allocates and accounts in, and a run laid across the
  /// boundary between two is read as an invalid key — the file it belongs to comes
  /// back as a hole, with nothing said about why.
  /// </remarks>
  [Test]
  public void EveryRun_StaysInsideOneBucket() {
    var files = Contents();
    using var stream = new MemoryStream(Build(files), writable: false);
    using var reader = new BcacheFsReader(stream);

    foreach (var entry in reader.Entries)
      foreach (var extent in entry.Extents) {
        var bucket = extent.FirstSector / 128;
        var lastBucket = (extent.FirstSector + extent.Sectors - 1) / 128;
        Assert.That(lastBucket, Is.EqualTo(bucket),
          $"{entry.Name}: a run at sector {extent.FirstSector} of {extent.Sectors} crosses a bucket boundary");
      }
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_KeepsEveryFile(DefragMode mode) {
    var files = Contents();
    using var image = new MemoryStream();
    var built = Build(files);
    image.Write(built, 0, built.Length);

    image.Position = 0;
    new BcacheFsFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });
    AssertReadsBack(image.ToArray(), files, $"after {mode}");
  }

  /// <summary>Laying the files out at the front actually moves them there.</summary>
  [Test]
  public void Defragment_AtStart_ClosesTheGapsBetweenFiles() {
    var files = Contents();
    using var image = new MemoryStream();
    var built = Build(files);
    image.Write(built, 0, built.Length);

    long FirstFreeGap() {
      image.Position = 0;
      using var reader = new BcacheFsReader(image, leaveOpen: true);
      var runs = reader.Entries.SelectMany(e => e.Extents)
        .OrderBy(x => x.FirstSector).ToList();
      var gaps = 0L;
      for (var i = 1; i < runs.Count; ++i) {
        var previousEnd = runs[i - 1].FirstSector + runs[i - 1].Sectors;
        // Runs are placed a bucket at a time, so anything past the bucket the last
        // one ended in is a gap.
        var nextBucketStart = (previousEnd + 127) / 128 * 128;
        if (runs[i].FirstSector > nextBucketStart) gaps += runs[i].FirstSector - nextBucketStart;
      }

      return gaps;
    }

    image.Position = 0;
    new BcacheFsFormatDescriptor().Defragment(image,
      new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    Assert.That(FirstFreeGap(), Is.Zero, "consolidating at the start must leave no gap between runs");
    AssertReadsBack(image.ToArray(), files, "after consolidating at the start");
  }

  /// <summary>
  /// More files than one b-tree node holds, and the trees grow a level.
  /// </summary>
  /// <remarks>
  /// A node is one bucket. Past that a tree is a root of pointers over a row of
  /// leaves, each responsible for a range of positions that has to meet its
  /// neighbours' exactly — and the superblock has to say how deep the tree is, or a
  /// reader looks for keys where the pointers are and finds the volume unreadable.
  /// </remarks>
  [Test, Category("RoundTrip")]
  public void MoreFilesThanOneNodeHolds_GrowsTheTreesAndStillReadsBack() {
    const int count = 1200;
    var path = Path.Combine(Path.GetTempPath(), "cwb_bchbig_" + Guid.NewGuid().ToString("N")[..8] + ".bch");
    try {
      var writer = new BcacheFsWriter();
      for (var i = 0; i < count; ++i)
        writer.AddFile($"f{i:D5}.bin", System.Text.Encoding.ASCII.GetBytes($"payload-{i:D5}"));

      using (var output = File.Create(path))
        writer.WriteTo(output);

      using var image = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
      using var reader = new BcacheFsReader(image, leaveOpen: true);
      Assert.That(reader.Valid, Is.True, reader.Status);
      Assert.That(reader.Entries, Has.Count.EqualTo(count));

      foreach (var entry in reader.Entries)
        Assert.That(System.Text.Encoding.ASCII.GetString(reader.Read(entry)),
          Is.EqualTo($"payload-{entry.Name.Substring(1, 5)}"), $"{entry.Name} must come back");
    } finally {
      try { File.Delete(path); } catch { /* the scratch image is gone already */ }
    }
  }

  [Test, Category("RoundTrip")]
  public void Create_ThenList_AndExtract_RoundTrips() {
    var files = Contents();
    var inputs = new List<ArchiveInputInfo>();
    var work = Path.Combine(Path.GetTempPath(), "cwb_bch_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);

    try {
      foreach (var (name, data) in files) {
        var path = Path.Combine(work, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        inputs.Add(new ArchiveInputInfo(path, name, false));
      }

      using var image = new MemoryStream();
      var descriptor = new BcacheFsFormatDescriptor();
      descriptor.Create(image, inputs, new FormatCreateOptions());

      image.Position = 0;
      var listed = descriptor.List(image, null).Select(e => e.Name).ToList();
      foreach (var name in files.Keys)
        Assert.That(listed, Does.Contain(name), $"{name} must be listed");

      var outDir = Path.Combine(work, "out");
      Directory.CreateDirectory(outDir);
      image.Position = 0;
      descriptor.Extract(image, outDir, null, null);

      foreach (var (name, data) in files) {
        var extracted = Path.Combine(outDir, name.Replace('/', Path.DirectorySeparatorChar));
        Assert.That(File.Exists(extracted), Is.True, $"{name} must be extracted");
        Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(data), $"{name} must extract byte for byte");
      }
    } finally {
      try { Directory.Delete(work, true); } catch { /* the scratch directory is gone already */ }
    }
  }

  /// <summary>
  /// A volume written for writing is one the kernel will mount read-write.
  /// </summary>
  /// <remarks>
  /// <para>A read-write mount needs allocation information, because it cannot
  /// decide where to put a write without it. The volume carries it, so there is
  /// nothing to ask for and no second kind of volume to write: this mounts the
  /// same image the read-only test mounts.</para>
  ///
  /// <para>Either way the device has to declare a durability, and that field holds
  /// one more than it is so that zero can mean the default. A device that declares
  /// zero durability keeps no copy of anything, and is never chosen to hold a
  /// journal or a b-tree — which is how a volume comes to be refused for having no
  /// writeable journal device on it.</para>
  /// </remarks>
  [Test, Category("OsIntegration")]
  public void AVolumeWrittenForWriting_MountsReadWrite() {
    if (!ThirdPartyFsCheck.IsSupported("BcacheFs")) Assert.Ignore("no third-party reader configured");

    var files = Contents();
    var path = Path.Combine(Path.GetTempPath(), "cwb_bchrw_" + Guid.NewGuid().ToString("N")[..8] + ".bch");
    try {
      var writer = new BcacheFsWriter();
      foreach (var (name, data) in files) writer.AddFile(name, data);
      using (var output = File.Create(path)) writer.WriteTo(output);

      var result = ThirdPartyFsCheck.ReadBack("BcacheFs", path,
        [.. files.Values.Where(v => v.Length > 0)], readOnly: false);
      if (!result.Ran) Assert.Ignore($"the kernel driver did not run here — {result.Detail}");
      Assert.That(result.Ok, Is.True, $"{result.Tool}: {result.Detail}");
    } finally {
      try { File.Delete(path); } catch { /* the scratch image is gone already */ }
    }
  }

  /// <summary>
  /// The kernel reads a volume written here, and reads the same bytes out of it.
  /// </summary>
  [Test, Category("OsIntegration")]
  public void TheKernelReadsAVolumeWeWrote() {
    if (!ThirdPartyFsCheck.IsSupported("BcacheFs")) Assert.Ignore("no third-party reader configured");

    var files = Contents();
    var path = Path.Combine(Path.GetTempPath(), "cwb_bchmnt_" + Guid.NewGuid().ToString("N")[..8] + ".bch");
    try {
      File.WriteAllBytes(path, Build(files));
      var result = ThirdPartyFsCheck.ReadBack("BcacheFs", path, [.. files.Values.Where(v => v.Length > 0)]);
      if (!result.Ran) Assert.Ignore($"the kernel driver did not run here — {result.Detail}");
      Assert.That(result.Ok, Is.True, $"{result.Tool}: {result.Detail}");
    } finally {
      try { File.Delete(path); } catch { /* the scratch image is gone already */ }
    }
  }
}
