using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileSystem.Svr4Bfs;

namespace Compression.Tests.Svr4Bfs;

/// <summary>
/// SVR4 / UnixWare Boot File System: the reader against a volume <c>mkfs.bfs</c> formatted
/// and the Linux <c>bfs</c> driver wrote, the writer against the layout <c>mkfs.bfs</c>
/// produces, and the in-place editor's refusals. The kernel reading what we write is in
/// <see cref="Svr4BfsKernelMountTests"/>.
/// </summary>
[TestFixture]
public class Svr4BfsTests {

  private static byte[] KernelVolume() {
    using var res = typeof(Svr4BfsTests).Assembly.GetManifestResourceStream("Svr4BfsVectors.kernel.bfs.gz")
      ?? throw new InvalidOperationException("kernel.bfs.gz not embedded");
    using var gz = new GZipStream(res, CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, string> KernelManifest() {
    using var res = typeof(Svr4BfsTests).Assembly.GetManifestResourceStream("Svr4BfsVectors.kernel.bfs.manifest")
      ?? throw new InvalidOperationException("kernel.bfs.manifest not embedded");
    using var sr = new StreamReader(res);
    return sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.Split("  ", 2))
      .ToDictionary(p => p[1].Trim(), p => p[0]);
  }

  private static Dictionary<string, byte[]> ReadAll(byte[] image) {
    using var ms = new MemoryStream(image, writable: false);
    var r = new Svr4BfsReader(ms);
    return r.Entries.ToDictionary(e => e.Name, r.Extract);
  }

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  // ── reading what mkfs.bfs + the kernel wrote ─────────────────────────

  [Test]
  public void GivenKernelWrittenVolume_WhenRead_ThenEveryFileMatchesWhatTheDriverReadBack() {
    var files = ReadAll(KernelVolume());
    var manifest = KernelManifest();
    Assert.That(files.Keys, Is.EquivalentTo(manifest.Keys));
    foreach (var (name, sha) in manifest)
      Assert.That(Sha(files[name]), Is.EqualTo(sha), name);
  }

  [Test]
  public void GivenKernelWrittenVolume_WhenRead_ThenHardLinkSharesOneInodeAndEmptyFileOwnsNoBlocks() {
    using var ms = new MemoryStream(KernelVolume(), writable: false);
    var r = new Svr4BfsReader(ms);
    var hello = r.Entries.Single(e => e.Name == "hello.txt");
    var link = r.Entries.Single(e => e.Name == "hello-link");
    Assert.That(link.Ino, Is.EqualTo(hello.Ino));
    Assert.That(hello.Inode.Nlink, Is.EqualTo(2u));
    var empty = r.Entries.Single(e => e.Name == "empty");
    Assert.That(empty.Inode.StartBlock, Is.Zero);
    Assert.That(empty.Inode.EndOffset, Is.EqualTo(0xFFFFFFFFu));
    Assert.That(r.Entries.Single(e => e.Name == "random.bin").Inode.Mode & 0xFFF, Is.EqualTo(0x180u));
    Assert.That(r.Superblock.FsName, Is.EqualTo("kfs001"));
    Assert.That(r.Superblock.Volume, Is.EqualTo("kvol01"));
  }

  [Test]
  public void GivenKernelWrittenVolume_WhenDetected_ThenMagicSelectsSvr4BfsNotBeFs() {
    var head = KernelVolume().AsSpan(0, 4096);
    Assert.That(Compression.Lib.FormatDetector.DetectByMagic(head).ToString(), Is.EqualTo("Svr4Bfs"));
  }

  // ── writer ────────────────────────────────────────────────────────────

  [Test]
  public void GivenNoFiles_WhenWrittenWithMkfsGeometry_ThenMetadataEqualsMkfsBfsLayout() {
    // mkfs.bfs (util-linux 2.39.3) on a 64 KiB file: 48 inodes in 6 blocks, s_start 0xE00,
    // s_end 0xFFFF, root inode 2 at blocks 7..8 holding "." and ".." (end offset 0xE1F),
    // mode 040755, uid 0, gid 1, two links. Only the timestamps differ between runs.
    var w = new Svr4BfsWriter { InodeCount = 48, VolumeSize = 64 * 1024 };
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var img = ms.ToArray();

    Assert.That(img, Has.Length.EqualTo(65536));
    Assert.That(Convert.ToHexString(img.AsSpan(0, 40)), Is.EqualTo(
      "CEFAAD1B000E0000FFFF0000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF" + "202020202020202020202020"));
    Assert.That(Convert.ToHexString(img.AsSpan(0x200, 36)), Is.EqualTo(
      "02000000" + "07000000" + "08000000" + "1F0E0000" + "02000000" + "ED410000" + "00000000" + "01000000" + "02000000"));
    Assert.That(Convert.ToHexString(img.AsSpan(0xE00, 32)), Is.EqualTo(
      "02002E00" + new string('0', 24) + "02002E2E" + new string('0', 24)));
    Assert.That(img.AsSpan(0x240, 0xE00 - 0x240).ContainsAnyExcept((byte)0), Is.False, "free inode slots are zero");
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(511)]
  [TestCase(512)]
  [TestCase(513)]
  [TestCase(70_000)]
  public void GivenFileOfBoundarySize_WhenWrittenAndRead_ThenBytesRoundTrip(int size) {
    var data = Enumerable.Range(0, size).Select(i => (byte)(i * 7 + 3)).ToArray();
    var img = Svr4BfsWriter.Build([("f.bin", data), ("after", "tail"u8.ToArray())]);
    var files = ReadAll(img);
    Assert.That(files["f.bin"], Is.EqualTo(data));
    Assert.That(files["after"], Is.EqualTo("tail"u8.ToArray()));
  }

  [Test]
  public void GivenFourteenByteName_WhenWritten_ThenStoredWithoutTerminator() {
    var img = Svr4BfsWriter.Build([("abcdefghijklmn", "x"u8.ToArray())]);
    Assert.That(ReadAll(img).Keys, Is.EquivalentTo(new[] { "abcdefghijklmn" }));
  }

  [TestCase("abcdefghijklmno", TestName = "Svr4Bfs_FifteenByteName_Refused")]
  [TestCase("dir/file", TestName = "Svr4Bfs_NestedPath_Refused")]
  [TestCase("..", TestName = "Svr4Bfs_DotDotName_Refused")]
  [TestCase("", TestName = "Svr4Bfs_EmptyName_Refused")]
  public void GivenUnstorableName_WhenWritten_ThenRefused(string name)
    => Assert.Throws<NotSupportedException>(() => Svr4BfsWriter.Build([(name, "x"u8.ToArray())]));

  [Test]
  public void GivenDuplicateNames_WhenWritten_ThenRefused()
    => Assert.Throws<NotSupportedException>(() => Svr4BfsWriter.Build([("a", [1]), ("a", [2])]));

  [Test]
  public void GivenVolumeTooSmall_WhenWritten_ThenRefused() {
    var w = new Svr4BfsWriter { VolumeSize = 8 * 1024 };
    w.AddFile("big", new byte[16 * 1024]);
    Assert.Throws<NotSupportedException>(() => w.WriteTo(new MemoryStream()));
  }

  [Test]
  public void GivenMetadata_WhenWritten_ThenModeOwnerAndTimeAreKept() {
    var when = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero);
    var d = new Svr4BfsFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("unix", "kernel"u8.ToArray(),
      new ArchiveEntryMetadata(LastWriteTimeUtc: when, UnixUserId: 3, UnixGroupId: 4, UnixMode: 0x1ED))], new FormatCreateOptions());
    ms.Position = 0;
    var e = new Svr4BfsReader(ms).Entries.Single();
    Assert.That(e.LastModified, Is.EqualTo(when.UtcDateTime));
    Assert.That(e.Inode.Uid, Is.EqualTo(3u));
    Assert.That(e.Inode.Gid, Is.EqualTo(4u));
    Assert.That(e.Inode.Mode, Is.EqualTo(0x81EDu));
  }

  [Test]
  public void GivenTree_WhenCreated_ThenFilesLandByLeafNameAndDirectoriesAreDropped() {
    var d = new Svr4BfsFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [new ArchiveInputInfo("sub", "sub", IsDirectory: true), ArchiveInputInfo.InMemory("sub/inner.txt", [7])], new FormatCreateOptions());
    Assert.That(ReadAll(ms.ToArray()).Keys, Is.EquivalentTo(new[] { "inner.txt" }));
  }

  [Test]
  public void GivenTwoLeavesWithOneName_WhenCreated_ThenRefused() {
    var d = new Svr4BfsFormatDescriptor();
    Assert.Throws<NotSupportedException>(() => d.Create(new MemoryStream(),
      [ArchiveInputInfo.InMemory("a/x", [1]), ArchiveInputInfo.InMemory("b/x", [2])], new FormatCreateOptions()));
  }

  [Test]
  public void GivenNestedPath_WhenAddedInPlace_ThenRefusedAndImageUnchanged() {
    var img = Svr4BfsWriter.Build([("a", [1])]);
    using var ms = new MemoryStream();
    ms.Write(img);
    Assert.Throws<NotSupportedException>(() => new Svr4BfsFormatDescriptor().Add(ms, [ArchiveInputInfo.InMemory("d/b", [2])]));
    Assert.That(ms.ToArray(), Is.EqualTo(img));
  }

  // ── malformed input ───────────────────────────────────────────────────

  [Test]
  public void GivenWrongMagic_WhenOpened_ThenInvalidData() {
    var img = Svr4BfsWriter.Build([("a", [1])]);
    img[0] ^= 0xFF;
    Assert.Throws<InvalidDataException>(() => new Svr4BfsReader(new MemoryStream(img)));
  }

  [Test]
  public void GivenTruncatedImage_WhenOpened_ThenInvalidDataOrEndOfStream() {
    var img = Svr4BfsWriter.Build([("a", [1])]).AsSpan(0, 1024).ToArray();
    Assert.Catch(() => new Svr4BfsReader(new MemoryStream(img)));
  }

  [Test]
  public void GivenInodePointingOutsideVolume_WhenRead_ThenFileIsNotListedAsData() {
    var img = Svr4BfsWriter.Build([("bad", new byte[600]), ("good", "ok"u8.ToArray())]);
    // Inode 3 ("bad"): end block far beyond s_end.
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)Svr4BfsLayout.InodeOffset(3) + 8), 0x00FFFFFF);
    var files = ReadAll(img);
    Assert.That(files.Keys, Is.EquivalentTo(new[] { "good" }));
  }

  // ── in-place edits ────────────────────────────────────────────────────

  [Test]
  public void GivenKernelVolume_WhenOneBlockFileAdded_ThenItFillsTheHoleTheDeletedFileLeft() {
    var img = KernelVolume();
    using var ms = new MemoryStream();
    ms.Write(img);
    Svr4BfsModifier.AddFile(ms, "fill", new byte[100]);
    ms.Position = 0;
    var r = new Svr4BfsReader(ms);
    Assert.That(r.Entries.Single(e => e.Name == "fill").Inode.StartBlock, Is.EqualTo(0x11u),
      "block 0x11 is the hole doomed.txt left between random.bin and hello.txt");
    Assert.That(ms.Length, Is.EqualTo(img.Length));
    var manifest = KernelManifest();
    foreach (var e in r.Entries.Where(e => e.Name != "fill"))
      Assert.That(Sha(r.Extract(e)), Is.EqualTo(manifest[e.Name]), e.Name);
  }

  [Test]
  public void GivenHardLink_WhenOneNameRemoved_ThenOtherNameKeepsDataAndLinkCountDrops() {
    using var ms = new MemoryStream();
    ms.Write(KernelVolume());
    Assert.That(Svr4BfsModifier.RemoveFile(ms, "hello.txt"), Is.True);
    ms.Position = 0;
    var r = new Svr4BfsReader(ms);
    var link = r.Entries.Single(e => e.Name == "hello-link");
    Assert.That(link.Inode.Nlink, Is.EqualTo(1u));
    Assert.That(Encoding.ASCII.GetString(r.Extract(link)), Is.EqualTo("hello from the kernel"));
  }

  [Test]
  public void GivenFile_WhenRemoved_ThenItsBlocksAndInodeAreZeroed() {
    var img = Svr4BfsWriter.Build([("keep", "keep"u8.ToArray()), ("drop", Encoding.ASCII.GetBytes("WIPE-ME-WIPE-ME"))]);
    using var ms = new MemoryStream();
    ms.Write(img);
    Assert.That(Svr4BfsModifier.RemoveFile(ms, "drop"), Is.True);
    Assert.That(Svr4BfsModifier.RemoveFile(ms, "absent"), Is.False);
    var after = ms.ToArray();
    Assert.That(Encoding.ASCII.GetString(after).Contains("WIPE-ME"), Is.False);
    Assert.That(after.AsSpan((int)Svr4BfsLayout.InodeOffset(4), Svr4BfsLayout.InodeSize).ContainsAnyExcept((byte)0), Is.False);
    Assert.That(ReadAll(after).Keys, Is.EquivalentTo(new[] { "keep" }));
  }

  [Test]
  public void GivenExistingName_WhenAddedAgain_ThenContentIsReplacedInPlace() {
    using var ms = new MemoryStream();
    ms.Write(Svr4BfsWriter.Build([("a", "old"u8.ToArray()), ("b", "bee"u8.ToArray())]));
    var d = new Svr4BfsFormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("a", new byte[2000])]);
    var files = ReadAll(ms.ToArray());
    Assert.That(files["a"], Is.EqualTo(new byte[2000]));
    Assert.That(files["b"], Is.EqualTo("bee"u8.ToArray()));
  }

  [Test]
  public void GivenNoContiguousRun_WhenAdded_ThenRefusedAndImageUnchanged() {
    var w = new Svr4BfsWriter { VolumeSize = 32 * 1024 };
    w.AddFile("a", [1]);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var before = ms.ToArray();
    var d = new Svr4BfsFormatDescriptor();
    Assert.Throws<NotSupportedException>(() =>
      d.Add(ms, [ArchiveInputInfo.InMemory("ok", [2]), ArchiveInputInfo.InMemory("huge", new byte[64 * 1024])]));
    Assert.That(ms.ToArray(), Is.EqualTo(before), "a refused batch writes nothing, not even the inputs before it");
  }

  [Test]
  public void GivenNoFreeInode_WhenAdded_ThenRefusedAndImageUnchanged() {
    // 8 inodes: root + 7 files. The writer gives the root directory a slot per inode,
    // so the inode table is what runs out.
    var w = new Svr4BfsWriter { InodeCount = 8 };
    for (var i = 0; i < 7; ++i) w.AddFile($"f{i}", [(byte)i]);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var before = ms.ToArray();
    Assert.Throws<NotSupportedException>(() => Svr4BfsModifier.AddFile(ms, "one-more", [9]));
    Assert.That(ms.ToArray(), Is.EqualTo(before));
  }

  [Test]
  public void GivenInterruptedCompaction_WhenEdited_ThenRefusedAndImageUnchanged() {
    var img = Svr4BfsWriter.Build([("a", [1])]);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(12), 9); // s_from
    using var ms = new MemoryStream();
    ms.Write(img);
    Assert.Throws<NotSupportedException>(() => Svr4BfsModifier.AddFile(ms, "b", [2]));
    Assert.Throws<NotSupportedException>(() => Svr4BfsModifier.RemoveFile(ms, "a"));
    Assert.That(ms.ToArray(), Is.EqualTo(img));
  }

  [Test]
  public void GivenDescriptor_WhenAskedForCapabilities_ThenReadWriteIsBacked() {
    var d = new Svr4BfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories), Is.False);
  }
}
