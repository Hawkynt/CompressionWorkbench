#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// A data fork is up to eight extents in its catalog record (more in the extents
/// overflow file). The reader used to read only the first and return zeros for the
/// rest, so every fragmented file extracted wrong — and every rebuild that went
/// through the reader wrote those zeros back. The layout map likewise named only
/// the first extent, so a wipe zeroed the rest of the file.
/// </summary>
[TestFixture]
public class HfsPlusFragmentedForkTests {

  private const uint BlockSize = 4096;

  /// <summary>
  /// A volume holding one two-block file whose blocks are swapped on disk and
  /// described by two extents in the right logical order.
  /// </summary>
  private static byte[] FragmentedVolume(out byte[] payload) {
    payload = new byte[2 * BlockSize];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i / BlockSize == 0 ? 0xA1 : 0xB2);
    payload[0] = 1; payload[BlockSize] = 2;
    var w = new HfsPlusWriter();
    w.AddFile("split.bin", payload);
    var image = w.Build(BlockSize);

    var entry = new HfsPlusReader(new MemoryStream(image)).Entries.Single(e => e.Name == "split.bin");
    var start = entry.FirstBlock;
    Assert.That(entry.BlockCount, Is.EqualTo(2u), "precondition: the writer stores the file as one run of two blocks");

    // Find the fork's first extent descriptor (start, count) in the catalog record.
    var pattern = new byte[8];
    BinaryPrimitives.WriteUInt32BigEndian(pattern, start);
    BinaryPrimitives.WriteUInt32BigEndian(pattern.AsSpan(4), 2);
    var at = image.AsSpan().IndexOf(pattern);
    Assert.That(at, Is.GreaterThan(0), "the extent descriptor was not found");

    // Swap the two blocks on disk and describe them as (start+1, 1), (start, 1).
    var first = image.AsSpan((int)(start * BlockSize), (int)BlockSize).ToArray();
    image.AsSpan((int)((start + 1) * BlockSize), (int)BlockSize).CopyTo(image.AsSpan((int)(start * BlockSize)));
    first.CopyTo(image, (int)((start + 1) * BlockSize));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(at), start + 1);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(at + 4), 1);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(at + 8), start);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(at + 12), 1);
    return image;
  }

  [Test]
  public void GivenAFileInTwoExtents_WhenExtracted_ThenBothExtentsAreRead() {
    var image = FragmentedVolume(out var payload);
    var reader = new HfsPlusReader(new MemoryStream(image));
    var entry = reader.Entries.Single(e => e.Name == "split.bin");
    Assert.That(reader.Extract(entry), Is.EqualTo(payload));
  }

  [Test]
  public void GivenAFileInTwoExtents_WhenUnusedSpaceIsWiped_ThenTheSecondExtentSurvives() {
    var image = FragmentedVolume(out var payload);
    using var ms = new MemoryStream();
    ms.Write(image);
    new HfsPlusFormatDescriptor().WipeUnusedSpace(ms);
    ms.Position = 0;
    var reader = new HfsPlusReader(ms, leaveOpen: true);
    Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "split.bin")), Is.EqualTo(payload));
  }

  [Test]
  public void GivenAFileInTwoExtents_WhenMapped_ThenBothExtentsAreNamed() {
    var image = FragmentedVolume(out _);
    var extents = HfsPlusExtentMap.Enumerate(new MemoryStream(image))
      .Where(e => e.Kind == DefragBlockKind.Used && e.FileName == "split.bin").ToList();
    Assert.That(extents, Has.Count.EqualTo(2));
  }
}
