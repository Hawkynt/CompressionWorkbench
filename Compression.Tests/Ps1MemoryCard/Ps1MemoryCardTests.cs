using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Tests.Ps1MemoryCard;

[TestFixture]
public sealed class Ps1MemoryCardTests {
  private const int FrameSize = 128;
  private const int BlockSize = 8192;
  private const int CardSize = 131072;

  [SetUp]
  public void SetUp() => Compression.Lib.FormatRegistration.EnsureInitialized();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void OneBank_RoundTripsWholeSaveBlock() {
    var data = Enumerable.Range(0, BlockSize).Select(i => (byte)(i * 17)).ToArray();
    using var image = Create([ArchiveInputInfo.InMemory("BASLUS-00001SAVE", data)]);

    Assert.That(image.Length, Is.EqualTo(CardSize));
    var ops = Ops();
    var entries = ops.List(image, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("BASLUS-00001SAVE"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(BlockSize));

    image.Position = 0;
    using var entry = ops.OpenEntry(image, entries[0].Name, null);
    using var copy = new MemoryStream();
    entry.CopyTo(copy);
    Assert.That(copy.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void ArbitraryInput_IsTruthfullyPaddedToEightKiBSaveUnit() {
    var data = "small-save"u8.ToArray();
    using var image = Create([ArchiveInputInfo.InMemory("SMALL", data)]);
    var ops = Ops();

    var entryInfo = ops.List(image, null).Single();
    Assert.That(entryInfo.OriginalSize, Is.EqualTo(BlockSize));
    image.Position = 0;
    using var entry = ops.OpenEntry(image, "SMALL", null);
    using var copy = new MemoryStream();
    entry.CopyTo(copy);
    var actual = copy.ToArray();
    Assert.That(actual, Has.Length.EqualTo(BlockSize));
    Assert.That(actual.AsSpan(0, data.Length).ToArray(), Is.EqualTo(data));
    Assert.That(actual.AsSpan(data.Length).ToArray(), Is.All.EqualTo((byte)0));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void FourBankCard_PreservesIndependentBankNamespaces() {
    using var image = Create(
      [
        ArchiveInputInfo.InMemory("bank01/SAME", new byte[BlockSize]),
        ArchiveInputInfo.InMemory("bank04/SAME", Enumerable.Repeat((byte)0x44, BlockSize).ToArray()),
      ],
      banks: "4");

    Assert.That(image.Length, Is.EqualTo(4L * CardSize));
    var entries = Ops().List(image, null).Select(e => e.Name).ToArray();
    Assert.That(entries, Is.EquivalentTo(new[] { "bank01/SAME", "bank04/SAME" }));
  }

  [Test, Category("HappyPath")]
  public void Shrink_RemovesOnlyUnusedTrailingBanks() {
    using var image = Create(
      [
        ArchiveInputInfo.InMemory("bank01/A", new byte[BlockSize]),
        ArchiveInputInfo.InMemory("bank02/B", new byte[BlockSize]),
      ],
      banks: "4");
    using var shrunk = new MemoryStream();

    ((IArchiveShrinkable)Descriptor()).Shrink(image, shrunk);

    Assert.That(shrunk.Length, Is.EqualTo(2L * CardSize));
    shrunk.Position = 0;
    Assert.That(Ops().List(shrunk, null).Select(e => e.Name),
      Is.EquivalentTo(new[] { "bank01/A", "bank02/B" }));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Defragment_CompactsChainWithoutChangingCardCapacity() {
    var data = Enumerable.Range(0, 3 * BlockSize).Select(i => (byte)(i * 29 + 7)).ToArray();
    using var canonical = Create([ArchiveInputInfo.InMemory("THREEBLOCKS", data)]);
    var raw = canonical.ToArray();

    // Re-home the middle chain block from directory/data slot 1 to slot 9.
    raw.AsSpan(2 * BlockSize, BlockSize).CopyTo(raw.AsSpan(10 * BlockSize, BlockSize));
    raw.AsSpan(2 * FrameSize, FrameSize).CopyTo(raw.AsSpan(10 * FrameSize, FrameSize));
    BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(FrameSize + 8, 2), 9);
    Stamp(raw.AsSpan(FrameSize, FrameSize));
    raw.AsSpan(2 * FrameSize, FrameSize).Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(2 * FrameSize, 4), 0xA0);
    BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(2 * FrameSize + 8, 2), 0xFFFF);
    Stamp(raw.AsSpan(2 * FrameSize, FrameSize));

    using var fragmented = new MemoryStream(raw, writable: true);
    Assert.That(Ops().List(fragmented, null), Has.Count.EqualTo(1));

    ((IArchiveDefragmentable)Descriptor()).Defragment(fragmented);

    Assert.That(fragmented.Length, Is.EqualTo(CardSize));
    var compacted = fragmented.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(compacted.AsSpan(FrameSize + 8, 2)), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(compacted.AsSpan(2 * FrameSize + 8, 2)), Is.EqualTo(2));
    fragmented.Position = 0;
    using var entry = Ops().OpenEntry(fragmented, "THREEBLOCKS", null);
    using var copy = new MemoryStream();
    entry.CopyTo(copy);
    Assert.That(copy.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Remove_MarksDirectoryDeletedAndLeavesRecoverablePayloadUntilWipe() {
    var data = Enumerable.Repeat((byte)0x6D, BlockSize).ToArray();
    using var image = Create([ArchiveInputInfo.InMemory("RECOVER", data)]);

    ((IArchiveModifiable)Descriptor()).Remove(image, ["RECOVER"]);

    image.Position = 0;
    Assert.That(Ops().List(image, null), Is.Empty);
    var raw = image.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(FrameSize, 4)), Is.EqualTo(0xA1u));
    Assert.That(raw.AsSpan(BlockSize, BlockSize).ToArray(), Is.EqualTo(data));

    ((IWipeEmpty)Descriptor()).WipeUnusedSpace(image, wipeClusterTips: true, wipeDeletedEntries: true);
    raw = image.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(FrameSize, 4)), Is.EqualTo(0xA0u));
    Assert.That(raw.AsSpan(BlockSize, BlockSize).ToArray(), Is.All.EqualTo((byte)0xFF));
  }

  [Test, Category("ErrorHandling")]
  public void MultiBank_UnqualifiedDuplicateNameIsAmbiguousForReplacement() {
    using var image = Create(
      [
        ArchiveInputInfo.InMemory("bank01/SAME", new byte[BlockSize]),
        ArchiveInputInfo.InMemory("bank02/SAME", new byte[BlockSize]),
      ],
      banks: "2");

    var ex = Assert.Throws<InvalidDataException>(() =>
      ((IArchiveModifiable)Descriptor()).Add(image,
        [ArchiveInputInfo.InMemory("SAME", Enumerable.Repeat((byte)1, BlockSize).ToArray())]));
    Assert.That(ex!.Message, Does.Contain("multiple PS1 banks"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ExplicitBankReplacementAndRemoval_AffectOnlyNamedBank() {
    using var image = Create(
      [
        ArchiveInputInfo.InMemory("bank01/SAME", Enumerable.Repeat((byte)1, BlockSize).ToArray()),
        ArchiveInputInfo.InMemory("bank02/SAME", Enumerable.Repeat((byte)2, BlockSize).ToArray()),
      ],
      banks: "2");
    var modifier = (IArchiveModifiable)Descriptor();

    modifier.Add(image,
      [ArchiveInputInfo.InMemory("bank02/SAME", Enumerable.Repeat((byte)3, BlockSize).ToArray())]);
    modifier.Remove(image, ["bank01/SAME"]);

    image.Position = 0;
    var list = Ops().List(image, null);
    Assert.That(list.Select(e => e.Name), Is.EqualTo(new[] { "bank02/SAME" }));
    image.Position = 0;
    using var entry = Ops().OpenEntry(image, "bank02/SAME", null);
    using var copy = new MemoryStream();
    entry.CopyTo(copy);
    Assert.That(copy.ToArray(), Is.All.EqualTo((byte)3));
  }

  [Test, Category("ErrorHandling")]
  public void BadDirectoryChecksum_IsRejected() {
    using var image = Create([]);
    var raw = image.ToArray();
    raw[FrameSize + 10] ^= 0x40;
    using var damaged = new MemoryStream(raw, writable: false);

    Assert.Throws<InvalidDataException>(() => Ops().List(damaged, null));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesRwAndMaintenanceContracts() {
    var descriptor = Descriptor();
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
    Assert.That(descriptor, Is.InstanceOf<IArchivePurgeable>());
    Assert.That(descriptor, Is.InstanceOf<IFilesystemExtentMap>());
    Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
  }

  private static IFormatDescriptor Descriptor()
    => FormatRegistry.GetById("Ps1MemoryCard")
       ?? throw new AssertionException("Ps1MemoryCard descriptor was not registered.");

  private static IArchiveFormatOperations Ops()
    => FormatRegistry.GetArchiveOps("Ps1MemoryCard")
       ?? throw new AssertionException("Ps1MemoryCard operations were not registered.");

  private static MemoryStream Create(IReadOnlyList<ArchiveInputInfo> inputs, string banks = "Auto") {
    var output = new MemoryStream();
    ((IArchiveCreatable)Ops()).Create(output, inputs,
      new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string>(StringComparer.Ordinal) { ["Banks"] = banks },
      });
    output.Position = 0;
    return output;
  }

  private static void Stamp(Span<byte> frame) {
    byte checksum = 0;
    for (var i = 0; i < frame.Length - 1; ++i) checksum ^= frame[i];
    frame[^1] = checksum;
  }
}
