using Compression.Registry;
using FileFormat.Swm;
using FileFormat.Wim;

namespace Compression.Tests.Swm;

[TestFixture]
public class SwmTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties_AdvertiseCreationAndEveryWimMethod() {
    var descriptor = new SwmFormatDescriptor();
    Assert.That(descriptor.Id, Is.EqualTo("Swm"));
    Assert.That(descriptor.Extensions, Contains.Item(".swm"));
    Assert.That(descriptor.Extensions, Contains.Item(".swm2"));
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(descriptor.Methods.Select(method => method.Name), Is.EquivalentTo(
      ["xpress", "xpress-huffman", "lzx", "lzms", "none"]));
  }

  [TestCase(WimConstants.CompressionNone)]
  [TestCase(WimConstants.CompressionXpress)]
  [TestCase(WimConstants.CompressionXpressHuffman)]
  [TestCase(WimConstants.CompressionLzx)]
  [TestCase(WimConstants.CompressionLzms)]
  [Category("End2End"), Category("RoundTrip")]
  public void WriterSplit_Reader_RoundTripsNamedFilesAndCompression(uint compression) {
    var first = new byte[12_000];
    var second = new byte[9_000];
    var repetitive = Enumerable.Repeat((byte)0x63, 64_000).ToArray();
    new Random(0x51A7).NextBytes(first);
    new Random(0xBEEF).NextBytes(second);
    var volumes = WimWriter.CreateSplit(3_000,
      [("folder/first.bin", first), ("second.bin", second), ("repetitive.bin", repetitive), ("empty.txt", [])], compression);

    Assert.That(volumes.Length, Is.GreaterThan(1));
    var readers = volumes.Select(volume => new MemoryStream(volume)).ToArray();
    try {
      var firstHeader = WimHeader.Read(readers[0]);
      Assert.That(firstHeader.PartNumber, Is.EqualTo(1));
      Assert.That(firstHeader.TotalParts, Is.EqualTo(volumes.Length));
      Assert.That((firstHeader.WimFlags & WimConstants.FlagSpanned) != 0, Is.True);
      readers[0].Position = 0;
      var resourceStreams = readers.Skip(1).Cast<Stream>().ToArray();
      using var reader = new WimReader(readers[0], resourceStreams);
      var named = reader.GetNamedFiles();
      Assert.That(named.Select(file => file.FileName), Is.EquivalentTo(
        ["folder/first.bin", "second.bin", "repetitive.bin", "empty.txt"]));
      Assert.That(reader.ReadResource(named.Single(file => file.FileName == "folder/first.bin").ResourceIndex), Is.EqualTo(first));
      Assert.That(reader.ReadResource(named.Single(file => file.FileName == "second.bin").ResourceIndex), Is.EqualTo(second));
      Assert.That(reader.ReadResource(named.Single(file => file.FileName == "repetitive.bin").ResourceIndex), Is.EqualTo(repetitive));
      Assert.That(named.Single(file => file.FileName == "empty.txt").ResourceIndex, Is.EqualTo(-1));
    } finally {
      foreach (var reader in readers)
        reader.Dispose();
    }
  }

  [Test, Category("End2End"), Category("RoundTrip")]
  public void Descriptor_Extract_LoadsAllSiblingParts() {
    var files = new[] {
      ("first.txt", Enumerable.Repeat((byte)'A', 10_000).ToArray()),
      ("second.txt", Enumerable.Repeat((byte)'B', 10_000).ToArray()),
    };
    var volumes = WimWriter.CreateSplit(3_000, files, WimConstants.CompressionNone);
    var directory = Path.Combine(Path.GetTempPath(), "swm_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      for (var index = 0; index < volumes.Length; ++index)
        File.WriteAllBytes(Path.Combine(directory, index == 0 ? "image.swm" : $"image{index + 1}.swm"), volumes[index]);
      var output = Path.Combine(directory, "out");
      using var input = File.OpenRead(Path.Combine(directory, "image.swm"));
      new SwmFormatDescriptor().Extract(input, output, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(output, "first.txt")), Is.EqualTo(files[0].Item2));
      Assert.That(File.ReadAllBytes(Path.Combine(output, "second.txt")), Is.EqualTo(files[1].Item2));
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [TestCase(0L)]
  [TestCase(-1L)]
  [Category("EdgeCase")]
  public void CreateSplit_GivenNonPositivePartSize_ThenRejected(long size) {
    Assert.That(() => WimWriter.CreateSplit(size, [new byte[] { 1 }]),
      Throws.TypeOf<ArgumentOutOfRangeException>());
  }

  [Test, Category("BoundaryCase")]
  public void CreateSplit_GivenPartSizeLargerThanImage_ThenOnePartWithoutSpannedFlag() {
    var volumes = WimWriter.CreateSplit(1L << 30, [("a.bin", new byte[5000])], WimConstants.CompressionNone);
    Assert.That(volumes, Has.Length.EqualTo(1));
    var header = WimHeader.Read(new MemoryStream(volumes[0]));
    Assert.That(header.TotalParts, Is.EqualTo(1));
    Assert.That(header.WimFlags & WimConstants.FlagSpanned, Is.Zero);
  }

  [Test, Category("BoundaryCase")]
  public void CreateSplit_GivenResourceLargerThanPartSize_ThenItGetsAPartOfItsOwn() {
    var big = new byte[20_000];
    new Random(7).NextBytes(big);
    var volumes = WimWriter.CreateSplit(4_000, [("big.bin", big), ("tiny.txt", "x"u8.ToArray())], WimConstants.CompressionNone);
    Assert.That(volumes.Any(v => v.Length > 20_000), Is.True, "the oversized resource is not cut");
    using var reader = new WimReader(new MemoryStream(volumes[0]), volumes.Skip(1).Select(v => (Stream)new MemoryStream(v)).ToArray());
    var named = reader.GetNamedFiles();
    Assert.That(reader.ReadResource(named.Single(f => f.FileName == "big.bin").ResourceIndex), Is.EqualTo(big));
  }

  [Test, Category("EdgeCase")]
  public void Reader_GivenPartFromAnotherSet_ThenRejected() {
    var setA = WimWriter.CreateSplit(3_000, [("a.bin", new byte[9_000]), ("b.bin", Enumerable.Repeat((byte)1, 9_000).ToArray())], WimConstants.CompressionNone);
    var setB = WimWriter.CreateSplit(3_000, [("c.bin", Enumerable.Repeat((byte)2, 9_000).ToArray()), ("d.bin", Enumerable.Repeat((byte)3, 9_000).ToArray())], WimConstants.CompressionNone);
    Assert.Throws<InvalidDataException>(() => _ = new WimReader(new MemoryStream(setA[0]), [new MemoryStream(setB[1])]));
  }

  [Test, Category("EdgeCase")]
  public void Reader_GivenSamePartTwice_ThenRejected() {
    var set = WimWriter.CreateSplit(3_000, [("a.bin", new byte[9_000]), ("b.bin", Enumerable.Repeat((byte)1, 9_000).ToArray())], WimConstants.CompressionNone);
    Assert.Throws<InvalidDataException>(() => _ = new WimReader(new MemoryStream(set[0]), [new MemoryStream(set[1]), new MemoryStream(set[1])]));
  }

  [Test, Category("EdgeCase")]
  public void Descriptor_GivenFirstPartWithoutSiblings_ThenListsButExtractionFails() {
    var set = WimWriter.CreateSplit(3_000, [("a.bin", Enumerable.Repeat((byte)4, 9_000).ToArray()), ("b.bin", Enumerable.Repeat((byte)5, 9_000).ToArray())], WimConstants.CompressionNone);
    Assert.That(set, Has.Length.GreaterThan(1));
    var names = new SwmFormatDescriptor().List(new MemoryStream(set[0]), null).Select(e => e.Name);
    Assert.That(names, Is.EquivalentTo(new[] { "a.bin", "b.bin" }));
    var dir = Path.Combine(Path.GetTempPath(), "swm_nosib_" + Guid.NewGuid().ToString("N"));
    try {
      Assert.Throws<InvalidDataException>(() => new SwmFormatDescriptor().Extract(new MemoryStream(set[0]), dir, null, null));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Descriptor_GivenLaterPartAlone_ThenListsPartInfoInsteadOfThrowing() {
    var set = WimWriter.CreateSplit(3_000, [("a.bin", Enumerable.Repeat((byte)4, 9_000).ToArray()), ("b.bin", Enumerable.Repeat((byte)5, 9_000).ToArray())], WimConstants.CompressionNone);
    var entries = new SwmFormatDescriptor().List(new MemoryStream(set[1]), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini" }));
    var text = System.Text.Encoding.UTF8.GetString(new SwmFormatDescriptor().ExtractEntryToMemory(new MemoryStream(set[1]), "metadata.ini", null));
    Assert.That(text, Does.Contain("part_number = 2"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_GivenUpperCaseSetNames_ThenSiblingsAreFound() {
    var files = new[] { ("first.txt", Enumerable.Repeat((byte)'A', 10_000).ToArray()), ("second.txt", Enumerable.Repeat((byte)'B', 10_000).ToArray()) };
    var volumes = WimWriter.CreateSplit(3_000, files, WimConstants.CompressionNone);
    var directory = Path.Combine(Path.GetTempPath(), "swm_uc_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      for (var index = 0; index < volumes.Length; ++index)
        File.WriteAllBytes(Path.Combine(directory, index == 0 ? "IMAGE.SWM" : $"IMAGE{index + 1}.SWM"), volumes[index]);
      var output = Path.Combine(directory, "out");
      using (var input = File.OpenRead(Path.Combine(directory, "IMAGE.SWM")))
        new SwmFormatDescriptor().Extract(input, output, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(output, "second.txt")), Is.EqualTo(files[1].Item2));
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [TestCase("xpress")]
  [TestCase("xpress-huffman")]
  [TestCase("lzx")]
  [TestCase("lzms")]
  [TestCase("none")]
  [Category("End2End"), Category("RoundTrip")]
  public void Descriptor_Create_AcceptsEveryAdvertisedMethod(string method) {
    using var output = new MemoryStream();
    var input = ArchiveInputInfo.InMemory("payload.bin", Enumerable.Repeat((byte)0x72, 32_000).ToArray());
    new SwmFormatDescriptor().Create(output, [input], new FormatCreateOptions(method));
    output.Position = 0;
    using var reader = new WimReader(output);
    var file = reader.GetNamedFiles().Single();
    Assert.That(reader.ReadResource(file.ResourceIndex), Is.EqualTo(input.InMemoryContent));
  }

  [Test, Category("EdgeCase")]
  public void List_NonWimInput_Throws() {
    var data = new byte[64];
    data[0] = (byte)'N'; data[1] = (byte)'O'; data[2] = (byte)'P'; data[3] = (byte)'E';
    using var stream = new MemoryStream(data);
    Assert.That(() => new SwmFormatDescriptor().List(stream, null), Throws.InstanceOf<InvalidDataException>());
  }
}
