#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using CompressionWorkbench.FileFormat.Ico;
using static Compression.Registry.FormatHelpers;

namespace CompressionWorkbench.FileFormat.Ani;

/// <summary>
/// Pseudo-archive descriptor for Windows animated cursor (<c>.ani</c>) files.
/// Each ANI frame is a complete CUR file; the descriptor unpacks each frame and
/// then further unpacks each CUR's sub-images using the CWB ICO/CUR reader, so
/// every extracted sub-image keeps its native on-disk encoding (PNG or DIB) and
/// is named with a matching <c>.png</c> / <c>.bmp</c> extension.
/// </summary>
/// <remarks>
/// Output naming: <c>frame_NNN/cursor_MM_WxH_hX_hY.{png|bmp}</c>. A
/// <c>metadata.ini</c> records the animation header (frame/step counts, default
/// jiffies, optional INAM/IART metadata) plus the <c>rate</c> and <c>seq </c>
/// chunks when present.
/// </remarks>
public sealed class AniFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {

  public string Id => "Ani";
  public string DisplayName => "ANI (animated cursor)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ani";
  public IReadOnlyList<string> Extensions => [".ani"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // RIFF header — confidence is moderate because RIFF is shared with WAV/AVI/AVX/etc.
    // The form-type "ACON" at offset 8 disambiguates; the reader rejects on mismatch.
    new("RIFF"u8.ToArray(), Confidence: 0.40),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Windows animated cursor — each frame is a CUR file; every CUR sub-image is " +
    "unpacked preserving its on-disk encoding (PNG or DIB), with the matching " +
    "file extension on the extracted entry.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
        e.Method, false, false, null))
      .ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// WORM creation: emits a RIFF "ACON" container whose <c>LIST "fram"</c>
  /// holds one <c>icon</c> chunk per input. Each input must already be a valid
  /// CUR (or ICO) file — the writer copies its bytes verbatim and does not
  /// synthesise cursor images from arbitrary input data. The first 4 bytes of
  /// every input are checked for the ICO/CUR magic (00 00 01 00 or 00 00 02 00);
  /// inputs that don't match are rejected so the writer never produces
  /// structurally invalid frames.
  /// </summary>
  /// <remarks>
  /// Per-format options (<see cref="FormatCreateOptions.FormatSpecific"/>):
  /// <list type="bullet">
  ///   <item><c>title</c> / <c>artist</c> — populate optional LIST INFO chunks.</item>
  ///   <item><c>default_jiffies</c> — frame duration in 1/60-second units (default 6 = 100 ms).</item>
  /// </list>
  /// </remarks>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var frames = new List<byte[]>();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var leaf = Path.GetFileName(i.ArchiveName);
      if (string.Equals(leaf, "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
      var data = i.ReadContent();
      if (!LooksLikeIconOrCursor(data))
        throw new ArgumentException(
          $"ANI: input '{i.ArchiveName}' is not an ICO or CUR file (expected magic 00 00 01 00 or 00 00 02 00).",
          nameof(inputs));
      frames.Add(data);
    }
    if (frames.Count == 0)
      throw new ArgumentException("ANI: at least one CUR/ICO frame is required.", nameof(inputs));

    var title = options?.GetOption("title", "");
    var artist = options?.GetOption("artist", "");
    var defaultJiffies = (uint)(options?.GetOptionInt("default_jiffies", 6) ?? 6);

    AniWriter.Write(
      output,
      frames,
      rates: null,
      sequence: null,
      title: string.IsNullOrEmpty(title) ? null : title,
      artist: string.IsNullOrEmpty(artist) ? null : artist,
      defaultJiffies: defaultJiffies);
  }

  private static bool LooksLikeIconOrCursor(byte[] data) =>
    data.Length >= 6
    && data[0] == 0x00 && data[1] == 0x00
    && (data[2] == 0x01 || data[2] == 0x02)
    && data[3] == 0x00;

  private static IEnumerable<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var ani = AniReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));

    yield return ("metadata.ini", BuildMetadata(ani), "stored");

    for (var f = 0; f < ani.Frames.Count; f++) {
      var frameBytes = ani.Frames[f];
      // Defensive: a malformed frame shouldn't take the whole listing down. Try-catch
      // the parse, but use a flag instead of yielding inside the catch (C# disallows
      // yield in a catch body).
      IcoReader.Bundle? bundle = TryReadCur(frameBytes);

      if (bundle == null) {
        // Couldn't parse this frame — surface the raw frame bytes so the user can
        // still recover them and see the structural problem.
        yield return ($"frame_{f:D3}/raw_frame.bin", frameBytes, "stored");
        continue;
      }

      foreach (var entry in bundle.Entries) {
        // entry.Name already encodes width/height/hotspot + ".png"/".bmp" extension
        // from IcoReader (cursor_NN_WxH_hX_hY.{png|bmp} or icon_NN_WxHxBPP.{png|bmp}).
        yield return ($"frame_{f:D3}/{entry.Name}", entry.Data, entry.IsPng ? "png" : "dib");
      }
    }
  }

  private static IcoReader.Bundle? TryReadCur(byte[] frameBytes) {
    try { return IcoReader.Read(frameBytes); }
    catch (InvalidDataException) { return null; }
  }

  private static byte[] BuildMetadata(AniReader.AniFile ani) {
    var sb = new StringBuilder();
    sb.AppendLine("[ani]");
    sb.Append(CultureInfo.InvariantCulture, $"num_frames = {ani.Header.NumFrames}\n");
    sb.Append(CultureInfo.InvariantCulture, $"num_steps = {ani.Header.NumSteps}\n");
    sb.Append(CultureInfo.InvariantCulture, $"default_jiffies_per_step = {ani.Header.DefaultJiffiesPerStep}  ; 1 jiffy = 1/60 s\n");
    sb.Append(CultureInfo.InvariantCulture, $"flags = 0x{ani.Header.Flags:X8}  ; bit 0 = ICON (frames are real ICO/CUR files)\n");
    if (ani.Header.Width > 0)
      sb.Append(CultureInfo.InvariantCulture, $"declared_width = {ani.Header.Width}\n");
    if (ani.Header.Height > 0)
      sb.Append(CultureInfo.InvariantCulture, $"declared_height = {ani.Header.Height}\n");
    if (ani.Header.BitsPerPixel > 0)
      sb.Append(CultureInfo.InvariantCulture, $"declared_bpp = {ani.Header.BitsPerPixel}\n");

    if (ani.Rates.Count > 0) {
      sb.AppendLine();
      sb.AppendLine("[rate]");
      sb.Append(CultureInfo.InvariantCulture, $"jiffies_per_step = {string.Join(",", ani.Rates)}\n");
    }
    if (ani.Sequence.Count > 0) {
      sb.AppendLine();
      sb.AppendLine("[seq]");
      sb.Append(CultureInfo.InvariantCulture, $"step_to_frame = {string.Join(",", ani.Sequence)}\n");
    }
    if (!string.IsNullOrEmpty(ani.Title)) {
      sb.AppendLine();
      sb.AppendLine("[info]");
      sb.Append(CultureInfo.InvariantCulture, $"title = {ani.Title}\n");
    }
    if (!string.IsNullOrEmpty(ani.Artist)) {
      sb.Append(CultureInfo.InvariantCulture, $"artist = {ani.Artist}\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
