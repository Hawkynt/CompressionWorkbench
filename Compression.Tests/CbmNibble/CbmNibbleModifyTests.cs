using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.CbmNibble;

namespace Compression.Tests.CbmNibble;

[TestFixture]
public sealed class CbmNibbleModifyTests {
  [Test, Category("RoundTrip")]
  public void G64_DirectTracks_AddReplaceRemoveDefragAndPurge() {
    var descriptor = new G64FormatDescriptor();
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);

    var a = Enumerable.Range(0, 113).Select(i => (byte)(i * 17)).ToArray();
    var b = Enumerable.Range(0, 227).Select(i => (byte)(255 - i)).ToArray();
    using var image = new MemoryStream();
    descriptor.Create(image, [
      ArchiveInputInfo.InMemory("track_00.bin", a),
      ArchiveInputInfo.InMemory("track_03.bin", b),
    ], new FormatCreateOptions());

    AssertTrack(descriptor, image, "track_00.bin", a);
    AssertTrack(descriptor, image, "track_03.bin", b);

    var replacement = Enumerable.Repeat((byte)0xA5, 181).ToArray();
    var added = Enumerable.Repeat((byte)0x3C, 97).ToArray();
    descriptor.Add(image, [
      ArchiveInputInfo.InMemory("track_00.bin", replacement),
      ArchiveInputInfo.InMemory("track_05.bin", added),
    ]);
    AssertTrack(descriptor, image, "track_00.bin", replacement);
    AssertTrack(descriptor, image, "track_03.bin", b);
    AssertTrack(descriptor, image, "track_05.bin", added);

    descriptor.Remove(image, ["track_03.bin"]);
    var names = ListNames(descriptor, image);
    Assert.That(names, Does.Not.Contain("track_03.bin"));
    Assert.That(names, Does.Contain("track_00.bin"));
    Assert.That(names, Does.Contain("track_05.bin"));

    descriptor.Defragment(image, new DefragOptions());
    AssertTrack(descriptor, image, "track_00.bin", replacement);
    AssertTrack(descriptor, image, "track_05.bin", added);

    descriptor.Purge(image);
    names = ListNames(descriptor, image);
    Assert.That(names.Where(n => n.StartsWith("track_", StringComparison.Ordinal)), Is.Empty);
    Assert.That(names, Does.Contain("metadata.ini"));
  }

  [Test, Category("EdgeCase")]
  public void G64_VariableSpeedMap_FailsClosedForMutationAndWipe() {
    var descriptor = new G64FormatDescriptor();
    using var image = new MemoryStream();
    descriptor.Create(image, [ArchiveInputInfo.InMemory("track_00.bin", new byte[] { 1, 2, 3, 4 })],
      new FormatCreateOptions());

    var bytes = image.ToArray();
    var trackCount = bytes[9];
    var speedTable = 12 + trackCount * 4;
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(speedTable, 4), 0x100);
    image.Position = 0;
    image.SetLength(0);
    image.Write(bytes);

    Assert.That(() => descriptor.Add(image,
      [ArchiveInputInfo.InMemory("track_00.bin", new byte[] { 9, 8, 7 })]),
      Throws.InstanceOf<NotSupportedException>());

    var before = image.ToArray();
    Assert.That(descriptor.WipeUnusedSpace(image), Is.EqualTo(0));
    Assert.That(image.ToArray(), Is.EqualTo(before));
  }

  [Test, Category("Maintenance")]
  public void G64_Wipe_ClearsOnlyUnreferencedTrailingBytes() {
    var descriptor = new G64FormatDescriptor();
    using var image = new MemoryStream();
    var track = Enumerable.Repeat((byte)0x55, 64).ToArray();
    descriptor.Create(image, [ArchiveInputInfo.InMemory("track_00.bin", track)], new FormatCreateOptions());
    var liveLength = image.Length;
    image.Position = image.Length;
    image.Write(Enumerable.Repeat((byte)0xCC, 128).ToArray());

    var wiped = descriptor.WipeUnusedSpace(image);
    Assert.That(wiped, Is.GreaterThanOrEqualTo(128));
    Assert.That(image.ToArray().AsSpan((int)liveLength, 128).ToArray(), Is.All.EqualTo((byte)0));
    AssertTrack(descriptor, image, "track_00.bin", track);
  }

  [Test, Category("RoundTrip")]
  public void Nib_DirectTracks_UseFixedSlotsForReplaceRemoveAndPurge() {
    var descriptor = new NibFormatDescriptor();
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);

    var a = Enumerable.Repeat((byte)0x55, CbmNibbleReader.NibTrackSize).ToArray();
    var b = Enumerable.Repeat((byte)0xAA, CbmNibbleReader.NibTrackSize).ToArray();
    using var image = new MemoryStream();
    descriptor.Create(image, [
      ArchiveInputInfo.InMemory("track_00.bin", a),
      ArchiveInputInfo.InMemory("track_17.bin", b),
    ], new FormatCreateOptions());

    Assert.That(image.Length, Is.EqualTo(CbmNibbleReader.NibExpectedFileSize));
    AssertTrack(descriptor, image, "track_00.bin", a);
    AssertTrack(descriptor, image, "track_17.bin", b);

    var replacement = Enumerable.Repeat((byte)0x6D, CbmNibbleReader.NibTrackSize).ToArray();
    descriptor.Add(image, [ArchiveInputInfo.InMemory("track_17.bin", replacement)]);
    AssertTrack(descriptor, image, "track_00.bin", a);
    AssertTrack(descriptor, image, "track_17.bin", replacement);

    var beforeDefrag = image.ToArray();
    descriptor.Defragment(image, new DefragOptions());
    Assert.That(image.ToArray(), Is.EqualTo(beforeDefrag), "Fixed-slot NIB defrag should be a true no-op.");

    descriptor.Remove(image, ["track_00.bin"]);
    Assert.That(ListNames(descriptor, image), Does.Not.Contain("track_00.bin"));
    AssertTrack(descriptor, image, "track_17.bin", replacement);

    descriptor.Purge(image);
    Assert.That(image.ToArray(), Is.All.EqualTo((byte)0));
    Assert.That(ListNames(descriptor, image).Where(n => n.StartsWith("track_", StringComparison.Ordinal)), Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void Nib_RejectsNonSlotSizedTrackReplacementWithoutChangingImage() {
    var descriptor = new NibFormatDescriptor();
    using var image = new MemoryStream();
    descriptor.Create(image, [], new FormatCreateOptions());
    var before = image.ToArray();

    Assert.That(() => descriptor.Add(image,
      [ArchiveInputInfo.InMemory("track_01.bin", new byte[123])]),
      Throws.InstanceOf<NotSupportedException>());
    Assert.That(image.ToArray(), Is.EqualTo(before));
  }

  private static HashSet<string> ListNames(IArchiveFormatOperations descriptor, MemoryStream image) {
    image.Position = 0;
    return descriptor.List(image, null).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
  }

  private static void AssertTrack(IArchiveFormatOperations descriptor, MemoryStream image,
      string name, byte[] expected) {
    image.Position = 0;
    var actual = descriptor.ExtractEntryToMemory(image, name, null);
    Assert.That(actual, Is.EqualTo(expected), name);
  }
}
