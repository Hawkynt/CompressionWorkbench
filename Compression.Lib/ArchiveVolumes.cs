using System.Globalization;
using System.Text.RegularExpressions;
using Compression.Core.Streams;

namespace Compression.Lib;

/// <summary>
/// Split archives the way 7-Zip writes and reads them (<c>7z a -v…</c>): the archive's bytes cut
/// into <c>name.001</c>, <c>name.002</c>, … — <c>backup.7z.001</c> for <c>backup.7z</c> — with no
/// header or trailer of their own, so reading them is reading their concatenation.
/// </summary>
public static partial class ArchiveVolumes {
  [GeneratedRegex(@"^(?<base>.+)\.(?<number>\d{3,})$")]
  private static partial Regex VolumeName();

  /// <summary>
  /// Whether <paramref name="path"/> names a volume (<c>….NNN</c>, three or more digits), with
  /// the path it splits and its number.
  /// </summary>
  public static bool TryParseVolume(string path, out string basePath, out int number) {
    var match = VolumeName().Match(path);
    if (match.Success && int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0) {
      basePath = match.Groups["base"].Value;
      return true;
    }
    basePath = path;
    number = 0;
    return false;
  }

  /// <summary>The path of volume <paramref name="number"/> of <paramref name="basePath"/>.</summary>
  public static string VolumePath(string basePath, int number) => $"{basePath}.{number:D3}";

  /// <summary>
  /// The volumes of the set <paramref name="path"/> belongs to, first to last; just
  /// <paramref name="path"/> when it is not a volume or the set has a single member.
  /// </summary>
  /// <exception cref="FileNotFoundException">A volume before the last one present is missing.</exception>
  public static IReadOnlyList<string> VolumesOf(string path) {
    if (!TryParseVolume(path, out var basePath, out _))
      return [path];

    var volumes = new List<string>();
    for (var n = 1; File.Exists(VolumePath(basePath, n)); ++n)
      volumes.Add(VolumePath(basePath, n));

    // A gap with later volumes behind it is a missing volume, not the end of the set.
    var directory = Path.GetDirectoryName(Path.GetFullPath(basePath)) ?? ".";
    var prefix = Path.GetFileName(basePath) + ".";
    var highest = Directory.EnumerateFiles(directory, prefix + "*")
      .Select(static f => TryParseVolume(f, out _, out var n) ? n : 0)
      .DefaultIfEmpty(0).Max();
    if (highest > volumes.Count)
      throw new FileNotFoundException(
        $"Volume {VolumePath(basePath, volumes.Count + 1)} of the split archive is missing (volumes up to .{highest:D3} exist).",
        VolumePath(basePath, volumes.Count + 1));
    if (volumes.Count == 0)
      throw new FileNotFoundException($"The first volume {VolumePath(basePath, 1)} of the split archive is missing.", VolumePath(basePath, 1));
    return volumes;
  }

  /// <summary>The path detection should look at: the first volume of a set, else the path itself.</summary>
  public static string FirstVolume(string path) => VolumesOf(path)[0];

  /// <summary>
  /// The format of what <paramref name="path"/> holds. For a volume set that is the format of the
  /// joined archive, recognised by its signature at the start of the first volume (a 7z, ZIP or
  /// RAR cut by 7-Zip); only content no signature claims stays the plain "split file".
  /// </summary>
  public static FormatDetector.Format Detect(string path) {
    var first = FirstVolume(path);
    var format = FormatDetector.Detect(first);
    if (format != FormatDetector.Format.SplitFile)
      return format;

    var header = new byte[(int)Math.Min(new FileInfo(first).Length, 1 << 16)];
    using (var stream = File.OpenRead(first))
      stream.ReadExactly(header);
    var inner = FormatDetector.DetectByMagic(header);
    return inner is FormatDetector.Format.Unknown or FormatDetector.Format.SplitFile ? format : inner;
  }

  /// <summary>
  /// Opens <paramref name="path"/> for reading — a split archive as one seekable stream over all
  /// its volumes. A volume missing at the end shows as a truncated archive, which the format's
  /// reader reports.
  /// </summary>
  public static Stream OpenRead(string path) {
    var volumes = VolumesOf(path);
    if (volumes.Count == 1)
      return File.OpenRead(volumes[0]);

    var streams = new Stream[volumes.Count];
    try {
      for (var i = 0; i < volumes.Count; ++i)
        streams[i] = File.OpenRead(volumes[i]);
    } catch {
      foreach (var s in streams)
        s?.Dispose();
      throw;
    }
    return new ConcatenatedStream(streams);
  }

  /// <summary>
  /// Splits the file at <paramref name="path"/> into volumes of <paramref name="volumeSize"/>
  /// bytes (the last one shorter), named as 7-Zip names them, removes the original, and removes
  /// stale higher-numbered volumes a previous run left behind. Returns the volume paths.
  /// </summary>
  public static IReadOnlyList<string> Split(string path, long volumeSize) {
    ArgumentOutOfRangeException.ThrowIfLessThan(volumeSize, 1);
    var volumes = new List<string>();
    using (var source = File.OpenRead(path)) {
      var buffer = new byte[(int)Math.Min(volumeSize, 1 << 20)];
      var remaining = source.Length;
      do {
        var target = VolumePath(path, volumes.Count + 1);
        using (var volume = File.Create(target)) {
          var left = Math.Min(volumeSize, remaining);
          while (left > 0) {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read <= 0)
              throw new EndOfStreamException($"{path} changed while it was being split.");
            volume.Write(buffer, 0, read);
            left -= read;
            remaining -= read;
          }
          volume.Flush(flushToDisk: true);
        }
        volumes.Add(target);
      } while (remaining > 0);
    }

    for (var n = volumes.Count + 1; File.Exists(VolumePath(path, n)); ++n)
      File.Delete(VolumePath(path, n));
    File.Delete(path);
    return volumes;
  }
}
