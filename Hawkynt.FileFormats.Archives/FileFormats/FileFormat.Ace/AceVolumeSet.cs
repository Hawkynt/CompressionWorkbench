using System.Globalization;

namespace FileFormat.Ace;

/// <summary>
/// Finds the volumes that follow an ACE volume on disk. WinAce names them <c>NAME.ACE</c>,
/// <c>NAME.C00</c> … <c>NAME.C99</c>, then <c>NAME.100</c> … <c>NAME.999</c>; the letter case of
/// the given file is tried first, then the other. A candidate is taken only when it is a volume of
/// a multi-volume set with the next volume number.
/// </summary>
internal static class AceVolumeSet {
  /// <summary>Opens a reader over <paramref name="stream"/> and, when it is a file of a multi-volume set, the volumes after it.</summary>
  public static AceReader Open(Stream stream, string? password) {
    var volumes = new List<Stream> { stream };
    if (stream is FileStream file
        && AceReader.TryReadVolumeInfo(stream, out var number, out var multi) && multi) {
      var path = file.Name;
      while (NextVolumeNames(path) is var names && names.Length > 0) {
        var next = names.FirstOrDefault(File.Exists);
        if (next == null)
          break;
        var candidate = File.OpenRead(next);
        if (!AceReader.TryReadVolumeInfo(candidate, out var n, out var m) || !m || n != number + 1) {
          candidate.Dispose();
          break;
        }
        volumes.Add(candidate);
        number = n;
        path = next;
      }
    }
    stream.Position = 0;
    // The caller keeps its own stream; the volumes opened here go with the reader.
    return new AceReader(volumes, password, ownedFrom: 1);
  }

  /// <summary>The name of the volume after <paramref name="path"/>, in the file's own letter case first.</summary>
  internal static string[] NextVolumeNames(string path) {
    var ext = Path.GetExtension(path);
    var stem = path[..^ext.Length];
    var upper = ext.Length > 1 && char.IsUpper(ext[1]);
    string next;
    if (ext.Length == 4 && (ext[1] is 'c' or 'C') && int.TryParse(ext.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var c))
      next = c < 99 ? $".c{c + 1:D2}" : ".100";
    else if (ext.Length == 4 && int.TryParse(ext.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d >= 100)
      next = d < 999 ? $".{d + 1}" : "";
    else
      next = ".c00";
    if (next.Length == 0)
      return [];
    var first = stem + (upper ? next.ToUpperInvariant() : next);
    var second = stem + (upper ? next : next.ToUpperInvariant());
    return first == second ? [first] : [first, second];
  }
}
