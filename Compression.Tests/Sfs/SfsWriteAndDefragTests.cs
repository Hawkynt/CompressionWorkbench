#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Sfs;

namespace Compression.Tests.Sfs;

/// <summary>
/// SFS volumes this writes hold their own arithmetic, and the layout pass over
/// them keeps every file reachable through the tree of extents.
/// </summary>
/// <remarks>
/// There is no SFS driver or checker on Linux to hold a volume up against, so
/// what stands in for one is the format's own bookkeeping: every block carrying
/// a header records its own block number and is checksummed by its longwords
/// summing to zero. A block that moved without being rewritten, or a checksum
/// left stale by a pass, fails that check — which is why every test here ends
/// by making it over the whole volume rather than trusting the reader that
/// wrote it.
/// </remarks>
[TestFixture]
public class SfsWriteAndDefragTests {

  /// <summary>The block types that carry a header, and so a checksum.</summary>
  private static readonly uint[] HeaderedIds =
    [0x53465300, 0x4F424A43, 0x42544D50, 0x4E444320, 0x424E4443, 0x41444D43];

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 23 + seed * 11) % 251);
    return data;
  }

  private static Dictionary<string, byte[]> Contents() {
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var k = 0; k < 5; ++k) files[$"FILE{k}.DAT"] = Payload(k, 300 + k * 900);
    return files;
  }

  private static byte[] Volume(Dictionary<string, byte[]> files) {
    var writer = new SfsWriter();
    foreach (var (name, data) in files) writer.AddFile(name, data);
    return writer.Build();
  }

  /// <summary>
  /// Every block that claims to be one of the volume's own must say which block
  /// it is and add up to zero. This is the whole outside opinion available for
  /// this format, so it is made over the entire image rather than spot-checked.
  /// </summary>
  private static void AssertTheVolumesArithmeticHolds(byte[] image) {
    using var ms = new MemoryStream(image, writable: false);
    var volume = new SfsVolume(ms);
    Assert.That(volume.Valid, Is.True, volume.Status);

    var checked_ = 0;
    for (var block = 0; block < volume.TotalBlocks; ++block) {
      var span = image.AsSpan(block * volume.BlockSize, volume.BlockSize);
      var id = BinaryPrimitives.ReadUInt32BigEndian(span);
      if (!HeaderedIds.Contains(id)) continue;

      ++checked_;
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(span[8..]), Is.EqualTo((uint)block),
        $"the block at {block} records a different block number");

      var sum = 0u;
      for (var at = 0; at + 4 <= span.Length; at += 4)
        sum += BinaryPrimitives.ReadUInt32BigEndian(span[at..]);
      Assert.That(sum, Is.Zero, $"the checksum of the block at {block} does not hold");
    }

    Assert.That(checked_, Is.GreaterThanOrEqualTo(6),
      "a volume has a root block, its copy, a bitmap, an admin space, a node table, " +
      "an extent tree and a root directory");
  }

  private static void AssertReadsBack(byte[] image, IReadOnlyDictionary<string, byte[]> expected) {
    using var ms = new MemoryStream(image, writable: false);
    var volume = new SfsVolume(ms);
    Assert.That(volume.Valid, Is.True, volume.Status);
    Assert.That(volume.Files.Select(f => f.Name), Is.EquivalentTo(expected.Keys));
    foreach (var file in volume.Files)
      Assert.That(volume.Read(file), Is.EqualTo(expected[file.Name]), $"{file.Name} must be intact");
  }

  /// <summary>
  /// Removes a file the way the filesystem would: its entry leaves the object
  /// container and its extent leaves the tree, and both blocks are checksummed
  /// again — so what is left is a volume with a gap in the middle rather than a
  /// broken one.
  /// </summary>
  private static byte[] WithAHole(byte[] image, string name) {
    var holed = (byte[])image.Clone();
    using var ms = new MemoryStream(holed, writable: false);
    var volume = new SfsVolume(ms);
    Assert.That(volume.Valid, Is.True, volume.Status);

    var victim = volume.Files.Single(f => f.Name == name);
    var bs = volume.BlockSize;

    var containerBlock = (int)(victim.EntryOffset / bs);
    var container = holed.AsSpan(containerBlock * bs, bs);

    var kept = new List<byte[]>();
    for (var cursor = 24; cursor + 25 < bs && container[cursor + 25] != 0;) {
      var length = EntryLength(container, cursor);
      if (containerBlock * bs + cursor != victim.EntryOffset)
        kept.Add(container.Slice(cursor, length).ToArray());
      cursor += length;
    }

    container[24..].Clear();
    var at = 24;
    foreach (var entry in kept) { entry.CopyTo(container[at..]); at += entry.Length; }
    Rechecksum(container);

    var tree = holed.AsSpan((int)volume.ExtentTreeBlock * bs, bs);
    var count = BinaryPrimitives.ReadUInt16BigEndian(tree[12..]);
    var nodes = new List<byte[]>();
    for (var i = 0; i < count; ++i) {
      var node = tree.Slice(16 + i * 14, 14);
      if (BinaryPrimitives.ReadUInt32BigEndian(node) != victim.Extents[0].Block)
        nodes.Add(node.ToArray());
    }

    tree[16..].Clear();
    BinaryPrimitives.WriteUInt16BigEndian(tree[12..], (ushort)nodes.Count);
    for (var i = 0; i < nodes.Count; ++i) nodes[i].CopyTo(tree[(16 + i * 14)..]);
    Rechecksum(tree);

    return holed;
  }

  private static int EntryLength(ReadOnlySpan<byte> container, int at) {
    var cursor = at + 25;
    while (container[cursor] != 0) ++cursor;
    ++cursor;
    while (container[cursor] != 0) ++cursor;
    ++cursor;
    return ((cursor - at) + 1) & ~1;
  }

  private static void Rechecksum(Span<byte> block) {
    BinaryPrimitives.WriteUInt32BigEndian(block[4..], 0);
    var sum = 0u;
    for (var at = 0; at + 4 <= block.Length; at += 4)
      sum += BinaryPrimitives.ReadUInt32BigEndian(block[at..]);
    BinaryPrimitives.WriteUInt32BigEndian(block[4..], 0u - sum);
  }

  [Test, Category("HappyPath")]
  public void AVolumeWeWrite_ReadsBackAndAddsUp() {
    var files = Contents();
    var image = Volume(files);

    AssertReadsBack(image, files);
    AssertTheVolumesArithmeticHolds(image);
  }

  /// <summary>
  /// The root block is kept twice, once at each end, and the copy carries the
  /// later sequence number — which is how the filesystem tells them apart when
  /// a write was interrupted between the two.
  /// </summary>
  [Test]
  public void AVolumeWeWrite_KeepsASecondRootBlockAtTheFarEnd() {
    var image = Volume(Contents());
    using var ms = new MemoryStream(image, writable: false);
    var volume = new SfsVolume(ms);

    var tail = (int)(volume.TotalBlocks - 1) * volume.BlockSize;
    Assert.That(image.AsSpan(tail, 4).SequenceEqual("SFS\0"u8), Is.True,
      "the last block must be a root block too");
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(tail + 14)),
      Is.GreaterThan(BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(14))),
      "the copy must be the later of the two");
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_OfAHoledVolume_KeepsEveryRemainingFile(DefragMode mode) {
    var files = Contents();
    var holed = WithAHole(Volume(files), "FILE1.DAT");
    files.Remove("FILE1.DAT");

    using var image = new MemoryStream();
    image.Write(holed, 0, holed.Length);
    image.Position = 0;
    new SfsFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });

    var result = image.ToArray();
    AssertReadsBack(result, files);
    AssertTheVolumesArithmeticHolds(result);
  }

  /// <summary>
  /// Closing the hole has to actually close it: the files after the gap move
  /// down into it and nothing is left between them.
  /// </summary>
  [Test]
  public void Defragment_ClosesTheGapARemovalLeft() {
    var files = Contents();
    var holed = WithAHole(Volume(files), "FILE1.DAT");

    using var image = new MemoryStream();
    image.Write(holed, 0, holed.Length);
    image.Position = 0;
    new SfsFormatDescriptor().Defragment(
      image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    image.Position = 0;
    var volume = new SfsVolume(image);
    Assert.That(volume.Valid, Is.True, volume.Status);

    var runs = volume.Files.SelectMany(f => f.Extents).OrderBy(e => e.Block).ToList();
    Assert.That(runs, Is.Not.Empty);

    var cursor = runs[0].Block;
    foreach (var run in runs) {
      Assert.That(run.Block, Is.EqualTo(cursor), "the files must follow each other with no gap");
      cursor += run.Count;
    }
  }

  /// <summary>
  /// Packing against the tail has to stop short of the copy of the root block
  /// the volume keeps there, and must not touch anything at the front either.
  /// </summary>
  [Test]
  public void Defragment_LeavesTheVolumesOwnBlocksAlone() {
    var files = Contents();
    var holed = WithAHole(Volume(files), "FILE1.DAT");

    using var before = new MemoryStream(holed, writable: false);
    var reservedBefore = new SfsVolume(before).ReservedBlocks.Distinct().OrderBy(b => b).ToList();

    using var image = new MemoryStream();
    image.Write(holed, 0, holed.Length);
    image.Position = 0;
    new SfsFormatDescriptor().Defragment(
      image, new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });

    image.Position = 0;
    var volume = new SfsVolume(image);
    Assert.That(volume.ReservedBlocks.Distinct().OrderBy(b => b), Is.EqualTo(reservedBefore),
      "the volume's own blocks must be exactly where they were");

    var reserved = reservedBefore.ToHashSet();
    foreach (var file in volume.Files)
      foreach (var extent in file.Extents)
        for (var b = extent.Block; b < extent.Block + extent.Count; ++b)
          Assert.That(reserved.Contains(b), Is.False, $"{file.Name} must not sit on block {b}");

    AssertTheVolumesArithmeticHolds(image.ToArray());
  }

  [Test]
  public void Defragment_OfAPackedVolume_ChangesNoByte() {
    var before = Volume(Contents());

    using var image = new MemoryStream();
    image.Write(before, 0, before.Length);
    image.Position = 0;
    new SfsFormatDescriptor().Defragment(
      image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    Assert.That(image.ToArray(), Is.EqualTo(before), "a packed volume comes back byte for byte");
  }

  [Test, Category("RoundTrip")]
  public void Create_ThenList_AndExtract_RoundTrips() {
    var files = Contents();
    var inputs = files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value)).ToList();

    using var image = new MemoryStream();
    var descriptor = new SfsFormatDescriptor();
    descriptor.Create(image, inputs, new FormatCreateOptions());

    image.Position = 0;
    var listed = descriptor.List(image, null).Select(e => e.Name).ToList();
    foreach (var name in files.Keys)
      Assert.That(listed, Does.Contain(name), $"{name} must be listed");

    var outDir = Path.Combine(Path.GetTempPath(), "cwb_sfsx_" + Guid.NewGuid().ToString("N")[..8]);
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
