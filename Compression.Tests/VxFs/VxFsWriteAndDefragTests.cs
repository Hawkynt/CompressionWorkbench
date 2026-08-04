#pragma warning disable CS1591
using Compression.Registry;
using Compression.Tests.Support;
using FileSystem.VxFs;

namespace Compression.Tests.VxFs;

/// <summary>
/// VxFS volumes this writes are read by the kernel's own driver, and the layout
/// pass over them keeps every file where the driver can still find it.
/// </summary>
/// <remarks>
/// The driver is the only opinion that settles whether a volume is a VxFS
/// volume, so where it is available every check here ends by mounting the
/// result. Where it is not, the walk in <see cref="VxFsVolume" /> stands in —
/// it is a separate implementation of the same five hops from the superblock to
/// the files, so agreeing with it is worth something even when nothing else can
/// be asked.
/// </remarks>
[TestFixture]
public class VxFsWriteAndDefragTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 31 + seed * 7) % 251);
    return data;
  }

  private static Dictionary<string, byte[]> Contents() {
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var k = 0; k < 5; ++k) files[$"FILE{k}.BIN"] = Payload(k, 500 + k * 1500);
    return files;
  }

  private static byte[] Volume(Dictionary<string, byte[]> files) {
    var writer = new VxFsWriter();
    foreach (var (name, data) in files) writer.AddFile(name, data);
    return writer.Build();
  }

  /// <summary>
  /// Blanks a directory entry the way removing a file would, leaving the blocks
  /// it held unclaimed in the middle of the volume — which is the shape a
  /// layout pass exists for.
  /// </summary>
  private static byte[] WithAHole(byte[] image, string name, out long freedBlocks) {
    var holed = (byte[])image.Clone();
    using var ms = new MemoryStream(holed, writable: false);
    var volume = new VxFsVolume(ms);
    Assert.That(volume.Valid, Is.True, volume.Status);

    var removed = volume.Files.Single(f => f.Name == name);
    freedBlocks = removed.Extents.Sum(e => e.Count);

    var block = volume.RootDirectoryExtents[0].Block * volume.BlockSize;
    for (var cursor = block + 4; cursor < block + volume.BlockSize;) {
      var recordLength = BitConverter.ToUInt16(holed, (int)cursor + 4);
      if (recordLength == 0) break;

      var nameLength = BitConverter.ToUInt16(holed, (int)cursor + 6);
      var found = System.Text.Encoding.ASCII.GetString(holed, (int)cursor + 10, nameLength);
      if (found == name) {
        BitConverter.GetBytes(0u).CopyTo(holed, (int)cursor);
        return holed;
      }

      cursor += recordLength;
    }

    throw new InvalidOperationException($"{name} is not in the directory.");
  }

  private static void AssertReadsBack(byte[] image, IReadOnlyDictionary<string, byte[]> expected) {
    using var ms = new MemoryStream(image, writable: false);
    var volume = new VxFsVolume(ms);
    Assert.That(volume.Valid, Is.True, volume.Status);
    Assert.That(volume.Files.Select(f => f.Name), Is.EquivalentTo(expected.Keys));
    foreach (var file in volume.Files)
      Assert.That(volume.Read(file), Is.EqualTo(expected[file.Name]), $"{file.Name} must be intact");
  }

  /// <summary>Mounts the image with the kernel driver, when there is one to ask.</summary>
  private static void AssertTheDriverAgrees(byte[] image, IReadOnlyDictionary<string, byte[]> expected) {
    var path = Path.Combine(Path.GetTempPath(), "cwb_vxfs_" + Guid.NewGuid().ToString("N")[..8] + ".vxfs");
    try {
      File.WriteAllBytes(path, image);
      var result = ThirdPartyFsCheck.ReadBack("VxFs", path, expected.Values.ToList());
      if (!result.Ran) Assert.Ignore(result.Detail);
      Assert.That(result.Ok, Is.True, result.Detail);
    } finally {
      try { File.Delete(path); } catch { /* the scratch file is gone already */ }
    }
  }

  [Test, Category("HappyPath")]
  public void AVolumeWeWrite_IsOneTheDriverReads() {
    var files = Contents();
    var image = Volume(files);

    AssertReadsBack(image, files);
    AssertTheDriverAgrees(image, files);
  }

  /// <summary>
  /// Every file must be one run to begin with. A pass that merely kept a
  /// volume's fragmentation would pass a payload check while doing nothing.
  /// </summary>
  [Test]
  public void AVolumeWeWrite_GivesEachFileOneExtent() {
    using var ms = new MemoryStream(Volume(Contents()));
    var volume = new VxFsVolume(ms);

    Assert.That(volume.Valid, Is.True, volume.Status);
    Assert.That(volume.Files, Has.Count.EqualTo(5));
    foreach (var file in volume.Files)
      Assert.That(file.Extents, Has.Count.EqualTo(1), $"{file.Name} should be in one piece");
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_OfAHoledVolume_KeepsEveryRemainingFile(DefragMode mode) {
    var files = Contents();
    var holed = WithAHole(Volume(files), "FILE1.BIN", out var freed);
    Assert.That(freed, Is.GreaterThan(0), "the removed file must have held blocks");
    files.Remove("FILE1.BIN");

    using var image = new MemoryStream();
    image.Write(holed, 0, holed.Length);
    image.Position = 0;
    new VxFsFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });

    var result = image.ToArray();
    AssertReadsBack(result, files);
    AssertTheDriverAgrees(result, files);
  }

  /// <summary>
  /// Closing the hole has to actually close it: the files after the gap move
  /// down into it, and nothing is left between them.
  /// </summary>
  [Test]
  public void Defragment_ClosesTheGapARemovalLeft() {
    var files = Contents();
    var holed = WithAHole(Volume(files), "FILE1.BIN", out _);
    files.Remove("FILE1.BIN");

    using var image = new MemoryStream();
    image.Write(holed, 0, holed.Length);
    image.Position = 0;
    new VxFsFormatDescriptor().Defragment(
      image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    image.Position = 0;
    var volume = new VxFsVolume(image);
    Assert.That(volume.Valid, Is.True, volume.Status);

    var runs = volume.Files
      .SelectMany(f => f.Extents)
      .OrderBy(e => e.Block)
      .ToList();
    Assert.That(runs, Is.Not.Empty);

    var cursor = runs[0].Block;
    foreach (var run in runs) {
      Assert.That(run.Block, Is.EqualTo(cursor), "the files must follow each other with no gap");
      cursor += run.Count;
    }
  }

  /// <summary>
  /// A volume that is already laid out is left exactly as it is, rather than
  /// rewritten to the same shape.
  /// </summary>
  [Test]
  public void Defragment_OfAPackedVolume_ChangesNoByte() {
    var before = Volume(Contents());

    using var image = new MemoryStream();
    image.Write(before, 0, before.Length);
    image.Position = 0;
    new VxFsFormatDescriptor().Defragment(
      image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    Assert.That(image.ToArray(), Is.EqualTo(before), "a packed volume comes back byte for byte");
  }

  /// <summary>
  /// Nothing may be moved onto the chain the driver walks to reach the files.
  /// A volume with a file on top of any of it does not mount at all, which is a
  /// worse outcome than the fragmentation the pass was asked to fix.
  /// </summary>
  [Test]
  public void Defragment_LeavesTheStructureAlone() {
    var files = Contents();
    var holed = WithAHole(Volume(files), "FILE1.BIN", out _);

    using var before = new MemoryStream(holed, writable: false);
    var reservedBefore = new VxFsVolume(before).ReservedExtents.ToList();

    using var image = new MemoryStream();
    image.Write(holed, 0, holed.Length);
    image.Position = 0;
    new VxFsFormatDescriptor().Defragment(
      image, new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });

    image.Position = 0;
    var volume = new VxFsVolume(image);
    Assert.That(volume.ReservedExtents, Is.EquivalentTo(reservedBefore),
      "the structures the driver walks must be exactly where they were");

    var reserved = volume.ReservedExtents
      .Concat(volume.RootDirectoryExtents)
      .SelectMany(e => Enumerable.Range((int)e.Block, (int)e.Count))
      .ToHashSet();
    foreach (var file in volume.Files)
      foreach (var extent in file.Extents)
        for (var b = extent.Block; b < extent.Block + extent.Count; ++b)
          Assert.That(reserved.Contains((int)b), Is.False,
            $"{file.Name} must not sit on block {b}");
  }

  [Test, Category("RoundTrip")]
  public void Create_ThenList_AndExtract_RoundTrips() {
    var files = Contents();
    var inputs = files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value)).ToList();

    using var image = new MemoryStream();
    var descriptor = new VxFsFormatDescriptor();
    descriptor.Create(image, inputs, new FormatCreateOptions());

    image.Position = 0;
    var listed = descriptor.List(image, null).Select(e => e.Name).ToList();
    foreach (var name in files.Keys)
      Assert.That(listed, Does.Contain(name), $"{name} must be listed");

    var outDir = Path.Combine(Path.GetTempPath(), "cwb_vxfsx_" + Guid.NewGuid().ToString("N")[..8]);
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
