using System.Buffers.Binary;
using System.Text;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// In-place removal used to search only the first catalog leaf, so on a catalog that had grown
/// past one node it reported an existing file as missing. It now walks the whole leaf chain,
/// and still refuses the removals it cannot make losslessly: one that would change the first
/// key of a leaf (which the index node above names it by) or empty a leaf.
/// </summary>
[TestFixture]
public sealed class HfsPlusMultiLeafRemoveTests {

  private const uint BlockSize = 4096;
  private const int Files = 600;

  private static string Name(int i) => $"file{i:D4}.txt";
  private static byte[] Content(int i) => Encoding.ASCII.GetBytes($"content of file {i}");

  private static byte[] Volume() {
    var w = new HfsPlusWriter();
    for (var i = 0; i < Files; ++i) w.AddFile(Name(i), Content(i));
    return w.Build(BlockSize);
  }

  /// <summary>Every catalog leaf record as (leaf number, index in leaf, parent CNID, name, record type).</summary>
  private static List<(int Leaf, int Index, uint Parent, string Name, short Type)> CatalogRecords(byte[] image, out ushort treeDepth, out uint leafRecords) {
    var vh = image.AsSpan(1024);
    var catalog = (int)(BinaryPrimitives.ReadUInt32BigEndian(vh[288..]) * BlockSize);
    var header = image.AsSpan(catalog + 14);
    treeDepth = BinaryPrimitives.ReadUInt16BigEndian(header);
    leafRecords = BinaryPrimitives.ReadUInt32BigEndian(header[6..]);
    var firstLeaf = BinaryPrimitives.ReadUInt32BigEndian(header[10..]);
    var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(header[18..]);
    var result = new List<(int, int, uint, string, short)>();
    var leafNumber = 0;
    for (var node = firstLeaf; node != 0; ++leafNumber) {
      var nd = image.AsSpan(catalog + (int)node * nodeSize, nodeSize);
      var count = BinaryPrimitives.ReadUInt16BigEndian(nd[10..]);
      for (var i = 0; i < count; ++i) {
        var off = BinaryPrimitives.ReadUInt16BigEndian(nd[(nodeSize - 2 * (i + 1))..]);
        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(nd[off..]);
        var parent = BinaryPrimitives.ReadUInt32BigEndian(nd[(off + 2)..]);
        var nameLength = BinaryPrimitives.ReadUInt16BigEndian(nd[(off + 6)..]);
        var name = Encoding.BigEndianUnicode.GetString(nd.Slice(off + 8, nameLength * 2));
        var data = off + 2 + keyLength;
        if ((data & 1) != 0) data++;
        result.Add((leafNumber, i, parent, name, BinaryPrimitives.ReadInt16BigEndian(nd[data..])));
      }
      node = BinaryPrimitives.ReadUInt32BigEndian(nd);
    }
    return result;
  }

  private static uint Cnid(byte[] image, string name)
    => BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(HfsPlusRemoveExtentsTests.FileRecordOffset(image, name) + 8));

  /// <summary>The files whose file and thread records both sit past the first slot of their leaves, and those that do not.</summary>
  private static (List<int> Removable, List<int> OpensALeaf) Classify(byte[] image) {
    var records = CatalogRecords(image, out _, out _);
    var removable = new List<int>();
    var opening = new List<int>();
    for (var i = 0; i < Files; ++i) {
      var cnid = Cnid(image, Name(i));
      var file = records.Single(r => r.Parent == 2 && r.Name == Name(i) && r.Type == 2);
      var thread = records.Single(r => r.Parent == cnid && r.Name.Length == 0 && r.Type == 4);
      (file.Index > 0 && thread.Index > 0 ? removable : opening).Add(i);
    }
    return (removable, opening);
  }

  [Test, Category("HappyPath")]
  public void GivenACatalogOfManyLeaves_WhenAFileOutsideTheFirstLeafIsRemoved_ThenItGoesAndTheVolumeStaysConsistent() {
    var image = Volume();
    var records = CatalogRecords(image, out var depth, out _);
    Assert.That(depth, Is.GreaterThan(1), "precondition: the catalog has an index level");
    var (removable, _) = Classify(image);
    var target = removable.Last();
    Assert.That(records.Single(r => r.Name == Name(target)).Leaf, Is.GreaterThan(0), "precondition: the file is not in the first leaf");

    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.That(HfsPlusModifier.RemoveFile(ms, Name(target)), Is.True);
    var after = ms.ToArray();

    var reader = new HfsPlusReader(new MemoryStream(after));
    var left = CatalogRecords(after, out _, out var leafRecords);
    var rootValence = BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan(HfsPlusRemoveExtentsTests.FolderRecordOffset(after) + 4));
    Assert.Multiple(() => {
      Assert.That(reader.Entries.Select(e => e.FullPath).ToList(), Has.Count.EqualTo(Files - 1).And.No.Contains(Name(target)));
      foreach (var i in new[] { 0, target - 1, target + 1, Files - 1 }.Where(i => i is >= 0 and < Files && i != target))
        Assert.That(reader.Extract(reader.Entries.Single(e => e.FullPath == Name(i))), Is.EqualTo(Content(i)), Name(i));
      Assert.That(leafRecords, Is.EqualTo((uint)left.Count), "header leafRecords");
      Assert.That(rootValence, Is.EqualTo((uint)(Files - 1)), "root folder valence");
      Assert.That(left.Any(r => r.Type == 4 && r.Parent == Cnid(image, Name(target))), Is.False, "thread record gone");
    });
    HfsPlusRemoveExtentsTests.AssertBitmapConsistent(after);
  }

  [Test, Category("HappyPath")]
  public void GivenACatalogOfManyLeaves_WhenRemovedThroughTheDescriptor_ThenTheFileIsFoundAndRemoved() {
    var image = Volume();
    var target = Classify(image).Removable[^2];
    using var ms = new MemoryStream();
    ms.Write(image);
    new HfsPlusFormatDescriptor().Remove(ms, [Name(target)]);
    ms.Position = 0;
    Assert.That(new HfsPlusReader(ms, leaveOpen: true).Entries.Select(e => e.FullPath), Does.Not.Contain(Name(target)));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAFileWhoseRecordOpensALeaf_WhenRemoved_ThenItIsRefusedAndTheVolumeIsUnchanged() {
    var image = Volume();
    var opening = Classify(image).OpensALeaf;
    Assert.That(opening, Is.Not.Empty, "precondition: some record opens a leaf");
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => HfsPlusModifier.RemoveFile(ms, Name(opening[0])));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenACatalogOfManyLeaves_WhenAFileIsReplaced_ThenItIsRefusedAndTheOldFileSurvives() {
    var image = Volume();
    var target = Classify(image).Removable[0];
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => HfsPlusModifier.AddFile(ms, Name(target), [1, 2, 3]));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAMissingName_WhenRemovedFromAManyLeafCatalog_ThenItIsReportedMissing() {
    var image = Volume();
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Multiple(() => {
      Assert.That(HfsPlusModifier.RemoveFile(ms, "no-such-file.txt"), Is.False);
      Assert.That(ms.ToArray(), Is.EqualTo(image));
    });
  }
}
