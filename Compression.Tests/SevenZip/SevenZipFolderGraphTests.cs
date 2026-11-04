using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// The 7z folder decoder: coder graphs are checked before anything is decoded, a folder larger
/// than the in-memory limit streams through a bounded pipe, and the push path (whole-folder
/// extraction) and the pull path (one entry at a time) produce the same bytes.
/// </summary>
[TestFixture]
public class SevenZipFolderGraphTests {
  private static SevenZipCoder Coder(byte[] id, int inputs = 1) => new() { CodecId = id, NumInStreams = inputs };

  private static SevenZipFolder Folder(SevenZipCoder[] coders, (int, int)[] binds, int[] packed, long[] sizes) {
    var folder = new SevenZipFolder { PackedStreams = packed, UnpackSizes = sizes };
    folder.Coders.AddRange(coders);
    folder.BindPairs.AddRange(binds);
    return folder;
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_ACoderFeedingItself_When_Validated_Then_TheGraphIsRefused() {
    // Coder 1's input is its own output; nothing reaches it from the folder output.
    var folder = Folder([Coder([0x03]), Coder([0x03])], [(1, 1)], [0], [10, 10]);
    Assert.That(folder.Validate, Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_ABindPairOutOfRange_When_Validated_Then_ItIsRefused() {
    var folder = Folder([Coder([0x03]), Coder([0x21])], [(0, 5)], [1], [10, 10]);
    Assert.That(folder.Validate, Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_AnInputNothingFeeds_When_Validated_Then_ItIsRefused() {
    var folder = Folder([Coder(SevenZipConstants.CodecBcj2.ToArray(), 4), Coder([0x21])], [(0, 1)], [4, 1, 2], [10, 10]);
    Assert.That(folder.Validate, Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_ACoderWithTwoOutputs_When_Validated_Then_ThePre9CoderModelIsRefusedAsUnsupported() {
    var folder = Folder([new SevenZipCoder { CodecId = [0x21], NumInStreams = 1, NumOutStreams = 2 }], [], [0], [10, 10]);
    Assert.That(folder.Validate, Throws.InstanceOf<NotSupportedException>());
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_AValidFilterChain_When_Validated_Then_TheOutputIsTheUnboundCoder() {
    var folder = Folder([Coder(SevenZipConstants.CodecBcj.ToArray()), Coder([0x21])], [(0, 1)], [], [100, 100]);
    folder.Validate();
    Assert.That(folder.MainCoderIndex, Is.Zero);
    Assert.That(folder.PackedStreams, Is.EqualTo(new[] { 1 }));
  }

  [Test]
  [Category("Performance")]
  public void Given_AFolderLargerThanTheInMemoryLimit_When_ReadEntryByEntryAndWhole_Then_BothPathsAgree() {
    var big = new byte[(int)SevenZipFolderDecoder.InMemoryLimit + (3 << 20)];
    for (var i = 0; i < big.Length; i += 4096)
      big[i] = (byte)(i >> 12);
    var small = "after the big one"u8.ToArray();

    using var archive = new MemoryStream();
    using (var writer = new SevenZipWriter(archive, SevenZipCodec.Lzma2, leaveOpen: true, filter: SevenZipFilter.BcjX86)) {
      writer.AddEntry(new SevenZipEntry { Name = "big.bin" }, big);
      writer.AddEntry(new SevenZipEntry { Name = "small.txt" }, small);
      writer.Finish();
    }

    archive.Position = 0;
    using (var reader = new SevenZipReader(archive, leaveOpen: true)) {
      Assert.That(reader.Extract(0).AsSpan().SequenceEqual(big), Is.True, "pull path, big entry");
      Assert.That(reader.Extract(1), Is.EqualTo(small), "pull path, the entry after it");
    }

    archive.Position = 0;
    using (var reader = new SevenZipReader(archive, leaveOpen: true)) {
      var outputs = new Dictionary<int, MemoryStream>();
      reader.ExtractMany(i => outputs[i] = new MemoryStream(), (_, _) => { });
      Assert.That(outputs[0].ToArray().AsSpan().SequenceEqual(big), Is.True, "push path, big entry");
      Assert.That(outputs[1].ToArray(), Is.EqualTo(small), "push path, the entry after it");
    }
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_AReaderStoppedHalfwayThroughAPipedFolder_When_Disposed_Then_ItReturns() {
    var big = new byte[(int)SevenZipFolderDecoder.InMemoryLimit + (1 << 20)];
    using var archive = new MemoryStream();
    using (var writer = new SevenZipWriter(archive, SevenZipCodec.Lzma2, leaveOpen: true, filter: SevenZipFilter.Delta)) {
      writer.AddEntry(new SevenZipEntry { Name = "a.txt" }, "first"u8);
      writer.AddEntry(new SevenZipEntry { Name = "big.bin" }, big);
      writer.Finish();
    }

    archive.Position = 0;
    var reader = new SevenZipReader(archive, leaveOpen: true);
    Assert.That(reader.Extract(0), Is.EqualTo("first"u8.ToArray()));
    var dispose = Task.Run(reader.Dispose);
    Assert.That(dispose.Wait(TimeSpan.FromSeconds(30)), Is.True);
  }
}
