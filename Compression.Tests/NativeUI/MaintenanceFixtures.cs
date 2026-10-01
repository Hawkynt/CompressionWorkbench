using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Compression.Core.DiskImage;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.NativeUI;

/// <summary>Real images for the Defragment tab tests: a fragmented FAT floppy, a ZIP and a partitioned disk.</summary>
internal static class MaintenanceFixtures {
  /// <summary>The files on the FAT fixture, by name, so a test can prove each survived byte for byte.</summary>
  public static readonly IReadOnlyDictionary<string, byte[]> FatFiles = new Dictionary<string, byte[]> {
    ["ALPHA.BIN"] = Pattern(23_000, 0x11),
    ["BRAVO.BIN"] = Pattern(41_500, 0x22),
    ["CHARLIE.TXT"] = Pattern(9_100, 0x33),
    ["DELTA.DAT"] = Pattern(57_000, 0x44),
    ["ECHO.LOG"] = Pattern(3_300, 0x55),
  };

  /// <summary>A 1.44 MB FAT12 floppy whose files are scattered on purpose, so defragmenting moves something.</summary>
  public static string FragmentedFat(string folder, string name = "volume.img") {
    var writer = new FileSystem.Fat.FatWriter();
    foreach (var (file, data) in FatFiles) writer.AddFile(file, data);
    var path = Path.Combine(folder, name);
    File.WriteAllBytes(path, writer.Build());

    FormatRegistration.EnsureInitialized();
    using var image = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
    ((IFilesystemScrambleable)FormatRegistry.GetArchiveOps("Fat")!).Scramble(image, new ScrambleOptions { Seed = 0x5EED });
    return path;
  }

  public static string Zip(string folder, string name = "bundle.zip") {
    var path = Path.Combine(folder, name);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    using (var writer = new StreamWriter(archive.CreateEntry("readme.txt").Open())) writer.Write(new string('z', 4000));
    using (var writer = new StreamWriter(archive.CreateEntry("docs/notes.txt").Open())) writer.Write("notes");
    return path;
  }

  /// <summary>A whole-disk image: an MBR with one FAT partition starting at 1 MiB.</summary>
  public static string PartitionedDisk(string folder, string name = "disk.img") {
    const long mib = 1024 * 1024;
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("HELLO.TXT", "hello"u8.ToArray());
    var fat = writer.Build();
    var length = (fat.Length + 511) / 512 * 512;
    var disk = new byte[mib + length + mib];
    using (var stream = new MemoryStream(disk, writable: true))
      new PartitionEditor(stream).AddPartition(mib, length, PartitionType.Fat16Lba, "data");
    fat.CopyTo(disk, (int)mib);

    var path = Path.Combine(folder, name);
    File.WriteAllBytes(path, disk);
    return path;
  }

  /// <summary>Every file on the FAT image, read back through the descriptor.</summary>
  public static Dictionary<string, byte[]> ReadFatFiles(string path) {
    var ops = FormatRegistry.GetArchiveOps("Fat")!;
    using var stream = File.OpenRead(path);
    var names = ops.List(stream, password: null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    return names.ToDictionary(n => n, n => ArchiveOperations.ExtractEntry(path, n, password: null));
  }

  public static string Scratch() {
    var root = Path.Combine(Path.GetTempPath(), "cwb-defrag-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    return root;
  }

  private static byte[] Pattern(int length, int seed) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 31 + seed + i / 251) & 0xFF);
    return data;
  }
}
