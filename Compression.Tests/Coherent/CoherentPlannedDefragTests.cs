#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Coherent;

namespace Compression.Tests.Coherent;

/// <summary>
/// Coherent lays a volume out again by moving blocks. A block is named once —
/// by a zone slot in the inode for the first ten, by an entry in an indirect
/// block for the rest — so a move is the copy plus three bytes, written in the
/// order a PDP-11 wrote them.
/// </summary>
/// <remarks>
/// Nothing described this volume before, so nothing could be planned against
/// it. The map that had to be written first is also what makes a file's second
/// and further runs visible: an indirect block sits between them, and a run
/// either side of it is two runs, not one.
/// </remarks>
[TestFixture]
public class CoherentPlannedDefragTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  private static MemoryStream Volume(out Dictionary<string, byte[]> files) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_coh_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    try {
      var inputs = new List<ArchiveInputInfo>();
      for (var k = 0; k < 5; ++k) {
        // Past ten blocks a file needs an indirect block, which is what splits
        // it into more than one run.
        var data = Payload(k, 3000 + k * 900);
        var path = Path.Combine(work, $"F{k}.BIN");
        File.WriteAllBytes(path, data);
        inputs.Add(new ArchiveInputInfo(path, $"F{k}.BIN", false));
        files[$"F{k}.BIN"] = data;
      }

      var image = new MemoryStream();
      new CoherentFormatDescriptor().Create(image, inputs, new FormatCreateOptions());
      return image;
    } finally {
      try { Directory.Delete(work, true); } catch { /* scratch is gone already */ }
    }
  }

  [Test]
  public void ExtentMap_ClaimsEveryBlockAFileOccupies() {
    using var image = Volume(out var files);
    image.Position = 0;
    var extents = new CoherentFormatDescriptor().EnumerateExtents(image).ToList();

    Assert.That(extents, Is.Not.Empty, "a volume that describes nothing reads as entirely free");

    foreach (var (name, data) in files) {
      var claimed = extents.Where(e => e.FileName == name).Sum(e => e.Length);
      Assert.That(claimed, Is.GreaterThanOrEqualTo(data.Length),
        $"{name}'s blocks must all be claimed");
    }

    // No two extents may describe the same bytes.
    var ordered = extents.OrderBy(e => e.Offset).ToList();
    for (var i = 1; i < ordered.Count; ++i)
      Assert.That(ordered[i].Offset, Is.GreaterThanOrEqualTo(ordered[i - 1].Offset + ordered[i - 1].Length),
        "two extents claim the same bytes");
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_KeepsEveryPayloadAndTheVolumesSize(DefragMode mode) {
    using var image = Volume(out var files);
    var size = image.Length;

    image.Position = 0;
    new CoherentFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });
    Assert.That(image.Length, Is.EqualTo(size), "a volume keeps its size");

    image.Position = 0;
    var reader = new CoherentReader(image);
    foreach (var (name, data) in files) {
      var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name == name);
      Assert.That(entry, Is.Not.Null, $"{name} must still be in the directory");
      Assert.That(reader.Extract(entry!), Is.EqualTo(data), $"{name} must read back byte for byte");
    }
  }

  [Test]
  public void Defragment_LeavesTheIndirectBlocksWhereTheyAre() {
    using var image = Volume(out _);
    var before = image.ToArray();

    image.Position = 0;
    var indirect = new CoherentFormatDescriptor().EnumerateExtents(image)
      .Where(e => e.FileName == "Coherent indirect block")
      .ToList();
    Assert.That(indirect, Is.Not.Empty, "the probe volume must have indirect blocks to protect");

    image.Position = 0;
    new CoherentFormatDescriptor().Defragment(image,
      new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
    var after = image.ToArray();

    // An indirect block holds the pointers to a file's later blocks; a file
    // written over one takes the rest of itself with it.
    foreach (var block in indirect) {
      var at = (int)block.Offset;
      var length = (int)block.Length;
      Assert.That(after.AsSpan(at, length).ToArray(), Is.EqualTo(before.AsSpan(at, length).ToArray()),
        $"the indirect block at {block.Offset} must be untouched");
    }
  }
}
