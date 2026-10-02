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

  /// <summary>
  /// The same contents under lowercase 8.3 names, as Linux writes them: a short entry with the
  /// lowercase flags set and no long name, which FAT's layout walker reports in capitals.
  /// </summary>
  public static readonly IReadOnlyDictionary<string, byte[]> LowercaseFatFiles
    = FatFiles.ToDictionary(f => f.Key.ToLowerInvariant(), f => f.Value);

  /// <summary>A 1.44 MB FAT12 floppy whose files are scattered on purpose, so defragmenting moves something.</summary>
  public static string FragmentedFat(string folder, string name = "volume.img", IReadOnlyDictionary<string, byte[]>? files = null) {
    var writer = new FileSystem.Fat.FatWriter();
    foreach (var (file, data) in files ?? FatFiles) writer.AddFile(file, data);
    var path = Path.Combine(folder, name);
    File.WriteAllBytes(path, writer.Build());

    FormatRegistration.EnsureInitialized();
    using var image = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
    ((IFilesystemScrambleable)FormatRegistry.GetArchiveOps("Fat")!).Scramble(image, new ScrambleOptions { Seed = 0x5EED });
    return path;
  }

  /// <summary>Files written to the root out of name order, long names and 8.3 names mixed.</summary>
  public static readonly IReadOnlyDictionary<string, byte[]> UnsortedFatFiles = new Dictionary<string, byte[]> {
    ["Zulu long file name.txt"] = Pattern(6_000, 0x61),
    ["alpha.txt"] = Pattern(1_200, 0x62),
    ["Mike.bin"] = Pattern(9_000, 0x63),
    ["BETA.TXT"] = Pattern(700, 0x64),
  };

  /// <summary>A FAT12 floppy whose root directory is out of name order.</summary>
  public static string UnsortedFat(string folder, string name = "unsorted.img") {
    var writer = new FileSystem.Fat.FatWriter();
    foreach (var (file, data) in UnsortedFatFiles) writer.AddFile(file, data);
    var path = Path.Combine(folder, name);
    File.WriteAllBytes(path, writer.Build());
    return path;
  }

  /// <summary>The root directory's file names in the order the directory holds them.</summary>
  public static List<string> RootOrder(string path) {
    using var stream = File.OpenRead(path);
    var reader = new FileSystem.Fat.FatReader(stream, leaveOpen: true);
    return [.. reader.Entries.Where(e => !e.IsDirectory && !e.Name.Contains('/')).Select(e => e.Name)];
  }

  public static List<string> NameOrder(IEnumerable<string> names)
    => [.. names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ThenBy(n => n, StringComparer.Ordinal)];

  /// <summary>A FAT floppy holding plain data files named <c>*.bin</c> — a name BIN/CUE claims, a content nothing does.</summary>
  public static string FatWithPlainBinFiles(string folder, string name = "disk.img") {
    var writer = new FileSystem.Fat.FatWriter();
    for (var i = 1; i <= 20; ++i) writer.AddFile($"f{i}.bin", Pattern(3_000 + i * 97, i));
    var path = Path.Combine(folder, name);
    File.WriteAllBytes(path, writer.Build());
    return path;
  }

  /// <summary>
  /// A ZIP holding a real FAT image (<c>inner.img</c>), a file that only looks like one by its name
  /// (<c>fake.img</c>, plain bytes) and a text file.
  /// </summary>
  public static string ZipWithNestedImage(string folder, string name = "nested.zip") {
    var inner = new FileSystem.Fat.FatWriter();
    inner.AddFile("INNER.TXT", Pattern(5_000, 0x71));
    var path = Path.Combine(folder, name);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    using (var stream = archive.CreateEntry("inner.img", CompressionLevel.NoCompression).Open()) stream.Write(inner.Build());
    using (var stream = archive.CreateEntry("fake.img").Open()) stream.Write(Pattern(40_000, 0x72));
    using (var writer = new StreamWriter(archive.CreateEntry("notes.txt").Open())) writer.Write("notes");
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
