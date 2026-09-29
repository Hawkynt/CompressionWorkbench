namespace Compression.NativeUI;

/// <summary>What the shell was asked to do on the command line.</summary>
internal enum StartupCommand {
  /// <summary>No recognised verb: open the browser, or the file named by the first argument.</summary>
  Browse,

  /// <summary>Capture one documentation screenshot and exit.</summary>
  Screenshot,

  /// <summary>Open the analysis window, on a file when one was given.</summary>
  Analyze,

  /// <summary>Create a ZIP beside the given file or folder.</summary>
  CreateZip,

  /// <summary>Create a 7z beside the given file or folder.</summary>
  Create7z,

  /// <summary>Extract an archive, asking where to put it.</summary>
  Extract,

  /// <summary>Extract an archive into its own folder without asking.</summary>
  ExtractHere,
}

/// <summary>
/// The command-line verbs the shell answers to, in one place.
/// <para>
/// These are also the Explorer context-menu verbs: <see cref="Views.FileAssociationsWindow"/> writes
/// them into the registry and <c>Program</c> dispatches on them. They were two separate lists once,
/// and the pair drifted — "Extract here" was registered for years against a verb nothing handled,
/// so the menu entry silently did nothing. One list, and a test that every entry in it resolves.
/// </para>
/// </summary>
internal static class ShellVerbs {
  public const string Analyze = "--analyze";
  public const string AnalyzeSlash = "/analyze";
  public const string AnalyzeShort = "-a";
  public const string CreateZip = "--create-zip";
  public const string Create7z = "--create-7z";
  public const string Extract = "--extract";
  public const string ExtractHere = "--extract-here";
  public const string ScreenshotPrefix = "--screenshot=";

  /// <summary>Every verb registered as an Explorer command, so a test can check each one resolves.</summary>
  public static readonly string[] ExplorerVerbs = [ExtractHere, CreateZip, Create7z];

  /// <summary>Identifies the verb in <paramref name="args"/>, without acting on it.</summary>
  public static StartupCommand Parse(string[] args) {
    if (args.Any(a => a.StartsWith(ScreenshotPrefix, StringComparison.OrdinalIgnoreCase)))
      return StartupCommand.Screenshot;

    return args switch {
      [Analyze or AnalyzeSlash or AnalyzeShort, ..] => StartupCommand.Analyze,
      [CreateZip, _, ..] => StartupCommand.CreateZip,
      [Create7z, _, ..] => StartupCommand.Create7z,
      [Extract, _, ..] => StartupCommand.Extract,
      [ExtractHere, _, ..] => StartupCommand.ExtractHere,
      _ => StartupCommand.Browse,
    };
  }
}
