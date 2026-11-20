#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.UnixV6;

namespace Compression.Tests.UnixV6;

/// <summary>
/// The V6 writer: round trips through our reader, the block accounting <c>icheck</c> does,
/// and (where SIMH and the V6 kit are installed in WSL) the V6 system's own checks.
/// </summary>
[TestFixture]
public sealed class UnixV6WriterTests {

  private const uint Time = 0x2A3B4C5D;

  private static byte[] Pattern(int length, int seed) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)(i * 31 + seed + (i >> 9));
    return data;
  }

  private static readonly byte[] Hello = "hello from our V6 writer\n"u8.ToArray();
  private static readonly byte[] Big = Pattern(1800 * 512 + 77, 1); // past 1792 blocks: double-indirect
  private static readonly byte[] Notes = Pattern(8 * 512, 2);       // exactly eight blocks: still small
  private static readonly byte[] Nine = Pattern(8 * 512 + 1, 3);    // one byte more: large

  private static UnixV6WriteNode Tree() => new("", Children: [
    new("hello", Hello) { Time = Time },
    new("big", Big) { Time = Time },
    new("empty", []) { Time = Time },
    new("docs", Children: [
      new("notes", Notes) { Time = Time, Permissions = 0x180 },
      new("nine", Nine) { Time = Time },
      new("deep", Children: [new("leaf", "leaf\n"u8.ToArray()) { Time = Time }]) { Time = Time },
    ]) { Time = Time },
    new("void", Children: []) { Time = Time },
  ]) { Time = Time };

  private static byte[] Volume(int blocks = 0) {
    using var ms = new MemoryStream();
    UnixV6Writer.Write(ms, Tree(), blocks, Time);
    return ms.ToArray();
  }

  [Test]
  public void GivenATree_WhenWrittenAndRead_ThenEveryEntryComesBack() {
    using var r = new UnixV6Reader(new MemoryStream(Volume()));
    var byName = r.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
    Assert.That(byName.Keys, Is.EquivalentTo(new[] { "hello", "big", "empty", "docs", "docs/notes", "docs/nine", "docs/deep", "docs/deep/leaf", "void" }));
    Assert.That(r.Extract(byName["hello"]), Is.EqualTo(Hello));
    Assert.That(r.Extract(byName["big"]), Is.EqualTo(Big));
    Assert.That(r.Extract(byName["empty"]), Is.Empty);
    Assert.That(r.Extract(byName["docs/notes"]), Is.EqualTo(Notes));
    Assert.That(r.Extract(byName["docs/nine"]), Is.EqualTo(Nine));
    Assert.That(r.Extract(byName["docs/deep/leaf"]), Is.EqualTo("leaf\n"u8.ToArray()));
    Assert.That(byName["docs"].IsDirectory && byName["void"].IsDirectory, Is.True);
    Assert.That(byName["hello"].LastModified, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(Time).UtcDateTime));
  }

  [Test]
  public void GivenAVolume_WhenItsBlocksAreAccounted_ThenEachBlockIsUsedOrFreeExactlyOnce() {
    var image = Volume();
    var isize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(512));
    var fsize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(514));
    Assert.That(image, Has.Length.EqualTo(fsize * 512));
    var owner = new int[fsize];

    void Claim(int block, int by) {
      Assert.That(block, Is.InRange(isize + 2, fsize - 1), $"block {block} of {by}");
      Assert.That(owner[block], Is.Zero, $"block {block} claimed twice");
      owner[block] = by;
    }
    int Word(int block, int slot) => BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(block * 512 + slot * 2));

    for (var ino = 1; ino <= isize * 16; ++ino) {
      var inode = image.AsSpan(1024 + (ino - 1) * 32, 32);
      var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
      if ((mode & 0x8000) == 0) { Assert.That(inode.ToArray(), Is.All.Zero); continue; }
      var size = (inode[5] << 16) | BinaryPrimitives.ReadUInt16LittleEndian(inode[6..]);
      var count = (size + 511) / 512;
      var addr = new int[8];
      for (var a = 0; a < 8; ++a) addr[a] = BinaryPrimitives.ReadUInt16LittleEndian(inode[(8 + a * 2)..]);
      if ((mode & 0x1000) == 0) {
        Assert.That(count, Is.LessThanOrEqualTo(8));
        for (var b = 0; b < 8; ++b) if (b < count) Claim(addr[b], ino); else Assert.That(addr[b], Is.Zero);
        continue;
      }
      Assert.That(count, Is.GreaterThan(8), "only files past eight blocks are large");
      var indirects = (count + 255) / 256;
      for (var i = 0; i < Math.Min(7, indirects); ++i) Claim(addr[i], ino);
      var tables = Enumerable.Range(0, Math.Min(7, indirects)).Select(i => addr[i]).ToList();
      if (indirects > 7) {
        Claim(addr[7], ino);
        for (var i = 7; i < indirects; ++i) { Claim(Word(addr[7], i - 7), ino); tables.Add(Word(addr[7], i - 7)); }
      }
      for (var b = 0; b < count; ++b) Claim(Word(tables[b / 256], b % 256), ino);
    }

    // walk the free chain the way the kernel's alloc does
    var nfree = BinaryPrimitives.ReadInt16LittleEndian(image.AsSpan(516));
    var free = Enumerable.Range(0, nfree).Select(k => (int)BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(518 + k * 2))).ToArray();
    var freeCount = 0;
    while (true) {
      for (var k = free.Length - 1; k >= 1; --k) { Claim(free[k], -1); ++freeCount; }
      if (free[0] == 0) break;
      var link = free[0];
      Claim(link, -1);
      ++freeCount;
      var n = Word(link, 0);
      Assert.That(n, Is.EqualTo(100), "each chained set is full");
      free = Enumerable.Range(0, n).Select(k => Word(link, 1 + k)).ToArray();
    }
    Assert.That(freeCount, Is.EqualTo(UnixV6Writer.DefaultSpareBlocks));
    Assert.That(owner.Skip(isize + 2).Count(o => o == 0), Is.Zero, "no block is missing");
  }

  [Test]
  public void GivenAVolume_WhenLinksAreCounted_ThenEachInodeHasAsManyAsEntriesNameIt() {
    var image = Volume();
    using var r = new UnixV6Reader(new MemoryStream(image));
    var references = new Dictionary<int, int>();
    foreach (var dir in r.Entries.Where(e => e.IsDirectory).Select(e => e.Inode).Append(1)) {
      var inode = image.AsSpan(1024 + (dir - 1) * 32, 32);
      var size = BinaryPrimitives.ReadUInt16LittleEndian(inode[6..]);
      var block = BinaryPrimitives.ReadUInt16LittleEndian(inode[8..]);
      for (var at = 0; at < size; at += 16) {
        var child = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(block * 512 + at));
        references[child] = references.GetValueOrDefault(child) + 1;
      }
    }
    foreach (var (ino, count) in references)
      Assert.That(image[1024 + (ino - 1) * 32 + 2], Is.EqualTo(count), $"i_nlink of inode {ino}");
  }

  [Test]
  public void GivenPermissions_WhenWritten_ThenTheModeCarriesThemAndTheType() {
    var image = Volume();
    using var r = new UnixV6Reader(new MemoryStream(image));
    int Mode(string name) => BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(1024 + (r.Entries.Single(e => e.Name == name).Inode - 1) * 32));
    Assert.That(Mode("docs/notes"), Is.EqualTo(0x8000 | 0x180));
    Assert.That(Mode("docs/nine"), Is.EqualTo(0x8000 | 0x1000 | 0x1A4));
    Assert.That(Mode("docs"), Is.EqualTo(0x8000 | 0x4000 | 0x1ED));
  }

  [Test]
  public void GivenAVolumeSize_WhenWritten_ThenTheImageHasIt() {
    Assert.That(Volume(UnixV6Writer.Rk05Blocks), Has.Length.EqualTo(UnixV6Writer.Rk05Blocks * 512));
    Assert.Throws<ArgumentOutOfRangeException>(() => Volume(100));
    Assert.Throws<InvalidOperationException>(() => Volume(0x10000));
  }

  [Test]
  public void GivenAnEmptyRoot_WhenWritten_ThenItReadsAsEmpty() {
    using var ms = new MemoryStream();
    UnixV6Writer.Write(ms, new("", Children: []));
    using var r = new UnixV6Reader(new MemoryStream(ms.ToArray()));
    Assert.That(r.Entries, Is.Empty);
  }

  [TestCase("fifteen-letters")]
  [TestCase("")]
  [TestCase("a/b")]
  [TestCase("..")]
  [TestCase("€")]
  public void GivenAnInvalidName_WhenWritten_ThenItIsRejected(string name) =>
    Assert.Throws<ArgumentException>(() => UnixV6Writer.Write(Stream.Null, new("", Children: [new(name, [1])])));

  [Test]
  public void GivenTheLongestName_WhenWritten_ThenItReadsBack() {
    using var ms = new MemoryStream();
    UnixV6Writer.Write(ms, new("", Children: [new("fourteen-chars", [1])]));
    using var r = new UnixV6Reader(new MemoryStream(ms.ToArray()));
    Assert.That(r.Entries.Single().Name, Is.EqualTo("fourteen-chars"));
  }

  [Test]
  public void GivenADuplicateName_WhenWritten_ThenItIsRejected() =>
    Assert.Throws<ArgumentException>(() => UnixV6Writer.Write(Stream.Null, new("", Children: [new("a", [1]), new("a", Children: [])])));

  [Test]
  public void GivenAFileAsRoot_WhenWritten_ThenItIsRejected() =>
    Assert.Throws<ArgumentException>(() => UnixV6Writer.Write(Stream.Null, new("", [1])));

  [Test]
  public void GivenTheDescriptor_WhenCreating_ThenTheVolumeListsTheInputs() {
    var d = new UnixV6FormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [
      ArchiveInputInfo.InMemory("a/b/c.txt", "c\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("top.txt", "top\n"u8.ToArray()),
    ], new FormatCreateOptions());
    var list = d.List(new MemoryStream(ms.ToArray()), null);
    Assert.That(list.Select(e => e.Name), Is.EquivalentTo(new[] { "a", "a/b", "a/b/c.txt", "top.txt" }));
    using var r = new UnixV6Reader(new MemoryStream(ms.ToArray()));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "a/b/c.txt")), Is.EqualTo("c\n"u8.ToArray()));
  }

  /// <summary>
  /// The V6 system on SIMH's PDP-11 (the uv6swre kit's root volume, Caldera ancient UNIX
  /// licence) checks our volume: <c>icheck</c> and <c>dcheck</c> report nothing, the kernel
  /// mounts and lists it and copies files inside it, and both checks still pass afterwards.
  /// The copies, read back here, prove the kernel read our block maps as we wrote them.
  /// </summary>
  [Test, Category("ExternalInterop")]
  public void GivenOurVolume_WhenV6ChecksMountsAndWritesIt_ThenEverythingAgrees() {
    if (!FsInteropToolbox.WslHasTool("pdp11")) Assert.Ignore("SIMH's pdp11 is not installed in WSL.");
    var kit = Environment.GetEnvironmentVariable("CWB_V6_KIT") ?? "$HOME/v6kit";
    var probe = FsInteropToolbox.RunWsl($"test -f {kit}/unix0_v6_rk.dsk && echo present");
    if (!probe.StdOut.Contains("present")) Assert.Ignore($"the V6 kit (unix0_v6_rk.dsk) is not in {kit}; set CWB_V6_KIT.");
    var dir = Path.Combine(Path.GetTempPath(), "cwb-v6-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var image = Path.Combine(dir, "ours.v6");
      File.WriteAllBytes(image, Volume(UnixV6Writer.Rk05Blocks));
      var script = FsInteropToolbox.WinToWsl(Path.Combine(Documentation.FilesystemReadmeIsCurrentTests.RepositoryRoot(), "Compression.Tests", "UnixV6", "ReferenceVectors", "v6check.py"));
      var run = FsInteropToolbox.RunWsl($"timeout 900 python3 -I {script} {kit}/unix0_v6_rk.dsk {FsInteropToolbox.WinToWsl(image)}");
      var log = run.StdOut;
      TestContext.Out.WriteLine(log);
      Assert.That(log, Does.Contain("== done"), log + run.StdErr);

      string Step(string name) {
        // the output line, not the echoed command line "# echo == name"
        var start = log.IndexOf("\n== " + name + "\n", StringComparison.Ordinal) + 1;
        var end = log.IndexOf("\n== ", start + 3, StringComparison.Ordinal);
        return log[start..end];
      }
      foreach (var check in new[] { "icheck", "icheck2" }) {
        var report = Step(check);
        Assert.That(report, Does.Not.Contain("dup").And.Not.Contain("bad").And.Not.Contain("missing"), report);
        // every data block is either used or free: icheck reports no "missing" and the two add up
        int Count(string label) => int.Parse(report.Split('\n').Single(l => l.StartsWith(label + " ", StringComparison.Ordinal))[label.Length..].Trim());
        var isize = BinaryPrimitives.ReadUInt16LittleEndian(File.ReadAllBytes(image).AsSpan(512));
        Assert.That(Count("used") + Count("free"), Is.EqualTo(UnixV6Writer.Rk05Blocks - isize - 2), log);
      }
      Assert.That(Step("icheck"), Does.Contain("huge       1"), "big is double-indirect");
      foreach (var check in new[] { "dcheck", "dcheck2" }) {
        var lines = Step(check).Split('\n').Where(l => l.Length > 0 && !l.StartsWith("==") && !l.StartsWith("dcheck") && !l.StartsWith("/dev/rk4") && !l.StartsWith("#")).ToList();
        Assert.That(lines, Is.Empty, Step(check));
      }
      var listing = Step("ls");
      Assert.That(listing, Does.Contain(" big").And.Contain(" hello").And.Contain(" notes").And.Contain(" deep"), listing);
      Assert.That(listing, Does.Contain(Big.Length.ToString()), listing);

      using var r = new UnixV6Reader(new MemoryStream(File.ReadAllBytes(image)));
      byte[] Read(string name) => r.Extract(r.Entries.Single(e => e.Name == name));
      Assert.That(Read("big.v6"), Is.EqualTo(Big));
      Assert.That(Read("docs/notes.v6"), Is.EqualTo(Notes));
      Assert.That(Read("made/hello"), Is.EqualTo(Hello));
      Assert.That(Read("big"), Is.EqualTo(Big));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
