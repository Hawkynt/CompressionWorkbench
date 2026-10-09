using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Reiser4;

namespace Compression.Tests.Reiser4;

[TestFixture]
public sealed class Reiser4NativeTreeTests {

  private static byte[] Payload(int length, int seed) {
    var result = new byte[length];
    for (var i = 0; i < result.Length; ++i)
      result[i] = (byte)(i * 37 + seed * 19 + i / Reiser4Writer.BlockSize);
    return result;
  }

  private static byte[] BuildWithoutLegacyDirectory(params (string Name, byte[] Data)[] files) {
    var writer = new Reiser4Writer();
    foreach (var (name, data) in files)
      writer.AddFile(name, data);

    var image = writer.Build();

    // The old workbench sidecar is announced at master-superblock byte 52 and
    // its first-directory pointer at byte 64. Erasing both makes any successful
    // listing/extraction depend on cde40/stat40/extent40 in the native leaf.
    image.AsSpan((int)Reiser4Reader.MasterOffset + 52, 20).Clear();
    return image;
  }

  [Test, Category("RoundTrip")]
  public void Reader_UsesNativeTree_WhenLegacyMarkerIsAbsent() {
    var shortPayload = Payload(73, 1);
    var indirectPayload = Payload(9_000, 2);
    const string shortName = "short.bin";
    const string hashedName = "very-long-filename-that-needs-hash.bin";
    var image = BuildWithoutLegacyDirectory(
      (shortName, shortPayload),
      (hashedName, indirectPayload));

    using var stream = new MemoryStream(image, writable: false);
    using var reader = new Reiser4Reader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.Valid, Is.True);
      Assert.That(reader.Entries.Select(static entry => entry.Name),
        Is.EquivalentTo(new[] { shortName, hashedName }));
      Assert.That(reader.Extract(reader.Entries.Single(entry => entry.Name == shortName)),
        Is.EqualTo(shortPayload));
      Assert.That(reader.Extract(reader.Entries.Single(entry => entry.Name == hashedName)),
        Is.EqualTo(indirectPayload));
    });
  }

  [Test, Category("RoundTrip")]
  public void NativeExtentRuns_DrivePhysicalLayout() {
    var payload = Payload(12_345, 3);
    var image = BuildWithoutLegacyDirectory(("runs.bin", payload));

    using var stream = new MemoryStream(image, writable: false);
    using var reader = new Reiser4Reader(stream);
    var entry = reader.Entries.Single();
    var runs = reader.EnumerateRuns(entry).ToArray();

    Assert.Multiple(() => {
      Assert.That(runs, Is.Not.Empty);
      Assert.That(runs.Sum(static run => run.Length), Is.EqualTo(payload.LongLength));
      Assert.That(reader.Extract(entry), Is.EqualTo(payload));
    });
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_ListsAndExtractsFromNativeTreeWithoutPrivateMarker() {
    var payload = Payload(4_321, 4);
    var image = BuildWithoutLegacyDirectory(("native-only.bin", payload));
    var descriptor = new Reiser4FormatDescriptor();
    using var stream = new MemoryStream(image, writable: false);

    var listed = descriptor.List(stream, null);
    Assert.That(listed.Select(static entry => entry.Name), Does.Contain("native-only.bin"));

    var output = Path.Combine(Path.GetTempPath(), "reiser4_native_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(output);
    try {
      stream.Position = 0;
      descriptor.Extract(stream, output, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(output, "native-only.bin")), Is.EqualTo(payload));
    } finally {
      try { Directory.Delete(output, recursive: true); } catch { /* best effort */ }
    }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_CreateFromStreams_EmitsNativeTreeWithoutBufferingWholeInput() {
    var payload = Payload(2 * 1024 * 1024 + 37, 5);
    var openCount = 0;
    var descriptor = new Reiser4FormatDescriptor();
    using var image = new MemoryStream();

    descriptor.CreateFromStreams(image, [new Compression.Registry.Streaming.StreamingArchiveInput(
      "streamed.bin", payload.LongLength, false, () => {
        ++openCount;
        return new MemoryStream(payload, writable: false);
      })], new FormatCreateOptions());

    image.Position = 0;
    using var reader = new Reiser4Reader(image);
    Assert.Multiple(() => {
      Assert.That(openCount, Is.EqualTo(1));
      Assert.That(reader.Entries.Select(static entry => entry.Name), Does.Contain("streamed.bin"));
      Assert.That(reader.Extract(reader.Entries.Single()), Is.EqualTo(payload));
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    });
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_DefaultMutation_RebuildsAndRemainsReadable() {
    var original = Payload(5_123, 6);
    var added = Payload(8_765, 7);
    var descriptor = new Reiser4FormatDescriptor();
    using var image = new MemoryStream(BuildWithoutLegacyDirectory(("original.bin", original)));

    ((IArchiveModifiable)descriptor).Add(image,
      [ArchiveInputInfo.InMemory("added.bin", added)]);

    image.Position = 0;
    using var reader = new Reiser4Reader(image);
    Assert.Multiple(() => {
      Assert.That(reader.Entries.Select(static entry => entry.Name),
        Is.EquivalentTo(new[] { "original.bin", "added.bin" }));
      Assert.That(reader.Extract(reader.Entries.Single(static entry => entry.Name == "original.bin")), Is.EqualTo(original));
      Assert.That(reader.Extract(reader.Entries.Single(static entry => entry.Name == "added.bin")), Is.EqualTo(added));
    });
  }

  [TestCase(".", TestName = "GivenTheDotName_WhenAddingToTheWriter_ThenItIsRefused")]
  [TestCase("..", TestName = "GivenTheDotDotName_WhenAddingToTheWriter_ThenItIsRefused")]
  [Category("Exceptional")]
  public void Writer_RefusesNamesTheRootDirectoryCannotHold(string name) {
    var writer = new Reiser4Writer();
    Assert.Multiple(() => {
      Assert.That(() => writer.AddFile(name, [1]), Throws.TypeOf<NotSupportedException>());
      Assert.That(() => writer.AddStreamingFile(name, 1, () => new MemoryStream([1])), Throws.TypeOf<NotSupportedException>());
      Assert.That(new Reiser4FormatDescriptor().CanAccept(ArchiveInputInfo.InMemory(name, [1]), out var reason), Is.False);
    });
  }

  [Test, Category("Exceptional")]
  public void Writer_RefusesPathsBeyondTheReaderDepthLimit() {
    var name = string.Join('/', Enumerable.Repeat("directory", 130));
    var writer = new Reiser4Writer();
    Assert.Multiple(() => {
      Assert.That(() => writer.AddFile(name, [1]), Throws.TypeOf<NotSupportedException>());
      Assert.That(() => writer.AddDirectory(name), Throws.TypeOf<NotSupportedException>());
      Assert.That(new Reiser4FormatDescriptor().CanAccept(ArchiveInputInfo.InMemory(name, [1]), out _), Is.False);
    });
  }

  [Test, Category("Exceptional")]
  public void GivenTheSameNameTwice_WhenAddingToTheWriter_ThenTheSecondIsRefused() {
    var writer = new Reiser4Writer();
    writer.AddFile("twice.bin", [1]);
    Assert.That(() => writer.AddFile("twice.bin", [2]), Throws.ArgumentException);
  }

  [Test, Category("EquivalenceClass")]
  public void GivenADirectoryInput_WhenAskingTheConstraints_ThenItIsAccepted() {
    var accepted = new Reiser4FormatDescriptor().CanAccept(new ArchiveInputInfo("sub", "sub", IsDirectory: true), out var reason);
    Assert.Multiple(() => {
      Assert.That(accepted, Is.True);
      Assert.That(reason, Is.Null);
    });
  }

  [TestCase("plain.bin", TestName = "GivenARootLevelName_WhenAskingTheConstraints_ThenItIsAccepted")]
  [TestCase("a-name-longer-than-twenty-three-characters.bin", TestName = "GivenAHashedLengthRootLevelName_WhenAskingTheConstraints_ThenItIsAccepted")]
  [Category("EquivalenceClass")]
  public void RootLevelNames_AreAccepted(string name)
    => Assert.That(new Reiser4FormatDescriptor().CanAccept(ArchiveInputInfo.InMemory(name, [1]), out _), Is.True);

  [Test, Category("RoundTrip")]
  public void GivenFiles_WhenWriting_ThenExtentsSitInTheTwigAndNotInTheLeaf() {
    var image = BuildWithoutLegacyDirectory(("body.bin", Payload(9_000, 3)));
    static (byte Level, ushort[] Plugins) Node(byte[] image, int block) {
      var node = image.AsSpan(block * Reiser4Writer.BlockSize, Reiser4Writer.BlockSize);
      var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(node[2..]);
      var plugins = new ushort[count];
      for (var i = 0; i < count; ++i)
        plugins[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(node[(Reiser4Writer.BlockSize - (i + 1) * 38 + 36)..]);
      return (node[26], plugins);
    }
    var twig = Node(image, 23);
    var leaf = Node(image, 24);
    Assert.Multiple(() => {
      Assert.That(twig.Level, Is.EqualTo(2));
      Assert.That(twig.Plugins, Is.EqualTo(new ushort[] { 3, 5 }), "twig: the leaf pointer, then the extent");
      Assert.That(leaf.Level, Is.EqualTo(1));
      Assert.That(leaf.Plugins, Does.Not.Contain((ushort)5), "a leaf never holds an extent");
    });
  }


  [Test, Category("RoundTrip")]
  public void Writer_EmitsNestedDirectoriesInTheNativeNamespace() {
    var payload = Payload(4_137, 8);
    var writer = new Reiser4Writer();
    writer.AddFile("one/two/three.bin", payload);
    using var image = new MemoryStream(writer.Build(), writable: false);
    using var reader = new Reiser4Reader(image);

    Assert.Multiple(() => {
      Assert.That(reader.Entries.Where(static entry => entry.IsDirectory).Select(static entry => entry.Name),
        Is.EquivalentTo(new[] { "one", "one/two" }));
      Assert.That(reader.Entries.Single(static entry => entry.Name == "one/two/three.bin").Size,
        Is.EqualTo(payload.Length));
      Assert.That(reader.Extract(reader.Entries.Single(static entry => entry.Name == "one/two/three.bin")),
        Is.EqualTo(payload));
    });
  }

  [Test, Category("RoundTrip")]
  public void Reader_FollowsMultipleInternalNodeLevels() {
    var payload = Payload(2_033, 9);
    var writer = new Reiser4Writer();
    writer.AddFile("deep-tree.bin", payload);
    var image = writer.Build();

    const int blockSize = Reiser4Writer.BlockSize;
    const int rootBlock = 23;
    const int intermediateBlock = 4095;
    image.AsSpan(rootBlock * blockSize, blockSize).CopyTo(image.AsSpan(intermediateBlock * blockSize, blockSize));
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(rootBlock * blockSize + Reiser4Tree.NodeHeaderBytes, 8),
      intermediateBlock);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootBlock * blockSize + 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootBlock * blockSize + 4),
      blockSize - Reiser4Tree.ItemHeaderBytes - Reiser4Tree.NodeHeaderBytes - 8);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootBlock * blockSize + 6), Reiser4Tree.NodeHeaderBytes + 8);
    image[rootBlock * blockSize + 26] = 3;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(17 * blockSize + 68, 2), 3);

    using var stream = new MemoryStream(image, writable: false);
    using var reader = new Reiser4Reader(stream);
    Assert.That(reader.Extract(reader.Entries.Single()), Is.EqualTo(payload));
  }

  [Test, Category("RoundTrip")]
  public void Rebuild_RetainsStatExtensionsEmptyDirectoriesAndVolumeIdentity() {
    var writer = new Reiser4Writer { Label = "metadata", Uuid = Enumerable.Range(0, 16).Select(static i => (byte)i).ToArray() };
    writer.AddFile("parent/file.bin", Payload(100, 10));
    using var source = new MemoryStream(writer.Build());
    using var initial = new Reiser4Reader(source);
    var metadata = initial.Entries.Single(static e => !e.IsDirectory).Metadata!;
    var raw = metadata.RawStatData.Concat(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }).ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(16), 1234);
    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(20), 5678);
    var retained = metadata with { UserId = 1234, GroupId = 5678, RawStatData = raw };
    var decorated = new Reiser4Writer { Label = writer.Label, Uuid = writer.Uuid };
    decorated.AddFile("parent/file.bin", Payload(100, 10), retained);
    decorated.AddDirectory("empty/child");
    using var image = new MemoryStream();
    decorated.Write(image);
    var descriptor = new Reiser4FormatDescriptor();
    descriptor.Add(image, [ArchiveInputInfo.InMemory("added.bin", Payload(150, 11))]);
    descriptor.Defragment(image);
    using var result = new Reiser4Reader(image);
    Assert.Multiple(() => {
      Assert.That(result.NativeTreeValid, Is.True);
      Assert.That(result.Label, Is.EqualTo("metadata"));
      Assert.That(result.UuidHex, Is.EqualTo(Convert.ToHexString(writer.Uuid!)));
      Assert.That(result.Entries.Single(static e => e.Name == "parent/file.bin").Metadata!.RawStatData,
        Is.EqualTo(raw));
      Assert.That(result.Entries.Single(static e => e.Name == "empty/child").IsDirectory, Is.True);
    });
  }

  [Test]
  public void UnsupportedTree_RebuildDoesNotOverwriteOriginal() {
    var imageBytes = BuildWithoutLegacyDirectory(("file.bin", Payload(100, 12)));
    BinaryPrimitives.WriteUInt64LittleEndian(imageBytes.AsSpan(23 * Reiser4Writer.BlockSize + 28), 23);
    using var image = new MemoryStream(imageBytes.ToArray());
    var descriptor = new Reiser4FormatDescriptor();
    Assert.Throws<NotSupportedException>(() => descriptor.Add(image,
      [ArchiveInputInfo.InMemory("added.bin", new byte[] { 1 })]));
    Assert.That(image.ToArray(), Is.EqualTo(imageBytes));
    using var reader = new Reiser4Reader(image);
    Assert.That(reader.NativeTreeValid, Is.False);
    Assert.That(reader.Entries, Is.Empty);
  }

  [Test]
  public void Writer_RejectsFileDirectoryCollisions() {
    var writer = new Reiser4Writer();
    writer.AddFile("collision", new byte[] { 1 });
    writer.AddFile("collision/child", new byte[] { 2 });
    Assert.Throws<InvalidDataException>(() => writer.Build());
  }

  [Test, Category("RoundTrip")]
  public void Writer_PacksWideDirectoryAcrossLeavesAndInternalLevels() {
    const int count = 5_000;
    var writer = new Reiser4Writer();
    for (var i = 0; i < count; ++i)
      writer.AddFile($"wide/file-{i:D5}.bin", i % 1000 == 0 ? Payload(17, i) : []);
    using var image = new MemoryStream(writer.Build());
    using var reader = new Reiser4Reader(image);
    var byName = reader.Entries.ToDictionary(static entry => entry.Name);
    Assert.Multiple(() => {
      Assert.That(reader.NativeTreeValid, Is.True);
      Assert.That(reader.NativeNodeBlocks.Count, Is.GreaterThan(89));
      Assert.That(byName.Count, Is.EqualTo(count + 1));
      for (var i = 0; i < count; i += 1000)
        Assert.That(reader.Extract(byName[$"wide/file-{i:D5}.bin"]), Is.EqualTo(Payload(17, i)));
      var format = image.ToArray().AsSpan(17 * Reiser4Writer.BlockSize);
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(format[68..]), Is.GreaterThanOrEqualTo(3));
    });
    var snapshot = image.ToArray();
    var nodes = reader.NativeNodeBlocks.ToDictionary(static block => block,
      block => snapshot.AsSpan(checked((int)block * Reiser4Writer.BlockSize), Reiser4Writer.BlockSize).ToArray());
    new Reiser4FormatDescriptor().WipeUnusedSpace(image);
    var wiped = image.ToArray();
    foreach (var (block, node) in nodes)
      Assert.That(wiped.AsSpan(checked((int)block * Reiser4Writer.BlockSize), Reiser4Writer.BlockSize).ToArray(),
        Is.EqualTo(node));
  }

  [Test, Category("RoundTrip")]
  public void Writer_RoundTripsMoreThan1024Directories() {
    const int count = 1_100;
    var writer = new Reiser4Writer();
    for (var i = 0; i < count; ++i)
      writer.AddDirectory($"directory-{i:D4}");
    using var image = new MemoryStream(writer.Build());
    using var reader = new Reiser4Reader(image);
    Assert.Multiple(() => {
      Assert.That(reader.NativeTreeValid, Is.True);
      Assert.That(reader.Entries.Count, Is.EqualTo(count));
      Assert.That(reader.Entries.All(static entry => entry.IsDirectory), Is.True);
      Assert.That(reader.Entries.Select(static entry => entry.Name).Distinct().Count(), Is.EqualTo(count));
    });
  }

  [Test, Category("RoundTrip")]
  public void MetadataUpdate_AppliesTypedFieldsAndKeepsIdentityAcrossReplacement() {
    var writer = new Reiser4Writer();
    writer.AddFile("directory/file.bin", Payload(81, 13));
    using var image = new MemoryStream();
    writer.Write(image);
    Reiser4Reader.FileMetadata before;
    using (var reader = new Reiser4Reader(image))
      before = reader.Entries.Single(static entry => !entry.IsDirectory).Metadata!;
    var updated = before with {
      Mode = 0x8180, UserId = 300, GroupId = 400,
      ModifiedTime = 1234567, AccessedTime = 7654321, ChangedTime = 2345678,
    };
    var descriptor = new Reiser4FormatDescriptor();
    descriptor.UpdateMetadata(image, "directory/file.bin", updated);
    descriptor.Add(image, [ArchiveInputInfo.InMemory("directory/file.bin", Payload(9_137, 14))]);
    using var result = new Reiser4Reader(image);
    var entry = result.Entries.Single(static entry => !entry.IsDirectory);
    Assert.Multiple(() => {
      Assert.That(result.NativeTreeValid, Is.True);
      Assert.That(entry.Metadata!.ObjectId, Is.EqualTo(before.ObjectId));
      Assert.That(entry.Metadata.StatLocality, Is.EqualTo(before.StatLocality));
      Assert.That(entry.Metadata.Mode, Is.EqualTo(updated.Mode));
      Assert.That(entry.Metadata.UserId, Is.EqualTo(updated.UserId));
      Assert.That(entry.Metadata.GroupId, Is.EqualTo(updated.GroupId));
      Assert.That(entry.Metadata.ModifiedTime, Is.EqualTo(updated.ModifiedTime));
      Assert.That(entry.Metadata.AccessedTime, Is.EqualTo(updated.AccessedTime));
      Assert.That(entry.Metadata.ChangedTime, Is.EqualTo(updated.ChangedTime));
      Assert.That(entry.Metadata!.Size, Is.EqualTo(9_137));
      Assert.That(result.Extract(entry), Is.EqualTo(Payload(9_137, 14)));
    });
  }

  [Test, Category("RoundTrip")]
  public void Writer_PlacesExtentsInTwigNodes() {
    var writer = new Reiser4Writer();
    writer.AddFile("extent.bin", Payload(9_137, 15));
    using var image = new MemoryStream(writer.Build());
    using var reader = new Reiser4Reader(image);
    var bytes = image.ToArray();
    var extentCount = 0;
    foreach (var block in reader.NativeNodeBlocks) {
      var node = bytes.AsSpan(checked((int)block * Reiser4Writer.BlockSize), Reiser4Writer.BlockSize);
      var count = BinaryPrimitives.ReadUInt16LittleEndian(node[2..]);
      for (var i = 0; i < count; ++i) {
        var header = Reiser4Writer.BlockSize - (i + 1) * Reiser4Tree.ItemHeaderBytes;
        if (BinaryPrimitives.ReadUInt16LittleEndian(node[(header + 36)..]) != 5) continue;
        ++extentCount;
        Assert.That(node[26], Is.EqualTo(2));
      }
    }
    Assert.That(extentCount, Is.GreaterThan(0));
    Assert.That(reader.NativeTreeValid, Is.True);
    Assert.That(reader.Extract(reader.Entries.Single()), Is.EqualTo(Payload(9_137, 15)));
  }

  private static uint MkfsIdOf(byte[] image)
    => BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(17 * Reiser4Writer.BlockSize + 48));

  private static ushort HeightOf(byte[] image)
    => BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(17 * Reiser4Writer.BlockSize + 68));

  [Test, Category("RoundTrip")]
  public void GivenAVolume_WhenEditingAndDefragmentingByRebuild_ThenTheMkfsIdSurvivesWithUuidAndLabel() {
    var writer = new Reiser4Writer { Label = "keepme", MkfsId = 0x1234ABCDu, Uuid = Enumerable.Range(1, 16).Select(static i => (byte)i).ToArray() };
    writer.AddFile("a/b.bin", Payload(5_000, 30));
    using var image = new MemoryStream(writer.Build());
    var descriptor = new Reiser4FormatDescriptor();
    descriptor.Add(image, [ArchiveInputInfo.InMemory("a/c.bin", Payload(9_000, 31))]);
    Assert.That(MkfsIdOf(image.ToArray()), Is.EqualTo(0x1234ABCDu), "after add");
    descriptor.Remove(image, ["a/b.bin"]);
    Assert.That(MkfsIdOf(image.ToArray()), Is.EqualTo(0x1234ABCDu), "after remove");
    descriptor.Defragment(image);
    Assert.That(MkfsIdOf(image.ToArray()), Is.EqualTo(0x1234ABCDu), "after defrag");
    using var reader = new Reiser4Reader(image);
    Assert.Multiple(() => {
      Assert.That(reader.Label, Is.EqualTo("keepme"));
      Assert.That(reader.UuidHex, Is.EqualTo(Convert.ToHexString(writer.Uuid!)));
    });
  }

  // Offsets inside the fixed blocks that carry the profile rather than the identity:
  // the format40 tail policy (block 17, byte 70), the backup of the root directory's
  // plugin set (block 22, from byte 127 on), the status block (21) and the journal (19, 20).
  [TestCase(17, 70, (byte)1, TestName = "GivenATailsOnlyPolicy_WhenEditing_ThenTheEditIsRefusedAndTheImageIsUntouched")]
  [TestCase(22, 131, (byte)2, TestName = "GivenANonDefaultHashInTheBackedUpPluginSet_WhenEditing_ThenTheEditIsRefusedAndTheImageIsUntouched")]
  [TestCase(21, 16, (byte)2, TestName = "GivenAStatusBlockNotMarkedConsistent_WhenEditing_ThenTheEditIsRefusedAndTheImageIsUntouched")]
  [TestCase(19, 0, (byte)1, TestName = "GivenAJournalHeaderThatIsNotBlank_WhenEditing_ThenTheEditIsRefusedAndTheImageIsUntouched")]
  public void VolumeOutsideTheWriterProfile_IsRefusedForEveryRebuild(int block, int offset, byte value) {
    var writer = new Reiser4Writer();
    writer.AddFile("d/f.bin", Payload(3_000, 32));
    var original = writer.Build();
    original[block * Reiser4Writer.BlockSize + offset] = value;
    var descriptor = new Reiser4FormatDescriptor();
    using var image = new MemoryStream(original.ToArray());

    Assert.Multiple(() => {
      Assert.Throws<NotSupportedException>(() => descriptor.Add(image, [ArchiveInputInfo.InMemory("x.bin", Payload(10, 33))]));
      Assert.That(image.ToArray(), Is.EqualTo(original), "add");
      Assert.Throws<NotSupportedException>(() => descriptor.Remove(image, ["d/f.bin"]));
      Assert.That(image.ToArray(), Is.EqualTo(original), "remove");
      Assert.Throws<NotSupportedException>(() => descriptor.Defragment(image));
      Assert.That(image.ToArray(), Is.EqualTo(original), "defragment");
      using var relayout = new MemoryStream();
      Assert.Throws<NotSupportedException>(() => descriptor.RebuildStreaming(image, relayout, new LayoutRebuildOptions()));
      Assert.That(image.ToArray(), Is.EqualTo(original), "relayout source");
      using var shrunk = new MemoryStream();
      descriptor.Shrink(image, shrunk);
      Assert.That(shrunk.ToArray(), Is.EqualTo(original), "shrink copies a volume it cannot rebuild through unchanged");
    });
  }

  [Test, Category("Boundary")]
  public void GivenOneFile_WhenWriting_ThenTheTreeIsTheTwoLevelTreeMkfsWrites() {
    var writer = new Reiser4Writer();
    writer.AddFile("one.bin", Payload(4_097, 36));
    var image = writer.Build();
    Assert.That(HeightOf(image), Is.EqualTo(2));
  }

  [Test, Category("Boundary")]
  public void GivenMoreTwigExtentsThanOneTwigHolds_WhenWriting_ThenTheTreeGrowsAThirdLevelAndEveryFileReadsBack() {
    // A twig item is a 38-byte header and a 16-byte extent unit: one 4 KiB twig holds
    // fewer than 76 of them, so 200 single-run files cannot share the root.
    var writer = new Reiser4Writer();
    for (var i = 0; i < 200; ++i) writer.AddFile($"f{i:D3}", Payload(1 + i, i));
    using var image = new MemoryStream(writer.Build());
    using var reader = new Reiser4Reader(image);
    Assert.Multiple(() => {
      Assert.That(HeightOf(image.ToArray()), Is.EqualTo(3));
      Assert.That(reader.NativeTreeValid, Is.True);
      Assert.That(reader.Entries, Has.Count.EqualTo(200));
      foreach (var entry in reader.Entries)
        Assert.That(reader.Extract(entry), Is.EqualTo(Payload(1 + int.Parse(entry.Name[1..]), int.Parse(entry.Name[1..]))), entry.Name);
    });
  }

  [Test, Category("Boundary")]
  public void GivenATallTree_WhenRemovingAllButOneFile_ThenTheRebuildShrinksBackToTwoLevels() {
    var writer = new Reiser4Writer();
    for (var i = 0; i < 200; ++i) writer.AddFile($"dir/f{i:D3}", Payload(10, i));
    using var image = new MemoryStream(writer.Build());
    Assert.That(HeightOf(image.ToArray()), Is.EqualTo(3));
    new Reiser4FormatDescriptor().Remove(image, [.. Enumerable.Range(1, 199).Select(static i => $"dir/f{i:D3}")]);
    using var reader = new Reiser4Reader(image);
    Assert.Multiple(() => {
      Assert.That(HeightOf(image.ToArray()), Is.EqualTo(2));
      Assert.That(reader.Entries.Select(static e => e.Name), Is.EquivalentTo(new[] { "dir", "dir/f000" }));
      Assert.That(reader.Extract(reader.Entries.Single(static e => !e.IsDirectory)), Is.EqualTo(Payload(10, 0)));
    });
  }

  [Test, Category("Boundary")]
  public void GivenTheOnlyFileOfADirectory_WhenRemovingIt_ThenTheDirectoryStaysAsAnEmptyDirectory() {
    var writer = new Reiser4Writer();
    writer.AddFile("keep/only.bin", Payload(10, 37));
    writer.AddFile("other.bin", Payload(10, 38));
    using var image = new MemoryStream(writer.Build());
    new Reiser4FormatDescriptor().Remove(image, ["keep/only.bin"]);
    using var reader = new Reiser4Reader(image);
    Assert.That(reader.Entries.Select(static e => (e.Name, e.IsDirectory)),
      Is.EquivalentTo(new[] { ("keep", true), ("other.bin", false) }));
  }

  [TestCase(23, TestName = "GivenANameOfTwentyThreeCharacters_WhenWriting_ThenItIsHeldInTheKeyAndReadsBack")]
  [TestCase(24, TestName = "GivenANameOfTwentyFourCharacters_WhenWriting_ThenItIsHashedAndReadsBack")]
  [TestCase(255, TestName = "GivenANameOf255Characters_WhenWriting_ThenItReadsBack")]
  [Category("Boundary")]
  public void NameLengthBoundaries_RoundTrip(int length) {
    var name = new string((char)('a' + length % 26), length);
    var writer = new Reiser4Writer();
    writer.AddFile("n/" + name, Payload(length, length));
    using var image = new MemoryStream(writer.Build());
    using var reader = new Reiser4Reader(image);
    var entry = reader.Entries.Single(static e => !e.IsDirectory);
    Assert.Multiple(() => {
      Assert.That(entry.Name, Is.EqualTo("n/" + name));
      Assert.That(reader.Extract(entry), Is.EqualTo(Payload(length, length)));
    });
  }

}
