#pragma warning disable CS1591

using Compression.Registry;

namespace Compression.Tests.Exotic;

/// <summary>
/// Generates minimal valid test files for exotic formats using our own writers.
/// Produces in-memory byte arrays so callers can write them to disk under any
/// extension. All corpus files are intentionally small (&lt; 200 KB) so they are
/// cheap to create and parse.
/// </summary>
public static class ExoticFormatCorpus {

  /// <summary>Describes the canonical test entries baked into every corpus file.</summary>
  public readonly record struct CorpusEntry(string Name, byte[] Data);

  /// <summary>Canonical 3-entry set used by most corpus tests. Names are short/uppercase
  /// so they survive the 8-char / 12-char / 16-char limits of retro formats.</summary>
  public static CorpusEntry[] DefaultEntries { get; } = [
    new("ALPHA",   "Alpha content"u8.ToArray()),
    new("BRAVO",   "Bravo content here"u8.ToArray()),
    new("CHARLIE", "Charlie third file payload"u8.ToArray()),
  ];

  // ── Public factories (one per exotic format we can write) ───────────────

  public static byte[] CreateD64(CorpusEntry[]? entries = null)
    => BuildViaDescriptor(new FileFormat.D64.D64FormatDescriptor(), entries ?? DefaultEntries);

  public static byte[] CreateT64(CorpusEntry[]? entries = null)
    => BuildViaDescriptor(new FileFormat.T64.T64FormatDescriptor(), entries ?? DefaultEntries);

  public static byte[] CreateAdf(CorpusEntry[]? entries = null)
    => BuildViaDescriptor(new FileFormat.Adf.AdfFormatDescriptor(), entries ?? DefaultEntries);

  public static byte[] CreateHfs(CorpusEntry[]? entries = null)
    => BuildViaDescriptor(new FileFormat.Hfs.HfsFormatDescriptor(), entries ?? DefaultEntries);

  public static byte[] CreateIso(CorpusEntry[]? entries = null)
    => BuildViaDescriptor(new FileFormat.Iso.IsoFormatDescriptor(),
      entries ?? ToIsoEntries(DefaultEntries));

  public static byte[] CreatePak(CorpusEntry[]? entries = null)
    => BuildViaDescriptor(new FileFormat.Pak.PakFormatDescriptor(), entries ?? DefaultEntries);

  public static byte[] CreateWad(CorpusEntry[]? entries = null)
    => BuildViaDescriptor(new FileFormat.Wad.WadFormatDescriptor(), entries ?? DefaultEntries);

  public static byte[] CreateWad2(CorpusEntry[]? entries = null)
    => BuildViaDescriptor(new FileFormat.Wad2.Wad2FormatDescriptor(), entries ?? DefaultEntries);

  public static byte[] CreateGrp(CorpusEntry[]? entries = null)
    => BuildViaDescriptor(new FileFormat.Grp.GrpFormatDescriptor(), entries ?? DefaultEntries);

  public static byte[] CreateHog(CorpusEntry[]? entries = null)
    => BuildViaDescriptor(new FileFormat.Hog.HogFormatDescriptor(), entries ?? DefaultEntries);

  public static byte[] CreateBig(CorpusEntry[]? entries = null)
    => BuildViaDescriptor(new FileFormat.Big.BigFormatDescriptor(), entries ?? DefaultEntries);

  public static byte[] CreateGodotPck(CorpusEntry[]? entries = null)
    => BuildViaDescriptor(new FileFormat.GodotPck.GodotPckFormatDescriptor(), entries ?? DefaultEntries);

  // ── Core builder ───────────────────────────────────────────────────────

  /// <summary>Writes entries to temp files, calls the descriptor's Create, and
  /// returns the produced archive bytes. Temp files are always cleaned up.</summary>
  public static byte[] BuildViaDescriptor(IArchiveFormatOperations ops, CorpusEntry[] entries) {
    var tempFiles = new List<string>(entries.Length);
    try {
      var inputs = new List<ArchiveInputInfo>(entries.Length);
      foreach (var e in entries) {
        var tmp = Path.Combine(Path.GetTempPath(), "cwb_corpus_" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(tmp, e.Data);
        tempFiles.Add(tmp);
        inputs.Add(new ArchiveInputInfo(tmp, e.Name, false));
      }

      using var ms = new MemoryStream();
      ops.Create(ms, inputs, new FormatCreateOptions());
      return ms.ToArray();
    }
    finally {
      foreach (var f in tempFiles)
        try { File.Delete(f); } catch { /* best effort */ }
    }
  }

  /// <summary>ISO requires file extensions and uppercase identifiers; convert default
  /// entries (<c>ALPHA</c>) into valid ISO identifiers (<c>ALPHA.TXT</c>).</summary>
  private static CorpusEntry[] ToIsoEntries(CorpusEntry[] src) {
    var result = new CorpusEntry[src.Length];
    for (var i = 0; i < src.Length; i++) {
      var name = src[i].Name;
      if (!name.Contains('.')) name += ".TXT";
      result[i] = new CorpusEntry(name.ToUpperInvariant(), src[i].Data);
    }
    return result;
  }
}
