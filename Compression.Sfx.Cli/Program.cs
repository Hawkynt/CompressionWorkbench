using Compression.Lib;

// SFX CLI stub: reads appended archive data from its own executable and extracts it.
// Layout: [stub.exe][archive data][8-byte archive offset (int64 LE)][4-byte magic "SFX!"]

var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path.");
var outputDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();

var info = SfxBuilder.ReadTrailer(exePath);
if (info == null) {
  Console.Error.WriteLine("This executable does not contain an embedded archive.");
  return 1;
}

Console.WriteLine($"Self-extracting archive ({info.Value.Format})");
Console.WriteLine($"Extracting to: {Path.GetFullPath(outputDir)}");

try {
  SfxBuilder.Extract(exePath, outputDir);
  Console.WriteLine("Extraction complete.");
  return 0;
}
catch (Exception ex) {
  Console.Error.WriteLine($"Extraction failed: {ex.Message}");
  return 1;
}
