#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Ubifs;

namespace Compression.Tests.Ubifs;

/// <summary>
/// UBIFS lays an image out again by moving nodes, and nothing else has to be
/// rewritten at all.
/// </summary>
/// <remarks>
/// What this writer emits is a linear log of nodes — no index tree, no
/// erase-block accounting — and the reader replays that log by looking for the
/// magic at the head of each node. So a node's position is recorded nowhere,
/// and moving one repoints nothing. What a move must do is leave nothing
/// behind: a copy of a node still carrying its magic is a second node, and the
/// replay would find both.
/// </remarks>
[TestFixture]
public class UbifsPlannedDefragTests {

  private const uint NodeMagic = 0x06101831;

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  private static MemoryStream Volume(out Dictionary<string, byte[]> files) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_ubifs_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    try {
      var inputs = new List<ArchiveInputInfo>();
      for (var k = 0; k < 5; ++k) {
        var data = Payload(k, 6000 + k * 2500);
        var path = Path.Combine(work, $"F{k}.BIN");
        File.WriteAllBytes(path, data);
        inputs.Add(new ArchiveInputInfo(path, $"F{k}.BIN", false));
        files[$"F{k}.BIN"] = data;
      }

      var image = new MemoryStream();
      new UbifsFormatDescriptor().Create(image, inputs, new FormatCreateOptions());
      return image;
    } finally {
      try { Directory.Delete(work, true); } catch { /* scratch is gone already */ }
    }
  }

  private static Dictionary<string, byte[]> ReadBack(MemoryStream image) {
    image.Position = 0;
    var reader = new UbifsFileReader(image);
    return reader.Entries
      .Where(e => !e.IsDirectory)
      .ToDictionary(e => Path.GetFileName(e.Name), reader.Extract, StringComparer.Ordinal);
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_KeepsEveryPayloadAndTheImagesSize(DefragMode mode) {
    using var image = Volume(out var files);
    var size = image.Length;

    image.Position = 0;
    new UbifsFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });
    Assert.That(image.Length, Is.EqualTo(size), "an image keeps its size");

    var read = ReadBack(image);
    foreach (var (name, data) in files) {
      Assert.That(read.Keys, Does.Contain(name), $"{name} must still be in the log");
      Assert.That(read[name], Is.EqualTo(data), $"{name} must read back byte for byte");
    }
  }

  [Test]
  public void Defragment_ActuallyMovesTheNodes() {
    using var image = Volume(out _);
    var descriptor = new UbifsFormatDescriptor();

    image.Position = 0;
    var before = descriptor.EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used).Select(e => e.Offset).OrderBy(o => o).ToList();
    Assert.That(before, Is.Not.Empty, "the probe image must have data nodes to move");

    image.Position = 0;
    descriptor.Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });

    image.Position = 0;
    var after = descriptor.EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used).Select(e => e.Offset).OrderBy(o => o).ToList();

    Assert.That(after, Is.Not.EqualTo(before), "packing against the tail must move something");
    Assert.That(after.Max(), Is.GreaterThan(before.Max()), "and it must move towards the tail");
  }

  [Test]
  public void Defragment_LeavesNoNodeBehindWhereItWas() {
    using var image = Volume(out _);
    var descriptor = new UbifsFormatDescriptor();

    image.Position = 0;
    var nodesBefore = descriptor.EnumerateExtents(image).Count();

    image.Position = 0;
    descriptor.Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });

    image.Position = 0;
    var nodesAfter = descriptor.EnumerateExtents(image).Count();
    Assert.That(nodesAfter, Is.EqualTo(nodesBefore),
      "a node copied without clearing its old home would be found twice");

    // And every magic in the image belongs to a node the map claims.
    var raw = image.ToArray();
    image.Position = 0;
    var claimed = descriptor.EnumerateExtents(image).Select(e => e.Offset).ToHashSet();
    for (var at = 0; at + 4 <= raw.Length; ++at) {
      if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at)) != NodeMagic) continue;
      Assert.That(claimed.Contains(at), Is.True,
        $"a node's magic at {at} is not one the log accounts for");
    }
  }
}
