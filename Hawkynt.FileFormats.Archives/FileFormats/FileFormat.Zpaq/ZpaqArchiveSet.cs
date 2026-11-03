using System.Globalization;

namespace FileFormat.Zpaq;

/// <summary>
/// Opens a zpaq archive together with the files that belong to it: the later parts of a
/// multi-part archive, or the parts an index (<c>-index</c>) was written for.
/// </summary>
/// <remarks>
/// <para>zpaq 7.15 names parts by replacing the <c>?</c> run (zero-padded) or <c>*</c> in the
/// archive name with the part number: <c>arc???</c> gives <c>arc001.zpaq</c>, <c>arc002.zpaq</c>,
/// ...; every update appends one part, and the archive is their concatenation. With
/// <c>-index arc000.zpaq</c> the index takes part number 0. When the parts are encrypted only the
/// first starts with the salt; the rest continue its key stream.</para>
/// <para>A name with a number is not proof of a set, so a candidate part is taken only when the
/// concatenation still reads without errors, adds versions, and the first new version's fragment
/// numbers continue where the earlier parts stopped. A separate archive numbers its fragments from
/// 1 again and is left out.</para>
/// </remarks>
internal static class ZpaqArchiveSet {
  /// <summary>Opens <paramref name="stream"/> and, when it is a file, the rest of its set.</summary>
  public static ZpaqReader Open(Stream stream, string? password) {
    if (stream is FileStream file && TryParse(file.Name, out var name)) {
      var reader = name.Number switch {
        0 => OpenParts(name, stream, password, includeFirst: false),
        1 => OpenParts(name, stream, password, includeFirst: true),
        _ => null,
      };
      if (reader != null)
        return reader;
    }
    stream.Position = 0;
    return new ZpaqReader(stream, password, leaveOpen: true);
  }

  private sealed record PartName(string Prefix, string Suffix, int Number, int Width) {
    public string Path(int number) => this.Prefix + number.ToString(new string('0', this.Width), CultureInfo.InvariantCulture) + this.Suffix;
  }

  private static bool TryParse(string path, out PartName name) {
    name = null!;
    var dir = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
    var file = System.IO.Path.GetFileName(path);
    var ext = System.IO.Path.GetExtension(file);
    var stem = file[..^ext.Length];
    var digits = 0;
    while (digits < stem.Length && char.IsAsciiDigit(stem[^(digits + 1)]))
      ++digits;
    if (digits == 0 || digits > 9)
      return false;
    name = new PartName(System.IO.Path.Combine(dir, stem[..^digits]), ext,
      int.Parse(stem.AsSpan(stem.Length - digits), CultureInfo.InvariantCulture), digits);
    return true;
  }

  /// <summary>
  /// Reads parts 1, 2, ... for as long as each one continues the set. For an index
  /// (<paramref name="includeFirst"/> false) part 1 is opened from disk too, and the set is used
  /// only if it exists; for part 1 the given stream is the first part.
  /// </summary>
  private static ZpaqReader? OpenParts(PartName name, Stream first, string? password, bool includeFirst) {
    var parts = new List<Stream>();
    var owned = new List<Stream>();
    try {
      if (includeFirst)
        parts.Add(first);
      else {
        var one = name.Path(1);
        if (!File.Exists(one))
          return null;
        var s = File.OpenRead(one);
        parts.Add(s);
        owned.Add(s);
      }

      var accepted = TryRead(parts, password);
      if (accepted == null) {
        foreach (var s in owned)
          s.Dispose();
        return null;
      }
      for (var number = 2; ; ++number) {
        var path = name.Path(number);
        if (!File.Exists(path))
          break;
        var next = File.OpenRead(path);
        var candidate = TryRead([.. parts, next], password);
        if (candidate == null || !Continues(accepted, candidate)) {
          candidate?.Dispose();
          next.Dispose();
          break;
        }
        accepted.Dispose();
        parts.Add(next);
        owned.Add(next);
        accepted = candidate;
      }
      if (parts.Count == 1 && includeFirst) {
        // A lone part 1 is just an archive: the caller opens it as one.
        accepted.Dispose();
        return null;
      }
      accepted.Own(owned);
      return accepted;
    } catch {
      foreach (var s in owned)
        s.Dispose();
      throw;
    }
  }

  /// <summary>A reader over the parts' concatenation, or null when that does not read cleanly.</summary>
  /// <remarks>Disposing the reader closes the concatenation, never the parts themselves.</remarks>
  private static ZpaqReader? TryRead(IReadOnlyList<Stream> parts, string? password) {
    var concat = new ZpaqConcatStream(parts, owned: []);
    try {
      var reader = new ZpaqReader(concat, password, leaveOpen: false);
      if (reader.Errors == 0)
        return reader;
      reader.Dispose();
      return null;
    } catch (Exception e) when (e is InvalidDataException or EndOfStreamException or NotSupportedException) {
      concat.Dispose();
      return null;
    }
  }

  /// <summary>Whether <paramref name="longer"/> is <paramref name="shorter"/> plus at least one version that continues it.</summary>
  private static bool Continues(ZpaqReader shorter, ZpaqReader longer) {
    if (longer.VersionCount <= shorter.VersionCount)
      return false;
    var first = longer.VersionFirstFragments[shorter.VersionCount];
    return first == 0 || first == shorter.NextFragment;
  }
}
