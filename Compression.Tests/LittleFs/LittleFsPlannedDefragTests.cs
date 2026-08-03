#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.LittleFs;

namespace Compression.Tests.LittleFs;

/// <summary>
/// littlefs lays a volume out again by moving blocks, even though almost
/// nothing outside a file names them.
/// </summary>
/// <remarks>
/// A file's blocks are a skip-list threaded backwards through the blocks
/// themselves: block <c>i</c> opens with pointers to <c>i-1</c>, <c>i-2</c>,
/// <c>i-4</c> and so on. So the pointers to a block live inside other blocks of
/// the same file, and they can only be written once every block has a final
/// home — which is why they are threaded from the finished order rather than
/// patched as the pass goes. The head is the one thing named from outside, by a
/// tag in a metadata pair, and writing it means taking that commit's checksum
/// again.
/// </remarks>
[TestFixture]
public class LittleFsPlannedDefragTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  /// <summary>A volume with a hole in it, so a pass has something to close up.</summary>
  private static MemoryStream Volume(out Dictionary<string, byte[]> files) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_lfs_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    try {
      var inputs = new List<ArchiveInputInfo>();
      for (var k = 0; k < 4; ++k) {
        var data = Payload(k, 3000 + k * 2500);
        var path = Path.Combine(work, $"F{k}.BIN");
        File.WriteAllBytes(path, data);
        inputs.Add(new ArchiveInputInfo(path, $"F{k}.BIN", false));
        files[$"F{k}.BIN"] = data;
      }

      var image = new MemoryStream();
      var descriptor = new LittleFsFormatDescriptor();
      descriptor.Create(image, inputs, new FormatCreateOptions());

      image.Position = 0;
      descriptor.Remove(image, ["F1.BIN"]);
      files.Remove("F1.BIN");
      return image;
    } finally {
      try { Directory.Delete(work, true); } catch { /* scratch is gone already */ }
    }
  }

  private static Dictionary<string, byte[]> ReadBack(MemoryStream image) {
    image.Position = 0;
    using var reader = new LittleFsReader(image);
    return reader.Files.ToDictionary(f => Path.GetFileName(f.Path), reader.ReadFile, StringComparer.Ordinal);
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_KeepsEveryPayloadAndTheVolumesSize(DefragMode mode) {
    using var image = Volume(out var files);
    var size = image.Length;

    image.Position = 0;
    new LittleFsFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });
    Assert.That(image.Length, Is.EqualTo(size), "a volume keeps its size");

    // Reading a file follows the skip-list from its head backwards, so a
    // payload that comes back whole is a chain threaded correctly.
    var read = ReadBack(image);
    foreach (var (name, data) in files) {
      Assert.That(read.Keys, Does.Contain(name), $"{name} must still be on the volume");
      Assert.That(read[name], Is.EqualTo(data), $"{name} must read back byte for byte");
    }
  }

  [Test]
  public void Defragment_ActuallyMovesTheBlocks() {
    using var image = Volume(out _);
    var descriptor = new LittleFsFormatDescriptor();

    image.Position = 0;
    var before = descriptor.EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used).Select(e => e.Offset).OrderBy(o => o).ToList();
    Assert.That(before, Is.Not.Empty, "the probe volume must have data blocks to move");

    image.Position = 0;
    descriptor.Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    image.Position = 0;
    var after = descriptor.EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used).Select(e => e.Offset).OrderBy(o => o).ToList();

    Assert.That(after, Is.Not.EqualTo(before), "packing from the front must move something");
    Assert.That(after.Min(), Is.LessThan(before.Min()), "and it must move towards the front");
  }

  [Test]
  public void Defragment_LeavesTheMetadataPairCheckingOut() {
    using var image = Volume(out var files);
    image.Position = 0;
    new LittleFsFormatDescriptor().Defragment(image,
      new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    // A metadata pair is a log of commits with a checksum over each; the reader
    // refuses a commit whose checksum does not hold, so a volume that still
    // lists its files is one whose commit was stamped again correctly.
    var read = ReadBack(image);
    Assert.That(read.Count, Is.EqualTo(files.Count),
      "every file must still be listed, which means the commit still checks out");
  }
}
