#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.TahoeLafs;

namespace Compression.Tests.TahoeLafs;

[TestFixture]
public sealed class TahoeLafsConnectionMaintenanceTests {
  private static byte[] Document() => new TahoeLafsConnection(
    new Uri("http://127.0.0.1:3456/"),
    TahoeLafsCapability.Parse("URI:DIR2:write:fingerprint")).Serialize();

  [Test]
  public void CapabilityDocument_MaintenanceIsByteExactAndHasNoWipeableRegion() {
    var descriptor = new TahoeLafsFormatDescriptor();
    var original = Document();

    using var defrag = new MemoryStream((byte[])original.Clone(), writable: true);
    ((IArchiveDefragmentable)descriptor).Defragment(defrag);
    Assert.That(defrag.ToArray(), Is.EqualTo(original));

    using var source = new MemoryStream(original, writable: false);
    using var shrunk = new MemoryStream();
    ((IArchiveShrinkable)descriptor).Shrink(source, shrunk);
    Assert.That(shrunk.ToArray(), Is.EqualTo(original));

    using var layoutSource = new MemoryStream(original, writable: false);
    var layout = ((IArchiveLayoutMap)descriptor).EnumerateLayout(layoutSource).ToArray();
    Assert.That(layout, Has.Length.EqualTo(1));
    Assert.That(layout[0].Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
    Assert.That(layout[0].Length, Is.EqualTo(original.Length));

    using var wipe = new MemoryStream((byte[])original.Clone(), writable: true);
    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(wipe);
    Assert.That(wiped, Is.Zero);
    Assert.That(wipe.ToArray(), Is.EqualTo(original));
  }
}
