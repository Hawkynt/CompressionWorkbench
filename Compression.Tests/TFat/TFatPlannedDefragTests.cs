#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.TFat;

namespace Compression.Tests.TFat;

/// <summary>
/// TFAT lays itself out again by moving clusters, the way FAT does, and the
/// markers that make it TFAT have to survive that.
/// </summary>
/// <remarks>
/// A layout pass knows about cluster chains and directory entries; it knows
/// nothing about the tag in the boot sector, the byte that says the volume is
/// transactional, or the four bytes at the end of each FAT region that say
/// which copy is current. They are re-applied afterwards from what the volume
/// already carries, so the two copies stay in the step they were in.
/// </remarks>
[TestFixture]
public class TFatPlannedDefragTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  private static MemoryStream FragmentedVolume(out IReadOnlyList<(string Name, byte[] Data)> files) {
    var built = new List<(string Name, byte[] Data)>();
    var writer = new TFatWriter();
    for (var k = 0; k < 6; ++k) {
      var data = Payload(k, 8 * 1024 + k * 1024);
      writer.AddFile($"F{k}.BIN", data);
      built.Add(($"F{k}.BIN", data));
    }

    var image = new MemoryStream();
    var bytes = writer.BuildAutoSized();
    image.Write(bytes, 0, bytes.Length);

    var descriptor = new TFatFormatDescriptor();
    image.Position = 0;
    descriptor.Remove(image, ["F1.BIN", "F3.BIN"]);

    var scratch = new List<string>();
    try {
      var inputs = new List<ArchiveInputInfo>();
      foreach (var index in new[] { 1, 3 }) {
        var path = Path.Combine(Path.GetTempPath(), "cwb_tfat_" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllBytes(path, built[index].Data);
        scratch.Add(path);
        inputs.Add(new ArchiveInputInfo(path, built[index].Name, false));
      }

      image.Position = 0;
      descriptor.Add(image, inputs);
    } finally {
      foreach (var path in scratch) File.Delete(path);
    }

    files = built;
    return image;
  }

  /// <summary>Where the two FAT regions sit, and how long each is.</summary>
  private static (long First, long Second, long Length) FatRegions(byte[] image) {
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11));
    if (bytesPerSector is 0 or > 4096) bytesPerSector = 512;
    var reserved = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(14));
    var small = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(22));
    var fatSectors = small == 0 ? BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(36)) : small;

    var first = (long)reserved * bytesPerSector;
    var length = (long)fatSectors * bytesPerSector;
    return (first, first + length, length);
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_KeepsTheMarkersAndEveryPayload(DefragMode mode) {
    using var image = FragmentedVolume(out var files);

    var before = image.ToArray();
    var (first, second, regionLength) = FatRegions(before);
    var firstSequence = BinaryPrimitives.ReadUInt32BigEndian(before.AsSpan((int)(first + regionLength - 4)));
    var secondSequence = BinaryPrimitives.ReadUInt32BigEndian(before.AsSpan((int)(second + regionLength - 4)));
    Assert.That(secondSequence, Is.Not.EqualTo(firstSequence),
      "the two copies are told apart by their sequence numbers");

    image.Position = 0;
    new TFatFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });

    var after = image.ToArray();
    Assert.That(after, Has.Length.EqualTo(before.Length), "the volume keeps its size");

    // The tag and the transactional byte live at one of two places depending on
    // which extended BPB the volume uses; whichever it was, it is still there.
    var tag = Encoding.ASCII.GetString(after, 54, 8);
    var tag32 = Encoding.ASCII.GetString(after, 82, 8);
    Assert.That(tag.StartsWith("TFAT", StringComparison.Ordinal)
      || tag32.StartsWith("TFAT", StringComparison.Ordinal),
      "the boot sector must still say the volume is TFAT");
    Assert.That(after[37] == 0x01 || after[65] == 0x01,
      "the transactional marker byte must still be set");

    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan((int)(first + regionLength - 4))),
        Is.EqualTo(firstSequence), "the first copy keeps the sequence it had");
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan((int)(second + regionLength - 4))),
        Is.EqualTo(secondSequence), "the second copy keeps the sequence it had");
    });

    // The chains are written to every copy, so the two hold the same allocation
    // once the pass is over — a fresh volume looks exactly like this.
    var firstChains = after.AsSpan((int)first, (int)regionLength - 4).ToArray();
    var secondChains = after.AsSpan((int)second, (int)regionLength - 4).ToArray();
    Assert.That(secondChains, Is.EqualTo(firstChains),
      "both FAT copies must describe the same allocation after a layout pass");

    image.Position = 0;
    using var reader = new TFatReader(image, leaveOpen: true);
    foreach (var (name, data) in files) {
      var entry = reader.Entries.FirstOrDefault(
        e => !e.IsDirectory && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
      Assert.That(entry, Is.Not.Null, $"{name} must still be in the directory");
      Assert.That(reader.Extract(entry!), Is.EqualTo(data), $"{name} must read back byte for byte");
    }
  }
}
