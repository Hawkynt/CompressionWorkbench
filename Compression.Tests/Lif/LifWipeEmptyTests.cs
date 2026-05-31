#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Lif;

namespace Compression.Tests.Lif;

/// <summary>
/// Unused-space wiping for the HP LIF descriptor. LIF stores each file as a
/// contiguous run of 256-byte sectors, but the directory entry records the
/// file length only in whole sectors — there is no byte-precise logical size on
/// disk, so a file always exactly fills its allocated sectors and there is no
/// recoverable cluster tip. Tip wiping is therefore N/A; the wiper zeros only
/// the free (unallocated) sectors between and after files.
/// </summary>
[TestFixture]
public class LifWipeEmptyTests {

  private const int SectorSize = 256;

  [Test]
  public void LifDescriptorImplementsIWipeEmpty() {
    var desc = new LifFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("RoundTrip")]
  public void WipeEmpty_FreeSectorZeroed_AndFileRoundTrips() {
    // Two files packed contiguously (LIF has no allocation bitmap; the writer
    // sizes the image to exactly fit). Removing the first file frees its
    // sectors, producing a free gap we can dirty and prove the wipe zeroes.
    var first = new byte[SectorSize - 30];
    Array.Fill(first, (byte)0xBB);
    var keep = new byte[SectorSize - 50];
    Array.Fill(keep, (byte)0xAA);
    var image = LifWriter.Build([("GONE", first), ("DATA", keep)]);

    var desc = new LifFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    ms.Position = 0;
    LifModifier.RemoveFile(ms, "GONE", wipeData: true);

    // Locate the freed sector and dirty it so we can prove the wipe cleans it.
    ms.Position = 0;
    var free = desc.EnumerateExtents(ms)
      .First(e => e.Kind == DefragBlockKind.Free && e.Length >= SectorSize);
    var dirtyOff = free.Offset;
    var dirtyLen = (int)Math.Min(free.Length, SectorSize);
    ms.Position = dirtyOff;
    var junk = new byte[dirtyLen];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk, 0, junk.Length);

    desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    ms.Position = dirtyOff;
    var region = new byte[dirtyLen];
    ms.Read(region, 0, region.Length);
    Assert.That(region, Is.All.EqualTo((byte)0), "free sectors must be zeroed");

    // The surviving file still reads back (tip wiping is N/A for LIF).
    ms.Position = 0;
    var v = LifReader.Read(ms.ToArray());
    var entry = v.Files.First(f => f.Name == "DATA");
    var extracted = LifReader.Extract(v, entry);
    Assert.That(extracted.AsSpan(0, keep.Length).ToArray(), Is.EqualTo(keep),
      "surviving file content survives the wipe intact");
  }
}
