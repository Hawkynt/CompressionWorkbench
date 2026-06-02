using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.SysV;

namespace Compression.Tests.SysV;

[TestFixture]
public class SysVTests {

  // Minimal s5fs image (1024-byte blocks, type code 2):
  //   Block 0     boot (zeroed)
  //   Block 1     superblock (magic 0xFD187E20 at +504, type=2 at +508)
  //   Block 2     ilist (inode table) — inode 2 = root, inode 3 = file
  //   Block 3     root dir data (16-byte records)
  //   Block 4     file data
  private static byte[] BuildMinimalSysV() {
    var image = new byte[8 * 1024];

    // Superblock
    var sb = 1024;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 0, 2), 1);    // s_isize: 1 block ilist
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 2, 4), 8);    // s_fsize: 8 blocks
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 504, 4), 0xFD187E20);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 508, 4), 2);  // 1024-byte blocks

    // Inode table at block 2 = offset 2048. Inode 1 unused (inum starts at 1
    // but root is inode 2). Inode N is at inodeTable + (N-1)*64.
    var ilist = 2 * 1024;

    // Inode 2 (root dir): mode=0x41ED, size=48 (3 entries of 16), zones[0]=3
    var ino2 = ilist + (2 - 1) * 64;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ino2 + 0, 2), 0x41ED);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ino2 + 8, 4), 48);
    Write24(image.AsSpan(ino2 + 12), 3);

    // Inode 3 (file): mode=0x81A4, size=23, zones[0]=4
    var content = "Hello from System V!\n#1"u8.ToArray();
    var ino3 = ilist + (3 - 1) * 64;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ino3 + 0, 2), 0x81A4);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ino3 + 8, 4), (uint)content.Length);
    Write24(image.AsSpan(ino3 + 12), 4);

    // Root dir at block 3 (offset 3*1024 = 3072): 16-byte records (u16 ino + 14 name)
    var rootDir = 3 * 1024;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootDir + 0, 2), 2);
    image[rootDir + 2] = (byte)'.';
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootDir + 16, 2), 2);
    image[rootDir + 18] = (byte)'.';
    image[rootDir + 19] = (byte)'.';
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootDir + 32, 2), 3);
    Encoding.ASCII.GetBytes("readme").CopyTo(image.AsSpan(rootDir + 34, 14));

    // File data at block 4
    content.CopyTo(image.AsSpan(4 * 1024));
    return image;
  }

  private static void Write24(Span<byte> dest, uint val) {
    dest[0] = (byte)(val & 0xFF);
    dest[1] = (byte)((val >> 8) & 0xFF);
    dest[2] = (byte)((val >> 16) & 0xFF);
  }

  // ── Stage 1 — R/O reader ────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new SysVFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("SysV"));
    Assert.That(d.Extensions, Does.Contain(".s5"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(1528));
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>(),
      "WORM promotion: SysV descriptor must opt in to IArchiveCreatable.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True,
      "WORM promotion: capability flag must advertise CanCreate.");
  }

  [Test, Category("HappyPath")]
  public void Reader_ReadsSyntheticImage() {
    var img = BuildMinimalSysV();
    using var ms = new MemoryStream(img);
    var r = new SysVReader(ms);
    Assert.That(r.Magic, Is.EqualTo(0xFD187E20u));
    Assert.That(r.BlockSize, Is.EqualTo(1024));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("readme"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(23));
    var data = r.Extract(r.Entries[0]);
    Assert.That(Encoding.ASCII.GetString(data), Is.EqualTo("Hello from System V!\n#1"));
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_ReturnsBoundedStream() {
    var img = BuildMinimalSysV();
    using var ms = new MemoryStream(img);
    var d = new SysVFormatDescriptor();
    using var s = d.OpenEntry(ms, "readme", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(s.Length, Is.EqualTo(23));
    var buf = new byte[64];
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(23));
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(0));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsCorruptedImage() {
    var img = BuildMinimalSysV();
    // Bit-flip in magic at file offset 1024+504 = 1528
    img[1528] ^= 0xFF;
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => new SysVReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var img = BuildMinimalSysV();
    using var ms = new MemoryStream(img);
    var d = new SysVFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("readme"));
    ms.Position = 0;
    var bytes = d.ExtractEntryToMemory(ms, "readme", null);
    Assert.That(Encoding.ASCII.GetString(bytes), Is.EqualTo("Hello from System V!\n#1"));
  }

  // ── Stage 2 — WORM writer round-trips ───────────────────────────────

  [Test, Category("HappyPath")]
  public void Writer_SingleFile_RoundTripsViaReader() {
    var content = "Hello from CWB SysV writer!\n"u8.ToArray();
    var bytes = SysVWriter.Build([("hello.txt", content)]);

    // Image must look like a real s5fs image: magic at +1528, type=2 at +1532.
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1528, 4)),
      Is.EqualTo(0xFD187E20u), "magic at +1528 must match s5fs");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1532, 4)),
      Is.EqualTo(2u), "s_type at +1532 must be 2 (1024-byte blocks)");

    using var ms = new MemoryStream(bytes);
    var r = new SysVReader(ms);
    Assert.That(r.Magic, Is.EqualTo(0xFD187E20u));
    Assert.That(r.BlockSize, Is.EqualTo(1024));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(content.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Writer_MultipleFiles_RoundTripPreservesNamesSizesAndBytes() {
    var files = new[] {
      ("alpha.txt", "first file"u8.ToArray()),
      ("beta.bin",  new byte[1024]),                  // exactly one block
      ("gamma.log", Encoding.ASCII.GetBytes(new string('z', 2500))),  // crosses 2 blocks
    };
    var bytes = SysVWriter.Build(files);

    using var ms = new MemoryStream(bytes);
    var r = new SysVReader(ms);
    Assert.That(r.Entries.Select(e => e.Name).ToArray(),
      Is.EquivalentTo(new[] { "alpha.txt", "beta.bin", "gamma.log" }));
    foreach (var (name, data) in files) {
      var entry = r.Entries.Single(e => e.Name == name);
      Assert.That(entry.Size, Is.EqualTo(data.Length), $"size mismatch for {name}");
      Assert.That(r.Extract(entry), Is.EqualTo(data), $"content mismatch for {name}");
    }
  }

  [Test, Category("HappyPath")]
  public void Writer_NestedDirectories_RoundTripPreservesPaths() {
    var files = new[] {
      ("etc/motd",          "Welcome to s5fs\n"u8.ToArray()),
      ("etc/hostname",      "cwb-sysv"u8.ToArray()),
      ("usr/bin/hello",     "#!/bin/sh\necho hi"u8.ToArray()),
    };
    var bytes = SysVWriter.Build(files);

    using var ms = new MemoryStream(bytes);
    var r = new SysVReader(ms);
    // Reader emits one entry per inode it visits (incl. nested dirs); only
    // the file leaves are useful here.
    var fileEntries = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(fileEntries.Select(e => e.Name).ToArray(),
      Is.EquivalentTo(new[] { "etc/motd", "etc/hostname", "usr/bin/hello" }));
    foreach (var (name, data) in files) {
      var entry = fileEntries.Single(e => e.Name == name);
      Assert.That(r.Extract(entry), Is.EqualTo(data), $"content mismatch for {name}");
    }
  }

  [Test, Category("HappyPath")]
  public void Writer_EmptyFile_RoundTripsWithZeroSize() {
    var bytes = SysVWriter.Build([("empty", Array.Empty<byte>())]);
    using var ms = new MemoryStream(bytes);
    var r = new SysVReader(ms);
    var e = r.Entries.Single();
    Assert.That(e.Name, Is.EqualTo("empty"));
    Assert.That(e.Size, Is.EqualTo(0));
    Assert.That(r.Extract(e), Is.Empty);
  }

  [Test, Category("Boundary")]
  public void Writer_TenKilobyteFile_FillsAllDirectZones() {
    // 10 KB = exactly 10 direct zones, the writer's documented per-file ceiling.
    var data = new byte[10 * 1024];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
    var bytes = SysVWriter.Build([("big.bin", data)]);

    using var ms = new MemoryStream(bytes);
    var r = new SysVReader(ms);
    var entry = r.Entries.Single();
    Assert.That(entry.Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(entry), Is.EqualTo(data));
  }

  [Test, Category("Sad")]
  public void Writer_FileExceedingDirectZones_Throws() {
    // 10 KB + 1 byte triggers the > 10-direct-zone guard.
    var data = new byte[10 * 1024 + 1];
    Assert.Throws<InvalidOperationException>(
      () => SysVWriter.Build([("huge.bin", data)]));
  }

  [Test, Category("HappyPath")]
  public void Writer_SuperblockSpecFields_HaveExpectedOffsets() {
    // Spec field-offset audit (linux/fs/sysv/super.c). Every offset below
    // must hold for the writer's output to be readable by a real Linux
    // kernel's sysv driver.
    var bytes = SysVWriter.Build([("file", "x"u8.ToArray())]);
    var sb = bytes.AsSpan(1024, 1024);

    // s_isize at +0 — non-zero (we always allocate at least one ilist block).
    var isize = BinaryPrimitives.ReadUInt16LittleEndian(sb);
    Assert.That(isize, Is.GreaterThanOrEqualTo((ushort)1), "s_isize must be >= 1");

    // s_fsize at +2 — must equal the image size in 1024-byte blocks.
    var fsize = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(2));
    Assert.That((long)fsize * 1024, Is.EqualTo((long)bytes.Length), "s_fsize must match image length");

    // s_nfree at +6 — non-zero, since the writer reserves trailing free blocks.
    var nfree = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(6));
    Assert.That(nfree, Is.GreaterThan((ushort)0), "s_nfree must be > 0 on a fresh image");

    // s_ninode at +216 — non-zero (writer fills the inode cache).
    var ninode = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(216));
    Assert.That(ninode, Is.GreaterThan((ushort)0), "s_ninode must be > 0");

    // s_flock/s_ilock/s_fmod/s_ronly at +418..+421 — all zero on a clean fs.
    Assert.That(sb[418], Is.EqualTo((byte)0), "s_flock must be 0 on clean fs");
    Assert.That(sb[419], Is.EqualTo((byte)0), "s_ilock must be 0 on clean fs");
    Assert.That(sb[420], Is.EqualTo((byte)0), "s_fmod  must be 0 on clean fs");
    Assert.That(sb[421], Is.EqualTo((byte)0), "s_ronly must be 0 on clean fs");

    // s_tfree at +434 — total free blocks > 0.
    var tfree = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(434));
    Assert.That(tfree, Is.GreaterThan(0u), "s_tfree must be > 0");

    // s_tinode at +438 — total free inodes > 0 (the ilist is intentionally
    // over-allocated so the kernel has room to grow).
    var tinode = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(438));
    Assert.That(tinode, Is.GreaterThan((ushort)0), "s_tinode must be > 0");

    // s_magic at +504, s_type at +508.
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(504)),
      Is.EqualTo(0xFD187E20u), "s_magic must be 0xFD187E20");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(508)),
      Is.EqualTo(2u), "s_type must be 2 (1024-byte blocks)");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_RoundTripsThroughOwnReader() {
    var d = new SysVFormatDescriptor();
    var content = "via IArchiveCreatable"u8.ToArray();
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("note.txt", content),
    };
    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());
    var bytes = ms.ToArray();

    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1528, 4)),
      Is.EqualTo(0xFD187E20u), "Create() output must carry s5fs magic");

    using var read = new MemoryStream(bytes);
    var entries = d.List(read, null);
    Assert.That(entries.Single().Name, Is.EqualTo("note.txt"));
    read.Position = 0;
    Assert.That(d.ExtractEntryToMemory(read, "note.txt", null), Is.EqualTo(content));
  }

  // ── Stage 2 — External validation gate (WSL mount) ──────────────────

  [Test, Category("ExternalFsInterop")]
  public void Writer_OurImage_MountableByLinuxSysvDriver() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed. The Linux kernel's sysv driver is the only " +
                    "real validator for s5fs; install WSL via `wsl --install` to enable this gate.");

    // The default WSL2 kernel ships without the sysv driver (it's a niche
    // legacy FS). modprobe -n -v gives us a dry-run answer without touching
    // the kernel.
    var probe = FsInteropToolbox.RunWsl("modprobe -n -v sysv 2>&1 || true");
    var sysvAvailable = probe.ExitCode == 0 && !probe.StdOut.Contains("not found", StringComparison.OrdinalIgnoreCase);
    if (!sysvAvailable)
      Assert.Ignore("WSL kernel doesn't expose the sysv driver. " +
                    "Stock WSL2 kernels (6.x) build sysv as a module but don't ship it; " +
                    "rebuild the WSL kernel with CONFIG_SYSV_FS=y (or =m and load it) to enable this gate.");

    if (!FsInteropToolbox.WslHasPasswordlessSudo)
      Assert.Ignore("mount requires sudo. Configure passwordless sudo in WSL " +
                    "(`echo \"$USER ALL=(ALL) NOPASSWD: ALL\" | sudo tee /etc/sudoers.d/cwb`) " +
                    "to enable this gate.");

    var content = "Hello from the sysv kernel driver!\n"u8.ToArray();
    var bytes = SysVWriter.Build([("hello.txt", content)]);
    var imgPath = Path.Combine(Path.GetTempPath(), $"cwb_sysv_{Guid.NewGuid():N}.img");
    File.WriteAllBytes(imgPath, bytes);
    try {
      var wslImg = FsInteropToolbox.WinToWsl(imgPath);
      // One bash script: make a mountpoint, try the mount, list the root,
      // copy the file out, unmount. Any failure surfaces in the test output.
      var script =
        "set -e; " +
        "MNT=$(mktemp -d); " +
        $"sudo mount -o loop,ro -t sysv {wslImg} \"$MNT\"; " +
        "ls -la \"$MNT\"; " +
        "cat \"$MNT/hello.txt\"; " +
        "sudo umount \"$MNT\"; " +
        "rmdir \"$MNT\"";
      var result = FsInteropToolbox.RunWsl(script);
      Assert.That(result.ExitCode, Is.EqualTo(0),
        $"sysv kernel driver rejected our image:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
      Assert.That(result.StdOut, Does.Contain("hello.txt"),
        "Mounted filesystem must list hello.txt");
      Assert.That(result.StdOut, Does.Contain("Hello from the sysv kernel driver"),
        "cat hello.txt must round-trip the original content");
    } finally {
      try { File.Delete(imgPath); } catch { /* best effort */ }
    }
  }
}
