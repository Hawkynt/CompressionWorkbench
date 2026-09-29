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
}
