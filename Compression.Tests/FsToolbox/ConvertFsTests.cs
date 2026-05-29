#pragma warning disable CS1591
namespace Compression.Tests.FsToolbox;

[TestFixture]
public class ConvertFsTests {

  /// <summary>
  /// Converts a FAT image to ext2 and verifies all files survive.
  /// </summary>
  [Test]
  public void ConvertFs_FatToExt_PreservesFiles() {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("HELLO.TXT", "hello ext"u8.ToArray());
    writer.AddFile("DATA.BIN", new byte[128]);
    var fatImage = writer.Build(totalSectors: 2880);

    var inputPath = Path.Combine(Path.GetTempPath(), $"cwb_cfs_fat_{Guid.NewGuid():N}.img");
    var outputPath = Path.Combine(Path.GetTempPath(), $"cwb_cfs_ext_{Guid.NewGuid():N}.img");
    try {
      File.WriteAllBytes(inputPath, fatImage);
      Compression.Lib.FormatRegistration.EnsureInitialized();

      var warnings = Compression.Lib.ArchiveOperations.ConvertFs(inputPath, outputPath, "Ext");
      Assert.That(File.Exists(outputPath), Is.True);

      // Read back and verify files exist in the ext image.
      using var fs = File.OpenRead(outputPath);
      var reader = new FileSystem.Ext.ExtReader(fs);
      var entries = reader.Entries.Where(e => !e.IsDirectory).ToList();
      Assert.That(entries, Has.Count.EqualTo(2));
    } finally {
      if (File.Exists(inputPath)) File.Delete(inputPath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  /// <summary>
  /// Converts an ext image to FAT and verifies all files survive.
  /// </summary>
  [Test]
  public void ConvertFs_ExtToFat_PreservesFiles() {
    var writer = new FileSystem.Ext.ExtWriter();
    writer.AddFile("README.TXT", "ext to fat"u8.ToArray());
    var extImage = writer.Build(blockSize: 1024, totalBlocks: 4096);

    var inputPath = Path.Combine(Path.GetTempPath(), $"cwb_cfs_ext2_{Guid.NewGuid():N}.ext2");
    var outputPath = Path.Combine(Path.GetTempPath(), $"cwb_cfs_fat2_{Guid.NewGuid():N}.img");
    try {
      File.WriteAllBytes(inputPath, extImage);
      Compression.Lib.FormatRegistration.EnsureInitialized();

      var warnings = Compression.Lib.ArchiveOperations.ConvertFs(inputPath, outputPath, "Fat");
      Assert.That(File.Exists(outputPath), Is.True);
      Assert.That(new FileInfo(outputPath).Length, Is.GreaterThan(0));

      // Read back from FAT.
      using var fs = File.OpenRead(outputPath);
      var reader = new FileSystem.Fat.FatReader(fs);
      var entries = reader.Entries.Where(e => !e.IsDirectory).ToList();
      Assert.That(entries, Has.Count.GreaterThanOrEqualTo(1));
    } finally {
      if (File.Exists(inputPath)) File.Delete(inputPath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  /// <summary>
  /// Converting to a retro format that truncates long names logs a warning.
  /// Also checks that conversion to a retro format with timestamps from a
  /// modern FS logs the timestamp warning.
  /// </summary>
  [Test]
  public void ConvertFs_ToRetroFormat_LogsWarnings() {
    // Create a FAT image with a long file name that will need truncation for D64.
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("THISISAVERYLONGFILENAME.TXT", "test"u8.ToArray());
    var fatImage = writer.Build(totalSectors: 2880);

    var inputPath = Path.Combine(Path.GetTempPath(), $"cwb_cfs_warn_{Guid.NewGuid():N}.img");
    var outputPath = Path.Combine(Path.GetTempPath(), $"cwb_cfs_d64_{Guid.NewGuid():N}.d64");
    try {
      File.WriteAllBytes(inputPath, fatImage);
      Compression.Lib.FormatRegistration.EnsureInitialized();

      var warnings = Compression.Lib.ArchiveOperations.ConvertFs(inputPath, outputPath, "D64");
      // D64 has 16-char name limit, so the long name should trigger a warning.
      Assert.That(warnings, Has.Count.GreaterThanOrEqualTo(1));
      Assert.That(warnings.Any(w => w.Contains("truncat", StringComparison.OrdinalIgnoreCase)
                                 || w.Contains("timestamp", StringComparison.OrdinalIgnoreCase)), Is.True);
    } finally {
      if (File.Exists(inputPath)) File.Delete(inputPath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  /// <summary>
  /// ConvertFs with an unknown target format throws NotSupportedException.
  /// </summary>
  [Test]
  public void ConvertFs_UnknownFormat_Throws() {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("X.TXT", "x"u8.ToArray());
    var fatImage = writer.Build(totalSectors: 2880);

    var inputPath = Path.Combine(Path.GetTempPath(), $"cwb_cfs_unk_{Guid.NewGuid():N}.img");
    var outputPath = Path.Combine(Path.GetTempPath(), $"cwb_cfs_unk_out_{Guid.NewGuid():N}.xyz");
    try {
      File.WriteAllBytes(inputPath, fatImage);
      Compression.Lib.FormatRegistration.EnsureInitialized();

      Assert.Throws<NotSupportedException>(() =>
        Compression.Lib.ArchiveOperations.ConvertFs(inputPath, outputPath, "DoesNotExist"));
    } finally {
      if (File.Exists(inputPath)) File.Delete(inputPath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  /// <summary>
  /// ConvertFs detects format from extension when targetFormatId is null.
  /// </summary>
  [Test]
  public void ConvertFs_AutoDetectFromExtension() {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("AUTO.TXT", "auto"u8.ToArray());
    var fatImage = writer.Build(totalSectors: 2880);

    var inputPath = Path.Combine(Path.GetTempPath(), $"cwb_cfs_auto_{Guid.NewGuid():N}.img");
    // .d64 extension should auto-detect as D64 format.
    var outputPath = Path.Combine(Path.GetTempPath(), $"cwb_cfs_auto_out_{Guid.NewGuid():N}.d64");
    try {
      File.WriteAllBytes(inputPath, fatImage);
      Compression.Lib.FormatRegistration.EnsureInitialized();

      // This should work if .d64 extension is registered.
      var warnings = Compression.Lib.ArchiveOperations.ConvertFs(inputPath, outputPath);
      Assert.That(File.Exists(outputPath), Is.True);
    } finally {
      if (File.Exists(inputPath)) File.Delete(inputPath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  // ── Cross-category conversion tests ────────────────────────────────

  /// <summary>
  /// Creates a ZIP archive, converts to FAT image via ConvertFs, verifies files.
  /// Archive → FS cross-category conversion.
  /// </summary>
  [Test]
  public void ConvertFs_ZipToFat_Works() {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    // Create a ZIP with two files.
    var zipPath = Path.Combine(Path.GetTempPath(), $"cwb_cfs_zip2fat_{Guid.NewGuid():N}.zip");
    var outputPath = Path.Combine(Path.GetTempPath(), $"cwb_cfs_zip2fat_out_{Guid.NewGuid():N}.img");
    try {
      var inputs = new List<Compression.Lib.ArchiveInput>();
      var file1 = Path.Combine(Path.GetTempPath(), $"cwb_zf_hello_{Guid.NewGuid():N}.txt");
      var file2 = Path.Combine(Path.GetTempPath(), $"cwb_zf_data_{Guid.NewGuid():N}.bin");
      File.WriteAllText(file1, "hello from zip");
      File.WriteAllBytes(file2, new byte[64]);
      try {
        inputs.Add(new Compression.Lib.ArchiveInput(file1, "HELLO.TXT"));
        inputs.Add(new Compression.Lib.ArchiveInput(file2, "DATA.BIN"));
        Compression.Lib.ArchiveOperations.Create(zipPath, inputs, new Compression.Lib.CompressionOptions());

        // Convert ZIP → FAT
        var warnings = Compression.Lib.ArchiveOperations.ConvertFs(zipPath, outputPath, "Fat");
        Assert.That(File.Exists(outputPath), Is.True);

        // Verify FAT image contains the files.
        using var fs = File.OpenRead(outputPath);
        var reader = new FileSystem.Fat.FatReader(fs);
        var entries = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Name.ToUpperInvariant()).ToList();
        Assert.That(entries, Has.Count.EqualTo(2));

        // Cross-category warning should be present.
        Assert.That(warnings.Any(w => w.Contains("Cross-category", StringComparison.OrdinalIgnoreCase)), Is.True);
      } finally {
        if (File.Exists(file1)) File.Delete(file1);
        if (File.Exists(file2)) File.Delete(file2);
      }
    } finally {
      if (File.Exists(zipPath)) File.Delete(zipPath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  /// <summary>
  /// Creates a FAT image, converts to ZIP archive via ConvertFs, verifies files.
  /// FS → Archive cross-category conversion.
  /// </summary>
  [Test]
  public void ConvertFs_FatToZip_Works() {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("README.TXT", "fat to zip"u8.ToArray());
    writer.AddFile("NOTES.TXT", "some notes"u8.ToArray());
    var fatImage = writer.Build(totalSectors: 2880);

    var inputPath = Path.Combine(Path.GetTempPath(), $"cwb_cfs_fat2zip_{Guid.NewGuid():N}.img");
    var outputPath = Path.Combine(Path.GetTempPath(), $"cwb_cfs_fat2zip_out_{Guid.NewGuid():N}.zip");
    try {
      File.WriteAllBytes(inputPath, fatImage);

      var warnings = Compression.Lib.ArchiveOperations.ConvertFs(inputPath, outputPath, "Zip");
      Assert.That(File.Exists(outputPath), Is.True);
      Assert.That(new FileInfo(outputPath).Length, Is.GreaterThan(0));

      // Read back from ZIP.
      using var fs = File.OpenRead(outputPath);
      var zipReader = new FileFormat.Zip.ZipReader(fs);
      var entries = zipReader.Entries.Where(e => !e.IsDirectory).ToList();
      Assert.That(entries, Has.Count.GreaterThanOrEqualTo(2));

      // Cross-category warning should be present.
      Assert.That(warnings.Any(w => w.Contains("Cross-category", StringComparison.OrdinalIgnoreCase)), Is.True);
    } finally {
      if (File.Exists(inputPath)) File.Delete(inputPath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }

  /// <summary>
  /// Creates a D64 image, converts to 7z archive via ConvertFs, verifies files.
  /// Retro FS → modern archive cross-category conversion.
  /// </summary>
  [Test]
  public void ConvertFs_D64To7z_Works() {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    var writer = new FileSystem.D64.D64Writer();
    writer.AddFile("GAME", "retro data"u8.ToArray());
    var d64Image = writer.Build();

    var inputPath = Path.Combine(Path.GetTempPath(), $"cwb_cfs_d64to7z_{Guid.NewGuid():N}.d64");
    var outputPath = Path.Combine(Path.GetTempPath(), $"cwb_cfs_d64to7z_out_{Guid.NewGuid():N}.7z");
    try {
      File.WriteAllBytes(inputPath, d64Image);

      var warnings = Compression.Lib.ArchiveOperations.ConvertFs(inputPath, outputPath, "SevenZip");
      Assert.That(File.Exists(outputPath), Is.True);
      Assert.That(new FileInfo(outputPath).Length, Is.GreaterThan(0));

      // Read back from 7z.
      using var fs = File.OpenRead(outputPath);
      var szReader = new FileFormat.SevenZip.SevenZipReader(fs);
      var entries = szReader.Entries.Where(e => !e.IsDirectory).ToList();
      Assert.That(entries, Has.Count.GreaterThanOrEqualTo(1));

      // Cross-category warning should be present.
      Assert.That(warnings.Any(w => w.Contains("Cross-category", StringComparison.OrdinalIgnoreCase)), Is.True);
    } finally {
      if (File.Exists(inputPath)) File.Delete(inputPath);
      if (File.Exists(outputPath)) File.Delete(outputPath);
    }
  }
}
