using Compression.Sfx;

// The console self-extracting stub. It is prepended to every archive built with it, so it does one
// job and carries nothing it does not need for that job.

var destination = args.Length > 0
  ? Path.GetFullPath(args[0])
  : Environment.CurrentDirectory;

FileStream container;
Stream payload;
try {
  if (!SfxPayload.TryOpen(out container, out payload)) {
    Console.Error.WriteLine("No embedded archive found.");
    return 1;
  }
} catch (Exception ex) {
  Console.Error.WriteLine($"Cannot read this archive: {ex.Message}");
  return 1;
}

using (container)
using (payload) {
  var operations = SfxFormatResolver.Resolve(payload);
  if (operations is null) {
    Console.Error.WriteLine("Cannot identify the embedded archive format.");
    return 1;
  }

  Console.WriteLine($"Self-extracting archive ({SfxFormatResolver.FormatName})");
  Console.WriteLine($"Extracting to: {destination}");

  try {
    Directory.CreateDirectory(destination);
    operations.Extract(payload, destination, password: null, files: null);
  } catch (Exception ex) {
    Console.Error.WriteLine($"Extraction failed: {ex.Message}");
    return 1;
  }
}

Console.WriteLine("Extraction complete.");
return 0;
