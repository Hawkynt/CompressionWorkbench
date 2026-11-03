#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.StreamHfe;

/// <summary>
/// HxC Stream HFE flux image (signature <c>HxC_Stream_Image</c>), the flux sibling of the bit-cell HFE.
/// It shares the <c>.hfe</c> extension, which the bit-cell HFE owns, so this one is detected by magic.
/// It lists <c>metadata.ini</c> (header fields and each track's revolution length, pulse count and
/// flags) and <c>tracks/track_CCC_S.bin</c>, the unpacked pulse stream of cylinder CCC, side S. Images
/// are created from those parts and their tracks can be edited. A removed track is written as an
/// unformatted (empty) track, since hxcfe expects every cylinder to be present. Layout in
/// <c>docs/STREAMHFE-ON-DISK.md</c>.
/// </summary>
public sealed partial class StreamHfeFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
    IArchiveModifiable, IArchiveWriteConstraints, ISyntheticEntryNames {
  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "metadata.ini" };

  [GeneratedRegex(@"^(?:.*/)?track_(\d{3})_([01])\.bin$", RegexOptions.IgnoreCase)]
  private static partial Regex TrackName();

  [GeneratedRegex(@"^track_(\d{3})_([01])_(length|flags)$")]
  private static partial Regex TrackKey();

  public string Id => "StreamHfe";
  public string DisplayName => "HxC Stream HFE";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".hfe";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("HxC_Stream_Image"u8.ToArray(), Confidence: 0.98)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lz4", "LZ4-packed pulse streams")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "HxC Floppy Emulator Stream HFE flux image (LZ4-packed pulse deltas per track)";
  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;
  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "accepts: tracks/track_CCC_S.bin (pulse stream), metadata.ini (sides, interface_mode, track_CCC_S_length, ...)";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = input.ArchiveName.Replace('\\', '/');
    if (TrackName().IsMatch(name) || Path.GetFileName(name).Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }
    reason = $"'{input.ArchiveName}' is not a Stream HFE member; {this.AcceptedInputsDescription}";
    return false;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      return Entries(Read(stream)).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null)).ToList();
    } catch (InvalidDataException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.hfe", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var (name, data) in Entries(Read(stream)))
      if (files is not { Length: > 0 } || MatchesFilter(name, files))
        WriteFile(outputDir, name, data);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    this.Apply(new StreamHfeImage(), inputs).Write(output);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    var image = this.Apply(Read(archive), inputs);
    WholeImageRebuildCommitter.Replace(archive, image.Write, candidate => Read(candidate));
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var image = Read(archive);
    foreach (var raw in entryNames) {
      var name = raw.Replace('\\', '/');
      var m = TrackName().Match(name);
      if (m.Success) image.Tracks.Remove(Key(m));
      else if (!Synthetic.Contains(Path.GetFileName(name))) throw new NotSupportedException($"Stream HFE: '{raw}' cannot be removed.");
    }
    WholeImageRebuildCommitter.Replace(archive, image.Write, candidate => Read(candidate));
  }

  private StreamHfeImage Apply(StreamHfeImage image, IReadOnlyList<ArchiveInputInfo> inputs) {
    var lengths = new Dictionary<(int, int), uint>();
    var flags = new Dictionary<(int, int), uint>();
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
      if (!this.CanAccept(input, out var reason)) throw new ArgumentException(reason);
      var content = input.ReadContent();
      var m = TrackName().Match(input.ArchiveName.Replace('\\', '/'));
      if (m.Success) {
        try {
          StreamHfeImage.Decode(content);
        } catch (InvalidDataException ex) {
          throw new ArgumentException($"Stream HFE: '{input.ArchiveName}' is not a pulse stream: {ex.Message}", ex);
        }
        // A replacement keeps the revolution time of the track it replaces when the new pulses fit in it.
        var key = Key(m);
        var old = image.Tracks.GetValueOrDefault(key);
        var fits = StreamHfeImage.DefaultLength(content);
        image.Tracks[key] = new StreamHfeImage.Track(content, old is not null && old.Length >= fits ? old.Length : fits, old?.Flags ?? StreamHfeImage.PackedFlag);
        continue;
      }
      foreach (var line in Encoding.UTF8.GetString(content).Split('\n')) {
        var eq = line.IndexOf('=');
        if (eq <= 0) continue;
        var k = line[..eq].Trim();
        var v = line[(eq + 1)..].TrimEnd('\r').Trim();
        var t = TrackKey().Match(k);
        if (t.Success) {
          var key = (int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(t.Groups[2].Value, CultureInfo.InvariantCulture));
          (t.Groups[3].Value == "length" ? lengths : flags)[key] = Number(k, v);
          continue;
        }
        switch (k) {
          case "revision": image.Revision = Number(k, v); break;
          case "flags": image.Flags = Number(k, v); break;
          case "tracks": image.Cylinders = (int)Number(k, v); break;
          case "sides": image.Sides = (int)Number(k, v); break;
          case "bit_period_ps": image.BitPeriod = Number(k, v); break;
          case "interface_mode": image.InterfaceMode = Number(k, v); break;
          case "name": image.Name = v; break;
          case "date": image.Date = v; break;
        }
      }
    }
    foreach (var (key, track) in image.Tracks.ToList())
      image.Tracks[key] = track with {
        Length = lengths.GetValueOrDefault(key, track.Length),
        Flags = flags.GetValueOrDefault(key, track.Flags),
      };
    return image;
  }

  private static uint Number(string key, string value)
    => uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
      : throw new ArgumentException($"Stream HFE: metadata '{key}' is not a number: '{value}'.");

  private static (int, int) Key(Match m)
    => (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));

  private static StreamHfeImage Read(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return StreamHfeImage.Read(ms.ToArray());
  }

  private static IEnumerable<(string Name, byte[] Data)> Entries(StreamHfeImage image) {
    yield return ("metadata.ini", Encoding.UTF8.GetBytes(image.RenderMetadata()));
    foreach (var ((c, s), t) in image.Tracks)
      yield return ($"tracks/track_{c:D3}_{s}.bin", t.Pulses);
  }
}
