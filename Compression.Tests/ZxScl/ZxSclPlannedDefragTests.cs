#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.ZxScl;

namespace Compression.Tests.ZxScl;

/// <summary>
/// An SCL container is laid out again by moving payloads, and the directory is
/// put back into the order they landed in.
/// </summary>
/// <remarks>
/// Position is implied by order here — the directory records a length in
/// sectors and nothing else, and the reader adds those lengths to a cursor that
/// starts where the directory ends. So the only layout the walk reaches is the
/// packed one, which is what a container we wrote already is: the pass over one
/// of those must find nothing to move and leave every byte where it was.
/// </remarks>
[TestFixture]
public class ZxSclPlannedDefragTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 17 + seed * 41) % 251);
    return data;
  }

  private static MemoryStream Container(out Dictionary<string, byte[]> files) {
    var w = new ZxSclWriter();
    files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var k = 0; k < 5; ++k) {
      var data = Payload(k, 300 + k * 700);
      w.AddFile($"F{k}.cod", data);
      files[$"F{k}.cod"] = data;
    }

    return new MemoryStream(w.Build()) { Position = 0 };
  }

  /// <summary>What the reader gives back, keyed by name and trimmed to the true length.</summary>
  private static Dictionary<string, byte[]> ReadBack(MemoryStream image, Dictionary<string, byte[]> expected) {
    image.Position = 0;
    using var reader = new ZxSclReader(image);
    return reader.Entries.ToDictionary(
      e => e.Name,
      e => reader.Extract(e).Take(expected.TryGetValue(e.Name, out var d) ? d.Length : 0).ToArray(),
      StringComparer.Ordinal);
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_KeepsEveryPayload(DefragMode mode) {
    using var image = Container(out var files);

    image.Position = 0;
    new ZxSclFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });

    var read = ReadBack(image, files);
    foreach (var (name, data) in files) {
      Assert.That(read.Keys, Does.Contain(name), $"{name} must still be listed");
      Assert.That(read[name], Is.EqualTo(data), $"{name} must read back byte for byte");
    }
  }

  /// <summary>
  /// A container we wrote is packed already, so the pass has nothing to do —
  /// and doing nothing has to mean exactly that, not rewriting it to the same
  /// shape with a different checksum or a shuffled directory.
  /// </summary>
  [Test]
  public void Defragment_OfAPackedContainer_ChangesNoByteAndIsNotARebuild() {
    using var image = Container(out _);
    var before = image.ToArray();
    var said = new List<string>();

    image.Position = 0;
    new ZxSclFormatDescriptor().Defragment(image, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      OnProgress = e => { if (e.Status != null) said.Add(e.Status); },
    });

    Assert.That(image.ToArray(), Is.EqualTo(before), "a packed container comes back byte for byte");
    Assert.That(said, Does.Contain("Already defragmented"),
      "the pass must reach that answer by planning, not by writing the container out again");
  }

  /// <summary>
  /// The payloads follow the directory with nothing between them, before and
  /// after. A gap anywhere would move every file behind it away from where the
  /// directory implies it is.
  /// </summary>
  [Test]
  public void Defragment_LeavesThePayloadsPackedAgainstTheDirectory() {
    using var image = Container(out _);

    image.Position = 0;
    new ZxSclFormatDescriptor().Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });

    image.Position = 0;
    var runs = ZxSclRecordMap.Enumerate(image).ToList();
    Assert.That(runs.Any(r => r.Kind == DefragBlockKind.Free), Is.False,
      "nothing may be left unaccounted for between the payloads");

    var used = runs.Where(r => r.Kind == DefragBlockKind.Used).OrderBy(r => r.Offset).ToList();
    Assert.That(used, Has.Count.EqualTo(5), "every file must still have a run");

    var cursor = runs.First(r => r.Kind == DefragBlockKind.MetadataReserved).Length;
    foreach (var run in used) {
      Assert.That(run.Offset, Is.EqualTo(cursor), "each payload follows the one before it");
      cursor += run.Length;
    }
  }

  /// <summary>
  /// Removing a file physically closes the gap it left, so a container stays
  /// packed across the edit and the pass over it still has nothing to do.
  /// </summary>
  [Test]
  public void Defragment_AfterARemoval_StillFindsNothingToMove() {
    using var image = Container(out var files);
    files.Remove("F2.cod");

    image.Position = 0;
    Assert.That(ZxSclInPlaceModifier.RemoveFile(image, "F2.cod"), Is.True, "the file must be removed");

    var before = image.ToArray();
    image.Position = 0;
    new ZxSclFormatDescriptor().Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    Assert.That(image.ToArray(), Is.EqualTo(before), "removal leaves nothing behind to close up");

    var read = ReadBack(image, files);
    foreach (var (name, data) in files)
      Assert.That(read[name], Is.EqualTo(data), $"{name} must read back byte for byte");
  }
}
