using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

/// <summary>
/// In-place edits, defragmentation and shrinking of encrypted volumes: new data enciphered under
/// fresh key versions, metadata republished under fresh node and journal sequences, so no nonce
/// ever enciphers two different things; checked by our reader, by <c>bcachefs fsck -n</c>
/// unlocked from the passphrase, and by the 6.8 kernel mounting key-in-the-clear volumes.
/// </summary>
[TestFixture]
public sealed class BcacheFsEncryptedEditTests {
  private const string Passphrase = "edit pass";
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_bcachefs_encedit_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static byte[] Payload(int length, int seed) {
    var result = new byte[length];
    for (var i = 0; i < result.Length; ++i)
      result[i] = (byte)(i * 7 + seed * 13 + i / 251);
    return result;
  }

  private string Write(string name, string? passphrase, bool wideMacs = false, long imageSize = 0) {
    var writer = new BcacheFsWriter();
    writer.SetEncryption(passphrase, wideMacs);
    if (imageSize > 0) writer.SetImageSize(imageSize);
    writer.AddFile("keep.txt", Encoding.ASCII.GetBytes("kept across every edit\n"));
    writer.AddFile("dir/big.bin", Payload(200_000, 1));
    writer.AddFile("dir/gone.bin", Payload(70_000, 2));
    writer.AddSymlink("link", "keep.txt");
    var path = Path.Combine(this._tmpDir, name);
    using var stream = File.Create(path);
    writer.WriteTo(stream);
    return path;
  }

  private static readonly ArchiveInputInfo[] Added = [
    ArchiveInputInfo.InMemory("new/added.bin", Payload(150_123, 3)),
    ArchiveInputInfo.InMemory("keep.txt", Encoding.ASCII.GetBytes("replaced in place\n")),
  ];

  /// <summary>Adds two files (one replacing another), removes one, through the descriptor with the password.</summary>
  private static void Edit(string path, string? passphrase) {
    var descriptor = new BcacheFsFormatDescriptor();
    using var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
    descriptor.Add(image, Added, new ArchiveMutationOptions { Password = passphrase });
    image.Position = 0;
    descriptor.Remove(image, ["dir/gone.bin"], new ArchiveMutationOptions { Password = passphrase });
  }

  private static Dictionary<string, string> Ours(string path, string? passphrase) {
    using var stream = File.OpenRead(path);
    using var reader = new BcacheFsReader(stream, passphrase);
    Assert.That(reader.Valid, Is.True, reader.Status);
    Assert.That(reader.Encrypted, Is.True);
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var d in reader.Directories.Where(d => d != "lost+found" && !d.StartsWith("lost+found/", StringComparison.Ordinal)))
      result[d] = "D";
    foreach (var e in reader.Entries)
      result[e.Name] = e.LinkTarget is { } t ? "L:" + t : Convert.ToHexStringLower(SHA256.HashData(reader.Read(e)));
    return result;
  }

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  private static void AssertEdited(Dictionary<string, string> tree) {
    Assert.Multiple(() => {
      Assert.That(tree["new/added.bin"], Is.EqualTo(Sha(Payload(150_123, 3))));
      Assert.That(tree["keep.txt"], Is.EqualTo(Sha(Encoding.ASCII.GetBytes("replaced in place\n"))));
      Assert.That(tree["dir/big.bin"], Is.EqualTo(Sha(Payload(200_000, 1))));
      Assert.That(tree.ContainsKey("dir/gone.bin"), Is.False);
      Assert.That(tree["link"], Is.EqualTo("L:keep.txt"));
    });
  }

  [TestCase(Passphrase, false)]
  [TestCase(null, false)]
  [TestCase(Passphrase, true)]
  [Category("RoundTrip")]
  public void GivenOurEncryptedVolume_WhenEditedWithItsKey_ThenTheEditsReadBackAndNothingIsInTheClear(string? passphrase, bool wideMacs) {
    var path = this.Write("edit.img", passphrase, wideMacs);
    Edit(path, passphrase);
    AssertEdited(Ours(path, passphrase));
    var bytes = File.ReadAllBytes(path);
    Assert.That(bytes.AsSpan().IndexOf("replaced in place"u8), Is.EqualTo(-1));
    Assert.That(bytes.AsSpan().IndexOf(Payload(150_123, 3).AsSpan(1000, 64)), Is.EqualTo(-1));
  }

  [Test, Category("Exception")]
  public void GivenAPassphraseVolume_WhenEditedWithoutThePassphrase_ThenItAsksForItAndChangesNothing() {
    var path = this.Write("nopass.img", Passphrase);
    var before = File.ReadAllBytes(path);
    var descriptor = new BcacheFsFormatDescriptor();
    using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
      Assert.Throws<UnauthorizedAccessException>(() => descriptor.Add(image, Added));
      image.Position = 0;
      Assert.Throws<UnauthorizedAccessException>(() =>
        descriptor.Remove(image, ["keep.txt"], new ArchiveMutationOptions { Password = "not it" }));
    }
    Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
  }

  [Test, Category("RoundTrip")]
  public void GivenAKeyInTheClearVolume_WhenDefragmentedAndShrunk_ThenItStaysEncryptedAndReadsTheSame() {
    var path = this.Write("defrag.img", null, imageSize: 192L << 20);
    Edit(path, null);
    var expected = Ours(path, null);
    var descriptor = new BcacheFsFormatDescriptor();
    using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
      descriptor.Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
    Assert.That(Ours(path, null), Is.EquivalentTo(expected), "defragmented");

    var shrunk = Path.Combine(this._tmpDir, "shrunk.img");
    using (var input = File.OpenRead(path))
    using (var output = File.Create(shrunk))
      descriptor.Shrink(input, output);
    Assert.That(new FileInfo(shrunk).Length, Is.LessThan(new FileInfo(path).Length), "the rebuild is smaller");
    Assert.That(Ours(shrunk, null), Is.EquivalentTo(expected), "shrunk");
  }

  [Test, Category("RoundTrip")]
  public void GivenAPassphraseVolume_WhenShrunkWithThePassphraseRemembered_ThenThePassphraseStillOpensIt() {
    var path = this.Write("shrink-pass.img", Passphrase, imageSize: 192L << 20);
    var expected = Ours(path, Passphrase);
    var shrunk = Path.Combine(this._tmpDir, "shrunk-pass.img");
    using (var input = File.OpenRead(path))
    using (var output = File.Create(shrunk))
    using (BcacheFsKeys.Remember(input, Passphrase))
      new BcacheFsFormatDescriptor().Shrink(input, output);
    Assert.That(new FileInfo(shrunk).Length, Is.LessThan(new FileInfo(path).Length), "the rebuild is smaller");
    Assert.That(Ours(shrunk, Passphrase), Is.EquivalentTo(expected));
  }

  // ── No nonce twice ─────────────────────────────────────────────────────────

  /// <summary>
  /// Every nonce the volume enciphers something under, with a hash of the ciphertext it covers:
  /// the journal set (its sequence), every b-tree node's first bset (sequence, journal sequence),
  /// and every encrypted extent (key version, compression, entry nonce).
  /// </summary>
  private static Dictionary<string, string> NonceCensus(string path) {
    var bytes = File.ReadAllBytes(path);
    using var image = new MemoryStream(bytes, writable: false);
    var census = new Dictionary<string, string>(StringComparer.Ordinal);
    var core = BcacheFsCoreVolume.Open(image);

    foreach (var sequence in core.Journal.Sequences)
      foreach (var replica in sequence.Replicas) {
        var at = replica.Sector * 512;
        var length = replica.RawBytes.Length;
        census[$"journal:{replica.Sequence}"] = Sha(bytes.AsSpan((int)at + 44, length - 44).ToArray());
      }

    var volume = BcacheFsVolume.Open(image);
    foreach (var id in Enum.GetValues<BcacheFsBtreeId>())
      foreach (var sector in volume.NodeSectors((int)id)) {
        var node = bytes.AsSpan((int)(sector * 512));
        var seq = BinaryPrimitives.ReadUInt64LittleEndian(node[136..]);
        var journalSeq = BinaryPrimitives.ReadUInt64LittleEndian(node[144..]);
        var end = 160 + BinaryPrimitives.ReadUInt16LittleEndian(node[158..]) * 8;
        census[$"node:{seq}:{journalSeq}"] = Sha(node[24..end].ToArray());
      }

    foreach (var key in BcacheFsBtreeReader.ReadTree(core, BcacheFsBtreeId.Extents).MaterializedLeafSlots) {
      if (key.Version.Lo == 0 || !BcacheFsExtentCodec.TryParseEntries(key.Value, out var entries, out _)) continue;
      BcacheFsExtentCrc? crc = null;
      foreach (var entry in entries) {
        if (entry.KnownType is BcacheFsExtentEntryType.Crc64 or BcacheFsExtentEntryType.Crc128)
          BcacheFsExtentCodec.TryReadExtentCrc(entry, out crc, out _);
        else if (entry.KnownType == BcacheFsExtentEntryType.Pointer && crc != null) {
          var sector = BcacheFsFormat.PointerSector(BinaryPrimitives.ReadUInt64LittleEndian(entry.RawBytes));
          var nonce = BcacheFsNonce.ForExtent(key.Version, crc);
          census[$"extent:{nonce}"] = Sha(bytes.AsSpan((int)(sector * 512), (int)crc.CompressedSize * 512).ToArray());
          break;
        }
      }
    }
    return census;
  }

  /// <summary>
  /// Two successive edits (and a defragmentation) never encipher different bytes under a nonce
  /// used before: whatever nonce reappears covers exactly the ciphertext it covered then.
  /// </summary>
  [Test, Category("Security")]
  public void GivenSuccessiveEdits_WhenEveryNonceIsCensused_ThenNoNonceEverCoversDifferentCiphertext() {
    var path = this.Write("nonces.img", null);
    var seen = new Dictionary<string, string>(StringComparer.Ordinal);
    void Census(string step) {
      foreach (var (nonce, cipher) in NonceCensus(path)) {
        if (seen.TryGetValue(nonce, out var before))
          Assert.That(cipher, Is.EqualTo(before), $"{step}: nonce {nonce} reused for different ciphertext");
        seen[nonce] = cipher;
      }
    }

    Census("written");
    var descriptor = new BcacheFsFormatDescriptor();
    for (var round = 0; round < 2; ++round) {
      using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        descriptor.Add(image, [ArchiveInputInfo.InMemory($"round{round}.bin", Payload(90_000, round + 10)),
          ArchiveInputInfo.InMemory("keep.txt", Encoding.ASCII.GetBytes($"edit {round}\n"))]);
      Census($"edit {round}");
    }
    using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
      descriptor.Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
    Census("defragmented");
    Assert.That(seen.Keys.Count(k => k.StartsWith("journal:", StringComparison.Ordinal)), Is.GreaterThanOrEqualTo(4),
      "every publish used a journal sequence of its own");
  }

  // ── The tools and the kernel as judges ─────────────────────────────────────

  private static void AssertFsckClean(string path, string? passphrase) {
    BcacheFsOracleGate.RequireBcachefsTools();
    var input = passphrase == null ? "" : $"printf '%s\\n' '{passphrase}' | ";
    var result = FsInteropToolbox.RunWsl($"{input}bcachefs fsck -n {FsInteropToolbox.WinToWsl(path)}");
    var output = result.StdOut + "\n" + result.StdErr;
    TestContext.Out.WriteLine(output);
    Assert.That(result.ExitCode, Is.EqualTo(0), output);
    Assert.That(output, Does.Contain("check_nlinks"), output);
    Assert.That(output, Does.Not.Contain("fixing"), output);
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenOurPassphraseVolume_WhenEditedInPlace_ThenBcachefsFsckUnlocksItAndFindsItClean() {
    var path = this.Write("fsck-edit.img", Passphrase);
    Edit(path, Passphrase);
    AssertFsckClean(path, Passphrase);
    AssertEdited(Ours(path, Passphrase));
  }

  private const string ListingScript = """
    cd /sysroot
    find . -mindepth 1 ! -path './lost+found' ! -path './lost+found/*' | LC_ALL=C sort | while IFS= read -r p; do
      if [ -L "$p" ]; then c="L:$(readlink "$p")"; elif [ -f "$p" ]; then c=$(sha256sum < "$p" | cut -c1-64); else c=D; fi
      printf 'E|%s|%s\n' "${p#./}" "$c"
    done
    """;

  private static Dictionary<string, string> Listing(string output) {
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var raw in output.Split('\n')) {
      var line = raw.TrimEnd('\r');
      if (!line.StartsWith("E|", StringComparison.Ordinal)) continue;
      var f = line.Split('|');
      result[f[1]] = f[2];
    }
    return result;
  }

  /// <summary>
  /// The kernel stores encrypted zstd extents in our key-in-the-clear volume; defragmentation
  /// moves each region as stored (MAC, nonce and compression kept, no nonce reused), and fsck and
  /// the kernel agree with our reader afterwards.
  /// </summary>
  [Test, Category("ExternalFsInterop")]
  public void GivenEncryptedCompressedExtentsTheKernelWrote_WhenDefragmented_ThenTheyStayCompressedAndTheKernelAndFsckAgree() {
    BcacheFsKernel.Require();
    var path = this.Write("kernel-zstd.img", null, imageSize: 192L << 20);
    BcacheFsKernel.Session(path, """
      set -e
      cd /sysroot
      mkdir -p packed
      seq 1 80000 > packed/seq.txt
      yes 'an encrypted line the kernel compresses' | head -c 400000 > packed/yes.txt
      sync
      """, "noatime,compression=zstd");
    var compressed = BcacheFsKernelMountTests.CompressedRegions(path);
    Assert.That(compressed, Is.GreaterThan(0), "precondition: the kernel stored compressed extents");

    var seen = NonceCensus(path);
    using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
      new BcacheFsFormatDescriptor().Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
    foreach (var (nonce, cipher) in NonceCensus(path))
      if (seen.TryGetValue(nonce, out var before))
        Assert.That(cipher, Is.EqualTo(before), $"nonce {nonce} reused for different ciphertext");

    AssertFsckClean(path, null);
    Assert.That(Listing(BcacheFsKernel.Session(path, ListingScript)), Is.EquivalentTo(Ours(path, null)));
    Assert.That(BcacheFsKernelMountTests.CompressedRegions(path), Is.EqualTo(compressed), "the moved extents stay compressed");
  }

  /// <summary>
  /// The kernel writes into our key-in-the-clear volume, we edit, defragment and shrink it, and
  /// the kernel must see exactly what we read, and fsck must find nothing to repair, each time.
  /// </summary>
  [Test, Category("ExternalFsInterop")]
  public void GivenAVolumeTheKernelWroteInto_WhenWeEditDefragmentAndShrinkIt_ThenTheKernelAndFsckAgree() {
    BcacheFsKernel.Require();
    var path = this.Write("kernel-edit.img", null, wideMacs: true, imageSize: 192L << 20);
    BcacheFsKernel.Session(path, "set -e; cd /sysroot; mkdir -p k; seq 1 30000 > k/seq.txt; printf tiny > k/tiny; rm dir/gone.bin; sync");
    AssertFsckClean(path, null);

    var descriptor = new BcacheFsFormatDescriptor();
    using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
      descriptor.Add(image, Added);
      image.Position = 0;
      descriptor.Remove(image, ["k/seq.txt"]);
    }
    AssertFsckClean(path, null);
    Assert.That(Listing(BcacheFsKernel.Session(path, ListingScript)), Is.EquivalentTo(Ours(path, null)), "after the edit");

    using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
      descriptor.Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
    AssertFsckClean(path, null);
    Assert.That(Listing(BcacheFsKernel.Session(path, ListingScript)), Is.EquivalentTo(Ours(path, null)), "after defragmentation");

    var shrunk = Path.Combine(this._tmpDir, "kernel-shrunk.img");
    using (var input = File.OpenRead(path))
    using (var output = File.Create(shrunk))
      descriptor.Shrink(input, output);
    Assert.That(new FileInfo(shrunk).Length, Is.LessThan(new FileInfo(path).Length), "the rebuild is smaller");
    AssertFsckClean(shrunk, null);
    Assert.That(Listing(BcacheFsKernel.Session(shrunk, ListingScript)), Is.EquivalentTo(Ours(shrunk, null)), "after shrinking");
  }
}
