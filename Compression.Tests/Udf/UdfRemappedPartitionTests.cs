using System.IO.Compression;
using System.Reflection;
using System.Text;
using FileSystem.Udf;

namespace Compression.Tests.Udf;

/// <summary>
/// UDF volumes whose blocks go through a remapping partition: a sparable partition (CD-RW /
/// DVD±RW, sparing table) whose files the Linux kernel's <c>udf</c> driver wrote, and the two
/// write-once layouts <c>mkudffs</c> formats, CD-R (UDF 2.01, VAT) and BD-R (UDF 2.50, metadata
/// partition over a VAT), which the kernel reads but cannot extend. Every byte read here was laid
/// out by foreign software.
/// <see cref="Given_UdfToolsAndTheKernel_When_TheVectorsAreRegenerated_Then_TheyReadTheSame"/>
/// makes them (WSL, udftools 2.3, Ubuntu 24.04 kernel).
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
public class UdfRemappedPartitionTests {

  /// <summary>The files the kernel wrote into the sparable and metadata volumes.</summary>
  internal static IReadOnlyList<(string Path, byte[] Data)> Inputs() {
    var big = new byte[300_000];
    for (var i = 0; i < big.Length; ++i)
      big[i] = (byte)((i * 2_654_435_761L) >> 13);
    return [
      ("a.bin", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("remapped udf block ", 300)))),
      ("dir/b.txt", "written by the kernel's udf driver\n"u8.ToArray()),
      ("dir/sub/big.bin", big),
      ("dir/sub/empty.dat", []),
    ];
  }

  private static byte[] Image(string name) {
    using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("UdfVectors." + name + ".img.gz")
      ?? throw new InvalidOperationException($"Missing embedded vector {name}.");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    using var copy = new MemoryStream();
    gzip.CopyTo(copy);
    return copy.ToArray();
  }

  private static Dictionary<string, byte[]> ReadAll(byte[] image) {
    using var reader = new UdfReader(new MemoryStream(image));
    Assert.That(reader.UsesRemappedPartitions, Is.True);
    return reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, reader.Extract, StringComparer.Ordinal);
  }

  [Test]
  public void Given_AKernelWrittenSparableVolume_When_Read_Then_EveryFileMatchesWhatTheKernelWrote() {
    var files = ReadAll(Image("sparable"));
    foreach (var (path, data) in Inputs()) {
      Assert.That(files, Does.ContainKey(path), path);
      Assert.That(files[path], Is.EqualTo(data), path);
    }
    Assert.That(files, Has.Count.EqualTo(Inputs().Count), "no invented files");
  }

  [TestCase("vat", Description = "CD-R, UDF 2.01: FSD and root through the VAT")]
  [TestCase("bdr", Description = "BD-R, UDF 2.50: metadata partition over the VAT")]
  public void Given_AWriteOnceVolume_When_Read_Then_ItsRootResolvesThroughTheVirtualAllocationTable(string volume) {
    // mkudffs formats these with the File Set Descriptor and root directory at virtual
    // addresses; the Linux driver mounts VAT volumes read-only, so the root holds no files.
    using var reader = new UdfReader(new MemoryStream(Image(volume)));
    Assert.That(reader.UsesRemappedPartitions, Is.True);
    Assert.That(reader.Entries, Is.Empty);
  }

  [Test]
  public void Given_ARelocatedPacket_When_Read_Then_TheSparingTableIsFollowed() {
    // Move the packet that holds a.bin to the spare area the first free sparing-table entry
    // names, record the move in every copy of the table, and blank the original packet.
    var image = Image("sparable");
    var before = ReadAll(image)["a.bin"];
    var (packetStart, packetLength, spareSector) = UdfSparingSurgery.RelocateFirstFilePacket(image, "a.bin");
    Assert.That(packetLength, Is.GreaterThan(0));
    var after = ReadAll(image);
    Assert.That(after["a.bin"], Is.EqualTo(before));
    Assert.That(spareSector, Is.Not.EqualTo(packetStart));
  }

  [Test]
  public void Given_ACorruptedVat_When_Read_Then_TheVolumeIsRefused() {
    var image = Image("vat");
    // The VAT is the last File Entry of the volume; destroying its tag leaves nothing to map with.
    for (var i = image.Length - 8 * 2048; i < image.Length; i += 2048)
      if (BitConverter.ToUInt16(image, i) is 261 or 266)
        image[i + 4] ^= 0xFF;
    Assert.Catch<Exception>(() => _ = new UdfReader(new MemoryStream(image)));
  }

  [TestCase("sparable")]
  [TestCase("vat")]
  [TestCase("bdr")]
  public void Given_ARemappedVolume_When_Edited_Then_ItIsRefusedAndLeftUntouched(string volume) {
    var original = Image(volume);
    using var stream = new MemoryStream();
    stream.Write(original);
    var descriptor = new UdfFormatDescriptor();
    Assert.Throws<NotSupportedException>(() => descriptor.Remove(stream, ["a.bin"]));
    Assert.Throws<NotSupportedException>(() => descriptor.WipeUnusedSpace(stream));
    Assert.Throws<NotSupportedException>(() => descriptor.Defragment(stream));
    Assert.Throws<NotSupportedException>(() => descriptor.EnumerateExtents(stream).ToList());
    Assert.That(stream.ToArray(), Is.EqualTo(original));
  }

  /// <summary>
  /// Formats the three volumes with mkudffs, fills two of them through the kernel's udf driver
  /// in the libguestfs appliance, and checks our reader against them; with
  /// <c>CWB_UDF_WRITE_VECTORS=1</c> it stores them (gzipped) as the committed vectors.
  /// </summary>
  [Test]
  [Category("ExternalInterop")]
  public void Given_UdfToolsAndTheKernel_When_TheVectorsAreRegenerated_Then_TheyReadTheSame() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("mkudffs") || !FsInteropToolbox.WslHasTool("guestfish"))
      Assert.Ignore("mkudffs (udftools) and guestfish (libguestfs-tools) are needed in WSL.");

    var dir = Directory.CreateTempSubdirectory("cwb-udf-gen-").FullName;
    try {
      var input = Directory.CreateDirectory(Path.Combine(dir, "in")).FullName;
      foreach (var (path, data) in Inputs())
        File.WriteAllBytes(Path.Combine(input, path.Replace('/', '_')), data);
      var fill = new StringBuilder("run\nmount-vfs \"\" udf /dev/sda /\nmkdir-p /dir/sub\n");
      foreach (var (path, _) in Inputs())
        fill.Append($"upload in/{path.Replace('/', '_')} /{path}\n");
      fill.Append("umount-all\n");
      File.WriteAllText(Path.Combine(dir, "fill.gf"), fill.ToString().Replace("\r\n", "\n"));

      const string sudo = "echo 1234 | sudo -S";
      var script = string.Join(" && ", [
        $"export LIBGUESTFS_BACKEND=direct; {sudo} chmod 0666 /dev/kvm 2>/dev/null; {sudo} chmod 0644 /boot/vmlinuz-* 2>/dev/null; cd {FsInteropToolbox.WinToWsl(dir)}",
        "truncate -s 16M sparable.img && mkudffs --media-type=cdrw --udfrev=2.01 --label=SPARABLE sparable.img",
        // Write-once media end at their VAT: cut the image there, as a disc's last written block.
        "truncate -s 16M vat.img && vb=$(mkudffs --media-type=cdr --udfrev=2.01 --label=VAT vat.img | sed -n 's/^vatblock=//p') && truncate -s $(( (vb + 1) * 2048 )) vat.img",
        "truncate -s 16M bdr.img && vb=$(mkudffs --media-type=bdr --udfrev=2.50 --label=BDR bdr.img | sed -n 's/^vatblock=//p') && truncate -s $(( (vb + 1) * 2048 )) bdr.img",
        "timeout 900 guestfish -a sparable.img -f fill.gf",
        // The kernel reads both write-once volumes (read-only: it cannot extend a VAT).
        "for v in vat bdr; do printf 'run\\nmount-ro /dev/sda /\\nfind /\\numount-all\\n' | timeout 900 guestfish -a $v.img > $v.list; done",
      ]);
      var (stdout, stderr, code) = FsInteropToolbox.RunWsl(script);
      Assert.That(code, Is.Zero, stdout + stderr);

      var files = ReadAll(File.ReadAllBytes(Path.Combine(dir, "sparable.img")));
      foreach (var (path, data) in Inputs())
        Assert.That(files[path], Is.EqualTo(data), $"sparable: {path}");
      foreach (var volume in new[] { "vat", "bdr" }) {
        // The kernel lists nothing but lost+found-free roots on these freshly formatted discs.
        Assert.That(File.ReadAllText(Path.Combine(dir, volume + ".list")).Trim(), Is.Empty, volume);
        using var reader = new UdfReader(File.OpenRead(Path.Combine(dir, volume + ".img")));
        Assert.That(reader.UsesRemappedPartitions, Is.True, volume);
        Assert.That(reader.Entries, Is.Empty, volume);
      }

      if (Environment.GetEnvironmentVariable("CWB_UDF_WRITE_VECTORS") == "1") {
        var vectors = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "Udf", "ReferenceVectors");
        Directory.CreateDirectory(vectors);
        foreach (var volume in new[] { "sparable", "vat", "bdr" }) {
          using var source = File.OpenRead(Path.Combine(dir, volume + ".img"));
          using var target = File.Create(Path.Combine(vectors, volume + ".img.gz"));
          using var gzip = new GZipStream(target, CompressionLevel.SmallestSize);
          source.CopyTo(gzip);
        }
      }
    } finally {
      Directory.Delete(dir, true);
    }
  }
}

/// <summary>Moves a packet of a sparable UDF volume into its spare area, as a drive does when it remaps.</summary>
internal static class UdfSparingSurgery {

  /// <summary>Returns the packet's partition-relative start, its length and the absolute spare sector it went to.</summary>
  internal static (uint PacketStart, int PacketLength, uint SpareSector) RelocateFirstFilePacket(byte[] image, string file) {
    const int block = 2048;
    var lvd = FindDescriptor(image, 6);
    var pd = FindDescriptor(image, 5);
    var partitionStart = BitConverter.ToUInt32(image, pd + 188);
    var maps = lvd + 440;
    int map = -1;
    for (var at = maps; at < maps + BitConverter.ToUInt32(image, lvd + 264); at += image[at + 1])
      if (image[at] == 2 && Encoding.ASCII.GetString(image, at + 5, 23).StartsWith("*UDF Sparable Partition", StringComparison.Ordinal))
        map = at;
    Assert.That(map, Is.GreaterThan(0), "no sparable partition map");
    var packetLength = BitConverter.ToUInt16(image, map + 40);
    var tables = image[map + 42];

    long dataOffset;
    using (var reader = new UdfReader(new MemoryStream(image)))
      dataOffset = reader.Entries.Single(e => e.Name == file).DataOffset;
    var lbn = (uint)(dataOffset / block - partitionStart);
    var packet = lbn & ~(uint)(packetLength - 1);

    // The first table copy is the one a reader consults; record the move there.
    Assert.That(tables, Is.GreaterThan(0));
    uint spare = 0;
    var table = (int)BitConverter.ToUInt32(image, map + 48) * block;
    var entries = BitConverter.ToUInt16(image, table + 48);
    for (var e = 0; e < entries; ++e) {
      var entry = table + 56 + e * 8;
      if (BitConverter.ToUInt32(image, entry) != 0xFFFFFFFF) // available for sparing (UDF 2.60 2.2.12)
        continue;
      spare = BitConverter.ToUInt32(image, entry + 4);
      BitConverter.GetBytes(packet).CopyTo(image, entry);
      break;
    }
    Assert.That(spare, Is.Not.Zero, "no free sparing-table entry");

    var from = (int)((partitionStart + packet) * (long)block);
    Array.Copy(image, from, image, (int)(spare * (long)block), packetLength * block);
    Array.Clear(image, from, packetLength * block);
    return (packet, packetLength, spare);
  }

  private static int FindDescriptor(byte[] image, ushort tag) {
    var anchor = 256 * 2048;
    var location = (int)BitConverter.ToUInt32(image, anchor + 20);
    for (var i = 0; i < 64; ++i) {
      var at = (location + i) * 2048;
      if (BitConverter.ToUInt16(image, at) == tag)
        return at;
    }
    throw new InvalidOperationException($"descriptor {tag} not found");
  }
}
