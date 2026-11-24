using System.IO.Compression;
using System.Text;
using Compression.Registry;
using FileSystem.Chfs;

namespace Compression.Tests.Chfs;

/// <summary>
/// CHFS writer: reproduces the images NetBSD's <c>makefs -t chfs</c> built byte for byte, lays out
/// trees the reader returns unchanged, and refuses what CHFS cannot hold. The NetBSD kernel's own
/// check of the writer's images is <see cref="ChfsNetBsdTests"/>.
/// </summary>
[TestFixture]
public class ChfsWriterTests {

  internal static byte[] Image(string name) {
    using var res = typeof(ChfsWriterTests).Assembly.GetManifestResourceStream($"ChfsVectors.{name}.img.gz")
      ?? throw new InvalidOperationException($"{name}.img.gz not embedded");
    using var gz = new GZipStream(res, CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>The tree the reader returns, as writer input: every field the image holds.</summary>
  internal static ChfsWriteNode Tree(IEnumerable<ChfsEntry> entries) {
    var children = new Dictionary<string, List<ChfsWriteNode>>();
    var all = entries.ToList();
    ChfsWriteNode Node(ChfsEntry e) => new(e.Path[(e.Path.LastIndexOf('/') + 1)..], e.Mode) {
      Uid = e.Uid, Gid = e.Gid, ATime = e.ATime, MTime = e.MTime, CTime = e.CTime,
      DirectorySize = e.IsDirectory ? e.Size : ChfsWriter.DefaultDirectorySize, Data = e.Data,
      Children = e.IsDirectory ? Kids(e.Path) : [],
    };
    List<ChfsWriteNode> Kids(string dir) => [.. all.Where(e => e.Path.Contains('/') ? e.Path[..e.Path.LastIndexOf('/')] == dir : dir.Length == 0).Select(Node)];
    return new ChfsWriteNode("", 0x41ED) { Children = Kids("") };
  }

  private static byte[] Write(ChfsWriteNode root, ChfsGeometry? geometry = null, int dataNodeSize = ChfsWriter.DefaultDataNodeSize) {
    using var ms = new MemoryStream();
    ChfsWriter.Write(ms, root, geometry, dataNodeSize: dataNodeSize);
    return ms.ToArray();
  }

  /// <summary>
  /// Blanks each data node's version and node CRC. <c>makefs</c> writes every data node as
  /// version 0; the writer numbers them, as the kernel does. Everything else has to match byte
  /// for byte.
  /// </summary>
  private static byte[] WithoutDataVersions(byte[] img, int pageSize, int eraseBlockSize) {
    var copy = (byte[])img.Clone();
    for (var eb = 0; eb < copy.Length; eb += eraseBlockSize)
      for (var pos = eb + 2 * pageSize; pos + 12 <= eb + eraseBlockSize && BitConverter.ToUInt16(copy, pos) == ChfsReader.NodeMagic;) {
        if (BitConverter.ToUInt16(copy, pos + 2) == ChfsReader.TypeData) {
          copy.AsSpan(pos + 20, 8).Clear();
          copy.AsSpan(pos + 44, 4).Clear();
        }
        pos += (BitConverter.ToInt32(copy, pos + 4) + 3) & ~3;
      }
    return copy;
  }

  [TestCase("chfs-nand")]
  [TestCase("chfs-nor")]
  public void GivenTreeOfMakefsImage_WhenWrittenWithItsGeometryAndPageSizedDataNodes_ThenByteIdenticalToMakefsExceptDataNodeVersions(string name) {
    var original = Image(name);
    var r = new ChfsReader(new MemoryStream(original, writable: false));
    var written = Write(Tree(r.Entries), new ChfsGeometry(r.PageSize, r.EraseBlockSize, r.IsNand), dataNodeSize: r.PageSize);
    Assert.That(written.Length, Is.EqualTo(original.Length));
    var (a, b) = (WithoutDataVersions(written, r.PageSize, r.EraseBlockSize), WithoutDataVersions(original, r.PageSize, r.EraseBlockSize));
    var diff = a.AsSpan().CommonPrefixLength(b);
    Assert.That(diff, Is.EqualTo(original.Length), $"first difference at byte {diff}");
  }

  [Test]
  public void GivenFileOfSeveralDataNodes_WhenWritten_ThenEveryDataNodeHasItsOwnVersion() {
    var img = Write(new ChfsWriteNode("", 0x41ED) { Children = [new ChfsWriteNode("f", 0x81A4) { Data = new byte[10_000] }] });
    var versions = new List<ulong>();
    for (var pos = 4096; BitConverter.ToUInt16(img, pos) == ChfsReader.NodeMagic; pos += (BitConverter.ToInt32(img, pos + 4) + 3) & ~3)
      versions.Add(BitConverter.ToUInt64(img, pos + (BitConverter.ToUInt16(img, pos + 2) == ChfsReader.TypeDirent ? 28 : 20)));
    // vnode and directory entry as makefs numbers them, then three 4 KiB data nodes
    Assert.That(versions, Is.EqualTo(new ulong[] { 0, 1, 1, 2, 3 }));
    Assert.That(BitConverter.ToInt32(img, 4096 + 64 + 56 + 4), Is.EqualTo(48 + 4096), "first data node length");
  }

  [Test]
  public void GivenEmptyTree_WhenWritten_ThenOneEraseBlockWithHeadersOnly() {
    var img = Write(new ChfsWriteNode("", 0x41ED));
    Assert.That(img.Length, Is.EqualTo(131072));
    Assert.That(img.AsSpan(4096).IndexOfAnyExcept((byte)0xFF), Is.EqualTo(-1));
    Assert.That(new ChfsReader(new MemoryStream(img)).Entries, Is.Empty);
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2047)]
  [TestCase(2048)]
  [TestCase(2049)]
  [TestCase(300_000)]   // spills over into further eraseblocks
  public void GivenFileOfSize_WhenWrittenAndRead_ThenSameBytes(int size) {
    var data = new byte[size];
    new Random(size).NextBytes(data);
    var img = Write(new ChfsWriteNode("", 0x41ED) { Children = [new ChfsWriteNode("f", 0x81A4) { Data = data, MTime = 1234 }] });
    var e = new ChfsReader(new MemoryStream(img)).Entries.Single();
    Assert.That((e.Path, e.Data, e.MTime), Is.EqualTo(("f", data, 1234u)));
    Assert.That(img.Length % 131072, Is.Zero);
  }

  [Test]
  public void GivenNodesThatDoNotFitTheBlock_WhenWritten_ThenTheyStartTheNextEraseBlock() {
    // NOR, 512-byte pages, 8 KiB eraseblocks: 7 KiB of node room per block
    var geometry = new ChfsGeometry(512, 8192, false);
    var files = Enumerable.Range(0, 40).Select(i => new ChfsWriteNode($"file{i:D2}", 0x81A4) { Data = Encoding.ASCII.GetBytes(new string((char)('a' + i % 26), 500)) }).ToList();
    var img = Write(new ChfsWriteNode("", 0x41ED) { Children = files }, geometry);
    var r = new ChfsReader(new MemoryStream(img));
    Assert.That((r.IsNand, r.PageSize, r.EraseBlockSize), Is.EqualTo((false, 512, 8192)));
    Assert.That(r.Entries.Select(e => e.Path), Is.EqualTo(files.Select(f => f.Name)));
    Assert.That(r.Entries.Select(e => e.Data), Is.EqualTo(files.Select(f => f.Data)));
    Assert.That(r.EraseCounts, Has.Count.GreaterThan(1).And.All.EqualTo(1u));
  }

  [Test]
  public void GivenSymlinkDeviceAndOwners_WhenWritten_ThenEveryFieldReadsBack() {
    var root = new ChfsWriteNode("", 0x41ED) {
      Children = [
        new ChfsWriteNode("d", 0x41C0) { Uid = 1000, Gid = 100, ATime = 1, MTime = 2, CTime = 3, DirectorySize = 1024, Children = [
          new ChfsWriteNode("l", 0xA1FF) { Data = "../x"u8.ToArray() },
          new ChfsWriteNode("tty", 0x2190) { Data = [5, 0, 0, 0, 0, 0, 0, 0] },
        ] },
      ],
    };
    var r = new ChfsReader(new MemoryStream(Write(root)));
    var d = r.Entries.Single(e => e.Path == "d");
    Assert.That((d.Mode, d.Uid, d.Gid, d.ATime, d.MTime, d.CTime, d.Size), Is.EqualTo((0x41C0u, 1000u, 100u, 1u, 2u, 3u, 1024u)));
    Assert.That(r.Entries.Single(e => e.Path == "d/l").Data, Is.EqualTo("../x"u8.ToArray()));
    Assert.That(r.Entries.Single(e => e.Path == "d/tty").Data, Is.EqualTo(new byte[] { 5, 0, 0, 0, 0, 0, 0, 0 }));
  }

  [TestCase("")]
  [TestCase(".")]
  [TestCase("..")]
  [TestCase("a/b")]
  [TestCase("nul\0byte")]
  public void GivenUnstorableName_WhenWritten_ThenArgumentException(string name)
    => Assert.Throws<ArgumentException>(() => Write(new ChfsWriteNode("", 0x41ED) { Children = [new ChfsWriteNode(name, 0x81A4)] }));

  [Test]
  public void GivenNameOf255And256Bytes_WhenWritten_ThenOnlyThe255ByteNameIsAccepted() {
    var ok = new string('n', 255);
    Assert.That(new ChfsReader(new MemoryStream(Write(new ChfsWriteNode("", 0x41ED) { Children = [new ChfsWriteNode(ok, 0x81A4)] }))).Entries.Single().Path, Is.EqualTo(ok));
    Assert.Throws<ArgumentException>(() => Write(new ChfsWriteNode("", 0x41ED) { Children = [new ChfsWriteNode(ok + "n", 0x81A4)] }));
  }

  [Test]
  public void GivenDuplicateName_WhenWritten_ThenArgumentException()
    => Assert.Throws<ArgumentException>(() => Write(new ChfsWriteNode("", 0x41ED) { Children = [new ChfsWriteNode("a", 0x81A4), new ChfsWriteNode("a", 0x41ED)] }));

  [Test]
  public void GivenFileAsRoot_WhenWritten_ThenArgumentException()
    => Assert.Throws<ArgumentException>(() => Write(new ChfsWriteNode("", 0x81A4)));

  [TestCase(8, 131072)]       // page smaller than the NAND eraseblock header
  [TestCase(2050, 131200)]    // page not a multiple of 4
  [TestCase(2048, 8192)]      // no room for a 4 KiB data node after the two header pages
  [TestCase(2048, 130000)]    // eraseblock not a whole number of pages
  public void GivenUnusableGeometry_WhenWritten_ThenArgumentOutOfRange(int page, int eraseBlock)
    => Assert.Throws<ArgumentOutOfRangeException>(() => Write(new ChfsWriteNode("", 0x41ED), new ChfsGeometry(page, eraseBlock, true)));

  // ---- descriptor: Create, Add, Remove

  private static readonly ChfsFormatDescriptor Descriptor = new();

  private static MemoryStream Created(params ArchiveInputInfo[] inputs) {
    var ms = new MemoryStream();
    Descriptor.Create(ms, inputs, new FormatCreateOptions());
    ms.Position = 0;
    return ms;
  }

  private static readonly ArchiveEntryMetadata At = new(LastWriteTimeUtc: new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero));

  [Test]
  public void GivenInputs_WhenCreated_ThenListedWithImpliedDirectoriesAndDefaultModes() {
    using var img = Created(
      ArchiveInputInfo.InMemory("docs/readme.txt", "hello"u8, At),
      ArchiveInputInfo.InMemory("top.bin", new byte[5000], At with { UnixMode = 0x1C0, UnixUserId = 7, UnixGroupId = 8 }),
      new ArchiveInputInfo("empty", "empty", true, Metadata: At));
    var r = new ChfsReader(img);
    Assert.That(r.Entries.Select(e => e.Path), Is.EqualTo(new[] { "docs", "docs/readme.txt", "empty", "top.bin" }));
    var top = r.Entries.Single(e => e.Path == "top.bin");
    Assert.That((top.Mode, top.Uid, top.Gid, top.MTime), Is.EqualTo((0x81C0u, 7u, 8u, (uint)At.LastWriteTimeUtc!.Value.ToUnixTimeSeconds())));
    Assert.That(r.Entries.Single(e => e.Path == "docs/readme.txt").Mode, Is.EqualTo(0x81A4u));
    Assert.That(r.Entries.Single(e => e.Path == "empty").Mode, Is.EqualTo(0x41EDu));
    Assert.That(Descriptor.ExtractEntryToMemory(img, "docs/readme.txt", null), Is.EqualTo("hello"u8.ToArray()));
  }

  [Test]
  public void GivenSameNameTwice_WhenCreated_ThenArgumentException()
    => Assert.Throws<ArgumentException>(() => Created(ArchiveInputInfo.InMemory("a", "1"u8, At), ArchiveInputInfo.InMemory("a", "2"u8, At)));

  [TestCase("chfs-nand")]
  [TestCase("chfs-nor")]
  public void GivenMakefsImage_WhenFileAdded_ThenEverythingElseIsKeptAndGeometryStays(string name) {
    var original = Image(name);
    var before = new ChfsReader(new MemoryStream(original, writable: false));
    using var img = new MemoryStream();
    img.Write(original);
    Descriptor.Add(img, [ArchiveInputInfo.InMemory("etc/new/added.txt", "added"u8, At), ArchiveInputInfo.InMemory("etc/motd", "replaced"u8, At)]);
    img.Position = 0;
    var after = new ChfsReader(img);
    Assert.That((after.IsNand, after.PageSize, after.EraseBlockSize), Is.EqualTo((before.IsNand, before.PageSize, before.EraseBlockSize)));
    Assert.That(after.Entries.Single(e => e.Path == "etc/new/added.txt").Data, Is.EqualTo("added"u8.ToArray()));
    Assert.That(after.Entries.Single(e => e.Path == "etc/motd").Data, Is.EqualTo("replaced"u8.ToArray()));
    var kept = before.Entries.Where(e => e.Path != "etc/motd").Select(e => (e.Path, e.Mode, e.Uid, e.Gid, e.ATime, e.MTime, e.CTime, e.Size, Convert.ToHexString(e.Data)));
    var now = after.Entries.Select(e => (e.Path, e.Mode, e.Uid, e.Gid, e.ATime, e.MTime, e.CTime, e.Size, Convert.ToHexString(e.Data)));
    Assert.That(now, Is.SupersetOf(kept));
  }

  [Test]
  public void GivenMakefsImage_WhenDirectoryRemoved_ThenItsSubtreeIsGoneAndTheRestStays() {
    var original = Image("chfs-nand");
    var before = new ChfsReader(new MemoryStream(original, writable: false));
    using var img = new MemoryStream();
    img.Write(original);
    Descriptor.Remove(img, ["deep", "bin/random.bin"]);
    img.Position = 0;
    var after = new ChfsReader(img);
    Assert.That(after.Entries.Select(e => e.Path),
      Is.EquivalentTo(before.Entries.Select(e => e.Path).Where(p => !p.StartsWith("deep", StringComparison.Ordinal) && p != "bin/random.bin")));
  }

  [Test]
  public void GivenMissingName_WhenRemoved_ThenFileNotFoundAndImageUnchanged() {
    var original = Image("chfs-nand");
    using var img = new MemoryStream();
    img.Write(original);
    Assert.Throws<FileNotFoundException>(() => Descriptor.Remove(img, ["no/such/file"]));
    Assert.That(img.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void GivenMakefsImage_WhenPurged_ThenNoFileIsLeftAndGeometryStays() {
    using var img = new MemoryStream();
    img.Write(Image("chfs-nor"));
    ((IArchivePurgeable)Descriptor).Purge(img);
    img.Position = 0;
    var r = new ChfsReader(img);
    Assert.That(r.Entries.Where(e => !e.IsDirectory), Is.Empty);
    Assert.That((r.IsNand, r.PageSize, r.EraseBlockSize), Is.EqualTo((false, 512, 16384)));
  }

  [Test]
  public void GivenImageWithHardLink_WhenModified_ThenRefusedAndUnchanged() {
    // two names for vnode 3, as a mounted file system writes after ln
    var img = Write(new ChfsWriteNode("", 0x41ED) { Children = [new ChfsWriteNode("a", 0x81A4) { Data = "x"u8.ToArray() }] });
    var linked = AppendDirent(img, vno: 3, parent: 2, "b");
    using var ms = new MemoryStream();
    ms.Write(linked);
    Assert.Throws<NotSupportedException>(() => Descriptor.Add(ms, [ArchiveInputInfo.InMemory("c", "y"u8, At)]));
    Assert.That(ms.ToArray(), Is.EqualTo(linked));
  }

  /// <summary>Appends a directory-entry node in the first eraseblock's free space.</summary>
  private static byte[] AppendDirent(byte[] img, ulong vno, ulong parent, string name) {
    var copy = (byte[])img.Clone();
    var pos = 4096;
    while (BitConverter.ToUInt16(copy, pos) == ChfsReader.NodeMagic) pos += (BitConverter.ToInt32(copy, pos + 4) + 3) & ~3;
    var n = Encoding.ASCII.GetBytes(name);
    var node = new byte[52];
    BitConverter.GetBytes(ChfsReader.NodeMagic).CopyTo(node, 0);
    BitConverter.GetBytes(ChfsReader.TypeDirent).CopyTo(node, 2);
    BitConverter.GetBytes(52 + n.Length).CopyTo(node, 4);
    BitConverter.GetBytes(Compression.Core.Checksums.Crc32.Compute(node.AsSpan(0, 8))).CopyTo(node, 8);
    BitConverter.GetBytes(vno).CopyTo(node, 12);
    BitConverter.GetBytes(parent).CopyTo(node, 20);
    BitConverter.GetBytes(100UL).CopyTo(node, 28);
    node[40] = (byte)n.Length;
    node[41] = 1;
    BitConverter.GetBytes(Compression.Core.Checksums.Crc32.Compute(n)).CopyTo(node, 44);
    BitConverter.GetBytes(Compression.Core.Checksums.Crc32.Compute(node.AsSpan(0, 48))).CopyTo(node, 48);
    node.CopyTo(copy, pos);
    n.CopyTo(copy, pos + 52);
    return copy;
  }

  [Test]
  public void GivenEraseCounts_WhenRewritten_ThenEachBlockKeepsItsCount() {
    var files = Enumerable.Range(0, 3).Select(i => new ChfsWriteNode($"f{i}", 0x81A4) { Data = new byte[100_000] }).ToList();
    using var ms = new MemoryStream();
    ChfsWriter.Write(ms, new ChfsWriteNode("", 0x41ED) { Children = files }, eraseCounts: [7, 0, 9]);
    Assert.That(new ChfsReader(new MemoryStream(ms.ToArray())).EraseCounts, Is.EqualTo(new uint[] { 7, 1, 9 }));
  }
}
