#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Nilfs2;

namespace Compression.Tests.Nilfs2;

/// <summary>
/// Nilfs2 lays a volume out again by moving payloads inside the one area where
/// that can be expressed: the base segment's own.
/// </summary>
/// <remarks>
/// A payload's position is an offset from the start of the segment describing
/// it, so a move is that one field. It cannot go below where those payloads
/// start — a negative offset the format has no way to write — and it cannot
/// reach past the first appended segment, whose header the reader finds by
/// carrying on from where they end. Which is where the holes are anyway:
/// removing a file writes a tombstone into a new segment and leaves the bytes
/// it had unclaimed.
/// </remarks>
[TestFixture]
[Category("Slow")]
public class Nilfs2PlannedDefragTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  private static MemoryStream Volume(out Dictionary<string, byte[]> kept) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_Nilfs2_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    kept = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    try {
      var inputs = new List<ArchiveInputInfo>();
      for (var k = 0; k < 6; ++k) {
        var data = Payload(k, 5000 + k * 2000);
        var path = Path.Combine(work, $"F{k}.BIN");
        File.WriteAllBytes(path, data);
        inputs.Add(new ArchiveInputInfo(path, $"F{k}.BIN", false));
        kept[$"F{k}.BIN"] = data;
      }

      var image = new MemoryStream();
      var descriptor = new Nilfs2FormatDescriptor();
      descriptor.Create(image, inputs, new FormatCreateOptions());

      // Removing leaves the bytes behind unclaimed, which is the hole a pass
      // closes up.
      image.Position = 0;
      descriptor.Remove(image, ["F1.BIN", "F3.BIN"]);
      kept.Remove("F1.BIN");
      kept.Remove("F3.BIN");
      return image;
    } finally {
      try { Directory.Delete(work, true); } catch { /* scratch is gone already */ }
    }
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_KeepsEveryPayloadAndTheVolumesSize(DefragMode mode) {
    using var image = Volume(out var kept);
    var size = image.Length;

    image.Position = 0;
    new Nilfs2FormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });
    Assert.That(image.Length, Is.EqualTo(size), "a volume keeps its size");

    image.Position = 0;
    using var reader = new Nilfs2Reader(image);
    foreach (var (name, data) in kept) {
      var entry = reader.Entries.FirstOrDefault(e => e.Name == name);
      Assert.That(entry, Is.Not.Null, $"{name} must still be in the directory");
      Assert.That(reader.Extract(entry!), Is.EqualTo(data), $"{name} must read back byte for byte");
    }
  }

  [Test]
  public void Defragment_ActuallyClosesTheHoles() {
    using var image = Volume(out _);
    var descriptor = new Nilfs2FormatDescriptor();

    image.Position = 0;
    var before = descriptor.EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used).Select(e => e.Offset).OrderBy(o => o).ToList();
    Assert.That(before, Is.Not.Empty, "the probe volume must have payloads to move");

    image.Position = 0;
    descriptor.Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    image.Position = 0;
    var after = descriptor.EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used).Select(e => e.Offset).OrderBy(o => o).ToList();

    Assert.That(after, Is.Not.EqualTo(before), "packing from the front must move something");
    Assert.That(after.Max(), Is.LessThan(before.Max()), "and it must move towards the front");
  }
}
