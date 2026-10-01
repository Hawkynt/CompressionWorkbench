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
  [TestCase("sub/inner.dat", TestName = "GivenANestedName_WhenAddingToTheWriter_ThenItIsRefused")]
  [TestCase("sub\\inner.dat", TestName = "GivenABackslashNestedName_WhenAddingToTheWriter_ThenItIsRefused")]
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
  public void GivenTheSameNameTwice_WhenAddingToTheWriter_ThenTheSecondIsRefused() {
    var writer = new Reiser4Writer();
    writer.AddFile("twice.bin", [1]);
    Assert.That(() => writer.AddFile("twice.bin", [2]), Throws.ArgumentException);
  }

  [Test, Category("EquivalenceClass")]
  public void GivenADirectoryInput_WhenAskingTheConstraints_ThenItIsRefusedWithAReason() {
    var accepted = new Reiser4FormatDescriptor().CanAccept(new ArchiveInputInfo("sub", "sub", IsDirectory: true), out var reason);
    Assert.Multiple(() => {
      Assert.That(accepted, Is.False);
      Assert.That(reason, Does.Contain("root directory"));
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
}
