#pragma warning disable CS1591

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Compression.Analysis.ExternalTools;

/// <summary>
/// Defines a configurable external tool command template.
/// Placeholders in <see cref="Arguments"/>:
/// <list type="bullet">
///   <item><c>{input}</c> — path to the input file</item>
///   <item><c>{output}</c> — path to the output file or directory</item>
///   <item><c>{outputDir}</c> — directory for extracted files</item>
///   <item><c>{stdin}</c> — indicates data should be piped via stdin (remove from args)</item>
/// </list>
/// </summary>
public sealed class ToolTemplate {
  /// <summary>Unique name for this template (e.g. "my-7z-extract", "custom-decompress").</summary>
  public required string Name { get; set; }

  /// <summary>Executable name or full path (e.g. "7z", "/usr/bin/zstd", "C:\Tools\myutil.exe").</summary>
  public required string Executable { get; set; }

  /// <summary>
  /// Argument template with placeholders. Example: <c>x "{input}" -o"{outputDir}" -y</c>
  /// </summary>
  public required string Arguments { get; set; }

  /// <summary>What this template does: "identify", "list", "extract", "decompress", "compress".</summary>
  public string Action { get; set; } = "extract";

  /// <summary>File extensions this template handles (e.g. [".myformat", ".mf"]). Empty = any.</summary>
  public List<string> Extensions { get; set; } = [];

  /// <summary>Whether decompressed data comes via stdout (true) or written to <c>{output}</c>/<c>{outputDir}</c>.</summary>
  public bool CaptureStdout { get; set; }

  /// <summary>Whether to pipe input data via stdin instead of <c>{input}</c> file path.</summary>
  public bool PipeStdin { get; set; }

  /// <summary>Timeout in milliseconds. 0 = use default.</summary>
  public int TimeoutMs { get; set; }

  /// <summary>Expected exit code for success. Default: 0.</summary>
  public int SuccessExitCode { get; set; }

  /// <summary>Optional description for display in UI/CLI.</summary>
  public string Description { get; set; } = "";

  /// <summary>
  /// Expands the argument template with actual paths.
  /// </summary>
  public string ExpandArguments(string? inputPath = null, string? outputPath = null, string? outputDir = null) {
    var args = Arguments;
    if (inputPath != null) args = args.Replace("{input}", inputPath, StringComparison.OrdinalIgnoreCase);
    if (outputPath != null) args = args.Replace("{output}", outputPath, StringComparison.OrdinalIgnoreCase);
    if (outputDir != null) args = args.Replace("{outputDir}", outputDir, StringComparison.OrdinalIgnoreCase);
    args = args.Replace("{stdin}", "", StringComparison.OrdinalIgnoreCase).Trim();
    return args;
  }

  /// <summary>
  /// Runs this template against a file and returns the result.
  /// </summary>
  public async Task<ToolRunResult> RunAsync(
    string inputPath,
    string? outputPath = null,
    string? outputDir = null,
    byte[]? stdinData = null,
    int? timeoutOverride = null
  ) {
    var executable = Executable;

    // Resolve via PATH if not an absolute path.
    if (!Path.IsPathRooted(executable)) {
      var resolved = ToolDiscovery.GetToolPath(executable);
      if (resolved != null) executable = resolved;
    }

    var args = ExpandArguments(inputPath, outputPath, outputDir);
    var timeout = timeoutOverride ?? (TimeoutMs > 0 ? TimeoutMs : ExternalToolRunner.DefaultTimeoutMs);

    return await ExternalToolRunner.RunAsync(
      executable, args,
      stdinData: PipeStdin ? stdinData : null,
      timeoutMs: timeout,
      captureStdoutBytes: CaptureStdout
    ).ConfigureAwait(false);
  }
}

/// <summary>
/// Manages a collection of user-configurable tool templates.
/// Templates can be loaded from/saved to a JSON file for persistent configuration.
/// </summary>
public sealed class ToolTemplateRegistry {

  private readonly List<ToolTemplate> _templates = [];
  private static readonly JsonSerializerOptions _jsonOptions = new() {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
  };

  /// <summary>All registered templates.</summary>
  public IReadOnlyList<ToolTemplate> Templates => _templates;

  /// <summary>Registers a tool template.</summary>
  public void Register(ToolTemplate template) => _templates.Add(template);

  /// <summary>Removes a template by name.</summary>
  public bool Remove(string name) => _templates.RemoveAll(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;

  /// <summary>Finds templates matching a file extension and action.</summary>
  public IEnumerable<ToolTemplate> FindTemplates(string extension, string action = "extract")
    => _templates.Where(t =>
      t.Action.Equals(action, StringComparison.OrdinalIgnoreCase)
      && (t.Extensions.Count == 0 || t.Extensions.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase))));

  /// <summary>Finds a template by name.</summary>
  public ToolTemplate? GetByName(string name)
    => _templates.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

  /// <summary>
  /// Tries all matching templates for a file until one succeeds.
  /// </summary>
  public async Task<(ToolTemplate? Template, ToolRunResult? Result)> TryMatchingAsync(
    string filePath, string action = "extract", string? outputDir = null
  ) {
    var ext = Path.GetExtension(filePath).ToLowerInvariant();
    foreach (var template in FindTemplates(ext, action)) {
      var result = await template.RunAsync(filePath, outputDir: outputDir).ConfigureAwait(false);
      if (result.ExitCode == template.SuccessExitCode)
        return (template, result);
    }
    return (null, null);
  }

  /// <summary>Saves all templates to a JSON file.</summary>
  [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "ToolTemplate is preserved")]
  public async Task SaveAsync(string path) {
    var json = JsonSerializer.Serialize(_templates, _jsonOptions);
    await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
  }

  /// <summary>Loads templates from a JSON file, replacing current contents.</summary>
  [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "ToolTemplate is preserved")]
  public async Task LoadAsync(string path) {
    if (!File.Exists(path)) return;
    var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
    var loaded = JsonSerializer.Deserialize<List<ToolTemplate>>(json, _jsonOptions);
    if (loaded == null) return;
    _templates.Clear();
    _templates.AddRange(loaded);
  }

  /// <summary>Loads templates from a JSON file synchronously.</summary>
  public void Load(string path) => LoadAsync(path).GetAwaiter().GetResult();

  /// <summary>Saves templates to a JSON file synchronously.</summary>
  public void Save(string path) => SaveAsync(path).GetAwaiter().GetResult();

  /// <summary>
  /// Creates a registry pre-populated with sensible defaults for common tools.
  /// </summary>
  public static ToolTemplateRegistry CreateDefaults() {
    var reg = new ToolTemplateRegistry();

    reg.Register(new() {
      Name = "7z-extract", Executable = "7z", Action = "extract",
      Arguments = "x \"{input}\" -o\"{outputDir}\" -y",
      Description = "Extract any archive with 7-Zip"
    });
    reg.Register(new() {
      Name = "7z-list", Executable = "7z", Action = "list",
      Arguments = "l -slt \"{input}\"",
      Description = "List archive contents with 7-Zip (technical format)"
    });
    reg.Register(new() {
      Name = "7z-identify", Executable = "7z", Action = "identify",
      Arguments = "l \"{input}\"",
      Description = "Identify archive format with 7-Zip"
    });
    reg.Register(new() {
      Name = "gzip-decompress", Executable = "gzip", Action = "decompress",
      Arguments = "-d -c \"{input}\"", CaptureStdout = true,
      Extensions = [".gz"], Description = "Decompress gzip via stdout"
    });
    reg.Register(new() {
      Name = "bzip2-decompress", Executable = "bzip2", Action = "decompress",
      Arguments = "-d -c \"{input}\"", CaptureStdout = true,
      Extensions = [".bz2"], Description = "Decompress bzip2 via stdout"
    });
    reg.Register(new() {
      Name = "xz-decompress", Executable = "xz", Action = "decompress",
      Arguments = "-d -c \"{input}\"", CaptureStdout = true,
      Extensions = [".xz"], Description = "Decompress xz via stdout"
    });
    reg.Register(new() {
      Name = "zstd-decompress", Executable = "zstd", Action = "decompress",
      Arguments = "-d -c \"{input}\"", CaptureStdout = true,
      Extensions = [".zst"], Description = "Decompress zstd via stdout"
    });
    reg.Register(new() {
      Name = "file-identify", Executable = "file", Action = "identify",
      Arguments = "--mime-type -b \"{input}\"",
      Description = "Identify file type via libmagic"
    });
    reg.Register(new() {
      Name = "binwalk-scan", Executable = "binwalk", Action = "identify",
      Arguments = "\"{input}\"",
      Description = "Scan for embedded signatures with binwalk"
    });
    reg.Register(new() {
      Name = "tar-extract", Executable = "tar", Action = "extract",
      Arguments = "xf \"{input}\" -C \"{outputDir}\"",
      Extensions = [".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tar.xz", ".txz"],
      Description = "Extract tar archives"
    });

    return reg;
  }
}
