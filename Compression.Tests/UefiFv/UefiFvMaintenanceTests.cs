#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.UefiFv;

namespace Compression.Tests.UefiFv;

[TestFixture]
public sealed class UefiFvMaintenanceTests {
  private const string First = "11111111-1111-1111-1111-111111111111_RAW.bin";
  private const string Second = "22222222-2222-2222-2222-222222222222_DRIVER.bin";
  private const string Third = "33333333-3333-3333-3333-333333333333_APPLICATION.bin";

  [Test, Category("RoundTrip")]
  public void Defrag_ClosesErasedGapPreservesHeaderCapacityAndContents() {
    var descriptor = new UefiFvFormatDescriptor();
    using var image = new MemoryStream();
    descriptor.Create(image, [
      ArchiveInputInfo.InMemory(First, Enumerable.Repeat((byte)0x11, 257).ToArray()),
      ArchiveInputInfo.InMemory(Second, Enumerable.Repeat((byte)0x22, 513).ToArray()),
      ArchiveInputInfo.InMemory(Third, Enumerable.Repeat((byte)0x33, 1025).ToArray()),
    ], new FormatCreateOptions());

    var originalLength = image.Length;
    var originalBytes = image.ToArray();
    var originalFv = UefiFvReader.Read(originalBytes);
    var headerLength = originalFv.Header.HeaderLength;
    var originalHeader = originalBytes.AsSpan(0, headerLength).ToArray();

    ((IArchiveModifiable)descriptor).Remove(image, [First]);
    var fragmented = image.ToArray();
    var dataStart = Align8(headerLength);
    Assert.That(fragmented.AsSpan(dataStart, 8).ToArray(), Is.EqualTo(Enumerable.Repeat((byte)0xFF, 8).ToArray()),
      "removing the first FFS record should leave an erased gap at the start of the data area");

    ((IArchiveDefragmentable)descriptor).Defragment(image);

    var compacted = image.ToArray();
    Assert.That(image.Length, Is.EqualTo(originalLength), "firmware-volume capacity is fixed");
    Assert.That(compacted.AsSpan(0, headerLength).ToArray(), Is.EqualTo(originalHeader),
      "FV header and block map must remain byte-identical");

    var fv = UefiFvReader.Read(compacted);
    Assert.That(fv.Files, Has.Count.EqualTo(2));
    Assert.That(fv.Files[0].Name, Is.EqualTo(Guid.Parse("22222222-2222-2222-2222-222222222222")));
    Assert.That(fv.Files[0].Type, Is.EqualTo(0x07));
    Assert.That(fv.Files[0].Contents, Is.EqualTo(Enumerable.Repeat((byte)0x22, 513).ToArray()));
    Assert.That(fv.Files[1].Name, Is.EqualTo(Guid.Parse("33333333-3333-3333-3333-333333333333")));
    Assert.That(fv.Files[1].Type, Is.EqualTo(0x09));
    Assert.That(fv.Files[1].Contents, Is.EqualTo(Enumerable.Repeat((byte)0x33, 1025).ToArray()));

    Assert.That(compacted[dataStart], Is.Not.EqualTo(0xFF), "first live FFS record should move into the erased gap");
    var usedEnd = dataStart + fv.Files.Sum(file => Align8(checked((int)file.Size)));
    var fvEnd = checked((int)fv.Header.FvLength);
    Assert.That(compacted.AsSpan(usedEnd, fvEnd - usedEnd).ToArray(),
      Is.All.EqualTo((byte)0xFF), "all capacity after the compacted live records must be restored to erased state");
  }

  private static int Align8(int value) => (value + 7) & ~7;
}
