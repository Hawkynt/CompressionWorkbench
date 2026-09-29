using Compression.Lib;
using Compression.NativeUI.Views;
using Compression.Registry;
using Hawkynt.NativeForms;

namespace Compression.NativeUI;

/// <summary>
/// Builds the deterministic fixtures the documentation screenshots are taken of, and opens the
/// window each one belongs to.
/// <para>
/// The WPF shell rendered its own visual tree to a PNG. NativeForms has no equivalent — nothing in
/// it draws a window to a bitmap — so the process now only populates and displays the window, and
/// the capture itself is taken from outside by the CI job. Everything that made the old captures
/// reproducible is unchanged: fixed payloads, fixed timestamps, a fixed scramble seed, and an
/// analysis window that leaves its elapsed time out of the status line.
/// </para>
/// </summary>
internal static class ScreenshotMode {
  /// <summary>The windows the documentation captures, by the name the CI job passes.</summary>
  public static readonly string[] Targets = ["archive-browser", "analysis", "maintenance"];

  /// <summary>
  /// Builds the fixture for <paramref name="target"/> and returns the window to display. The caller
  /// runs the message loop; an external capture tool takes the picture.
  /// </summary>
  public static Form CreateWindow(string target, string fixtureRoot) {
    Directory.CreateDirectory(fixtureRoot);

    switch (target) {
      case "archive-browser": {
        var window = new MainForm([]);
        window.OpenArchive(CreateArchiveFixture(fixtureRoot));
        return window;
      }

      case "analysis": {
        var archivePath = CreateArchiveFixture(fixtureRoot);
        var (name, data) = CreateAnalysisFixture(fixtureRoot, archivePath);
        // The status line would otherwise end in a millisecond count, which differs on every run
        // and would make the committed picture differ on every run with it.
        var window = new AnalysisWindow { OmitElapsedTime = true };
        window.RunAnalysis(name, data);
        return window;
      }

      case "maintenance":
        return new DefragmentWindow(CreateFilesystemFixture(fixtureRoot));

      default:
        throw new ArgumentOutOfRangeException(nameof(target), target,
          $"Unknown screenshot target. Expected one of: {string.Join(", ", Targets)}");
    }
  }

  private static string CreateArchiveFixture(string fixtureRoot) {
    var inputRoot = Path.Combine(fixtureRoot, "CompressionWorkbench-demo");
    var docs = Path.Combine(inputRoot, "docs");
    var source = Path.Combine(inputRoot, "src");
    Directory.CreateDirectory(docs);
    Directory.CreateDirectory(source);

    var readme = Path.Combine(inputRoot, "README.txt");
    var vision = Path.Combine(docs, "vision.txt");
    var codec = Path.Combine(source, "Codec.cs");
    var payloadPath = Path.Combine(inputRoot, "payload.bin");

    File.WriteAllText(readme,
      "CompressionWorkbench demo archive\n\nEvery payload is generated locally by screenshot CI.\n");
    File.WriteAllText(vision,
      "One tool for codecs, archives, pseudo-archives, filesystems, analysis and maintenance.\n");
    File.WriteAllText(codec,
      "namespace Demo;\n\ninternal static class Codec { public const string Method = \"Deflate\"; }\n");

    var payload = new byte[4096];
    for (var i = 0; i < payload.Length; i++)
      payload[i] = (byte)((i * 37 + i / 7) & 0xff);
    File.WriteAllBytes(payloadPath, payload);

    // Archive listings include modification timestamps. Pin them so repeated CI
    // captures are byte-stable when the UI itself has not changed.
    var timestamp = new DateTime(2024, 1, 2, 12, 34, 56, DateTimeKind.Utc);
    foreach (var path in new[] { readme, vision, codec, payloadPath })
      File.SetLastWriteTimeUtc(path, timestamp);

    var archivePath = Path.Combine(fixtureRoot, "CompressionWorkbench-demo.zip");
    // Resolve the staging directory's children, not the directory: archiving the directory itself
    // put everything one level down, so the browser opened on a single folder row and the listing
    // it is meant to demonstrate was out of sight.
    var inputs = ArchiveInput.Resolve(Directory.GetFileSystemEntries(inputRoot));
    ArchiveOperations.Create(archivePath, inputs, new CompressionOptions());
    return archivePath;
  }

  /// <summary>
  /// A slice of a recovered volume: unallocated zeros, an imaging log in plain text, then a JPEG, a
  /// PNG and the demo archive itself embedded in high-entropy filler. This is the shape of file the
  /// analysis window exists for, so the scan grid, the entropy map, the statistics and the strings
  /// tab all have something real in them, and the entropy trace has both extremes rather than one
  /// flat band.
  /// </summary>
  private static (string Name, byte[] Data) CreateAnalysisFixture(string fixtureRoot, string archivePath) {
    using var image = new MemoryStream();

    WriteZeros(image, 3072);
    image.Write(System.Text.Encoding.ASCII.GetBytes(
      "MOUNT /dev/sdb1 type vfat (ro,noatime)\n" +
      "2024-01-02 12:34:56  imaging started, 32 KiB read\n" +
      "2024-01-02 12:35:07  sector 0x1A00 unreadable, zero-filled\n" +
      "2024-01-02 12:35:41  imaging finished, 3 payloads carved\n"));
    WriteZeros(image, 512);

    image.Write(BuildMinimalJpeg());
    WriteNoise(image, 6144, 0x5A);
    image.Write(BuildMinimalPng());
    WriteNoise(image, 4096, 0xC3);
    image.Write(File.ReadAllBytes(archivePath));
    WriteNoise(image, 2048, 0x17);
    WriteZeros(image, 1024);

    return ("recovered-volume.img", image.ToArray());
  }

  /// <summary>
  /// A FAT volume holding a plausible mix of files across two directories. The block map is the
  /// point of the maintenance window, so the volume is left about a third full on a fixed floppy
  /// geometry: occupied regions, a directory block and a free tail, rather than one flat colour.
  ///
  /// It is then fragmented on purpose. Add and Remove both lay the volume out again from scratch --
  /// removing 421 KB of files leaves 1.4 KB of gaps, not 421 -- so for a long time nothing here
  /// could produce split extents and the map could only ever read "no fragmentation detected",
  /// which is not what the window is for. The scramble verb deals every cluster a fresh slot from a
  /// fixed seed, so the map shows the interleaving Defragment exists to undo, and shows the same
  /// one on every run.
  /// </summary>
  private static string CreateFilesystemFixture(string fixtureRoot) {
    var stage = Path.Combine(fixtureRoot, "volume");
    Directory.CreateDirectory(Path.Combine(stage, "REPORTS"));
    Directory.CreateDirectory(Path.Combine(stage, "CAPTURE"));

    WriteFiller(Path.Combine(stage, "README.TXT"), 2_100, 0x11);
    WriteFiller(Path.Combine(stage, "CATALOG.DB"), 96_000, 0x22);
    WriteFiller(Path.Combine(stage, "REPORTS", "Q1.CSV"), 41_000, 0x33);
    WriteFiller(Path.Combine(stage, "REPORTS", "Q2.CSV"), 47_500, 0x44);
    WriteFiller(Path.Combine(stage, "CAPTURE", "FRAME001.RAW"), 120_000, 0x55);
    WriteFiller(Path.Combine(stage, "CAPTURE", "FRAME002.RAW"), 118_000, 0x66);
    WriteFiller(Path.Combine(stage, "CHANGELOG.LOG"), 8_800, 0x77);

    // The staged directory's own name must not become a folder on the volume, so its children are
    // resolved rather than the directory itself. A fixed floppy geometry leaves the volume about a
    // third full, which is what makes the map show occupied and free regions instead of one colour.
    var imagePath = Path.Combine(fixtureRoot, "capture-volume.img");
    var format = FormatDetector.DetectByExtensionForCreate(imagePath);
    ArchiveOperations.Create(imagePath, ArchiveInput.Resolve(Directory.GetFileSystemEntries(stage)),
      new CompressionOptions(), format,
      new Dictionary<string, string> {
        ["ImageSize"] = "1.44 MB (3.5\" HD)",
        ["VolumeLabel"] = "CAPTURE",
      });

    // The seed is what makes the capture reproducible: the same one deals the same layout on every
    // machine, so the picture only changes when the fixture does.
    var descriptor = FormatRegistry.GetArchiveOps(format.ToString());
    if (descriptor is IFilesystemScrambleable scrambler) {
      using var image = File.Open(imagePath, FileMode.Open, FileAccess.ReadWrite);
      scrambler.Scramble(image, new ScrambleOptions { Seed = 0x5CA27B1E });
    }

    return imagePath;
  }

  private static void WriteZeros(Stream target, int count) => target.Write(new byte[count]);

  /// <summary>Deterministic high-entropy filler; a fixed seed keeps captures byte-stable.</summary>
  private static void WriteNoise(Stream target, int count, int seed) {
    var state = (uint)(seed * 2654435761u + 1);
    var buffer = new byte[count];
    for (var i = 0; i < count; ++i) {
      state ^= state << 13;
      state ^= state >> 17;
      state ^= state << 5;
      buffer[i] = (byte)state;
    }
    target.Write(buffer);
  }

  /// <summary>
  /// Text-like file content: compressible, but not a single repeated byte. The timestamp is pinned
  /// for the same reason the archive fixture pins its own -- the Files panel renders it, so leaving
  /// it at the capture time would change the image on every run.
  /// </summary>
  private static void WriteFiller(string path, int length, int seed) {
    var buffer = new byte[length];
    for (var i = 0; i < length; ++i)
      buffer[i] = (byte)(0x20 + ((i * 7 + seed) % 0x5F));
    File.WriteAllBytes(path, buffer);
    File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 2, 12, 34, 56, DateTimeKind.Utc));
  }

  private static byte[] BuildMinimalJpeg() {
    using var jpeg = new MemoryStream();
    jpeg.Write([0xFF, 0xD8]);                                            // SOI
    jpeg.Write([0xFF, 0xE0, 0x00, 0x10]);                                // APP0, length 16
    jpeg.Write("JFIF\0"u8);
    jpeg.Write([0x01, 0x02, 0x00, 0x00, 0x48, 0x00, 0x48, 0x00, 0x00]);
    for (var i = 0; i < 512; ++i) jpeg.WriteByte((byte)(i & 0xFE));      // never 0xFF: no stray marker
    jpeg.Write([0xFF, 0xD9]);                                            // EOI
    return jpeg.ToArray();
  }

  private static byte[] BuildMinimalPng() {
    using var png = new MemoryStream();
    png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    WritePngChunk(png, "IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 0, 0, 0, 0]);  // 1x1 greyscale
    WritePngChunk(png, "IDAT", [0x78, 0x9C, 0x62, 0x00, 0x00, 0x00, 0x00, 0x01]);
    WritePngChunk(png, "IEND", []);
    return png.ToArray();
  }

  private static void WritePngChunk(Stream target, string type, ReadOnlySpan<byte> data) {
    Span<byte> length = stackalloc byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
    target.Write(length);
    target.Write(System.Text.Encoding.ASCII.GetBytes(type));
    target.Write(data);
    target.Write([0, 0, 0, 0]);   // the carver never verifies the CRC
  }

}
