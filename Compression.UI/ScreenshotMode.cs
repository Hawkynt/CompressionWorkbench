using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Compression.Lib;

namespace Compression.UI;

/// <summary>
/// Deterministic, interaction-free screenshot generation for documentation CI.
/// The application renders its own WPF visual tree instead of relying on desktop
/// automation, so captures do not depend on focus, monitor geometry, or runner timing.
/// </summary>
internal static class ScreenshotMode {
  private const double CaptureWidth = 1200;
  private const double CaptureHeight = 760;

  public static bool TryRun(Application application, string[] args) {
    if (args.Length == 0 || !string.Equals(args[0], "--screenshots", StringComparison.OrdinalIgnoreCase))
      return false;

    application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    var outputDirectory = Path.GetFullPath(
      args.Length > 1 ? args[1] : Path.Combine("docs", "screenshots"));

    try {
      Directory.CreateDirectory(outputDirectory);
      Console.WriteLine($"Screenshot output: {outputDirectory}");

      var fixtureRoot = Path.Combine(Path.GetTempPath(), "CompressionWorkbench-Screenshots");
      RecreateDirectory(fixtureRoot);

      try {
        Console.WriteLine("Creating deterministic archive fixture...");
        var archivePath = CreateArchiveFixture(fixtureRoot);

        Console.WriteLine("Capturing archive browser...");
        var mainWindow = new MainWindow {
          Width = CaptureWidth,
          Height = CaptureHeight,
        };
        mainWindow.OpenArchive(archivePath);
        Capture(mainWindow, Path.Combine(outputDirectory, "archive-browser.png"));

        Console.WriteLine("Capturing analysis window...");
        Capture(new Views.AnalysisWindow {
          Width = CaptureWidth,
          Height = CaptureHeight,
        }, Path.Combine(outputDirectory, "analysis.png"));

        Console.WriteLine("Capturing maintenance window...");
        Capture(new Views.DefragmentWindow {
          Width = CaptureWidth,
          Height = CaptureHeight,
        }, Path.Combine(outputDirectory, "maintenance.png"));
      }
      finally {
        try { Directory.Delete(fixtureRoot, recursive: true); }
        catch { /* CI fixture cleanup is best-effort. */ }
      }

      Console.WriteLine("Screenshot generation completed successfully.");
      application.Shutdown(0);
    }
    catch (Exception ex) {
      var diagnostic = $"Screenshot generation failed:{Environment.NewLine}{ex}";
      Console.Error.WriteLine(diagnostic);
      System.Diagnostics.Trace.WriteLine(diagnostic);
      try {
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "screenshot-error.txt"), diagnostic);
      }
      catch { /* The original exception is the useful failure. */ }
      application.Shutdown(1);
    }

    return true;
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
    var inputs = ArchiveInput.Resolve([inputRoot]);
    ArchiveOperations.Create(archivePath, inputs, new CompressionOptions());
    return archivePath;
  }

  private static void Capture(Window window, string outputPath) {
    window.WindowStartupLocation = WindowStartupLocation.Manual;
    window.Left = -10000;
    window.Top = -10000;
    window.ShowInTaskbar = false;
    window.ShowActivated = false;
    window.Show();

    DrainDispatcher(window.Dispatcher);
    window.UpdateLayout();

    var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
    var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
    Console.WriteLine($"Rendering {Path.GetFileName(outputPath)} at {width}x{height}...");
    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(window);

    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using (var output = File.Create(outputPath))
      encoder.Save(output);

    window.Close();
    DrainDispatcher(window.Dispatcher);
  }

  private static void DrainDispatcher(Dispatcher dispatcher) {
    var frame = new DispatcherFrame();
    dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
    Dispatcher.PushFrame(frame);
  }

  private static void RecreateDirectory(string path) {
    if (Directory.Exists(path))
      Directory.Delete(path, recursive: true);
    Directory.CreateDirectory(path);
  }
}
