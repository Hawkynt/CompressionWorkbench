namespace FileFormat.Uharc;

/// <summary>
/// Adds, replaces and removes files of a UHARC archive by rebuilding it (docs/UHARC-ON-DISK.md,
/// "Editing"). Every version the reader opens is rewritten as itself: the same version and
/// stream method byte (so the same mode and window), the same password, the same
/// self-extractor stub, and every kept file with its name, directory, time stamp and attribute
/// byte; the filters are applied as the writer applies them to new archives. The new archive is
/// read back and compared before it replaces the old one, so a failed edit leaves the bytes as
/// they were.
/// </summary>
internal static class UharcEditor {

  /// <summary>Rebuilds <paramref name="archive"/> with <paramref name="edit"/> applied to its members.</summary>
  /// <param name="archive">The archive, read and then overwritten in place.</param>
  /// <param name="password">The archive's password, when it has one.</param>
  /// <param name="edit">Changes the list of members (files and directories, in stored order).</param>
  /// <exception cref="UnauthorizedAccessException">The archive has a password and it is missing or wrong.</exception>
  /// <exception cref="NotSupportedException">The stream cannot be rewritten, or the method cannot be decoded.</exception>
  public static void Edit(Stream archive, string? password, Action<List<UharcWriter.Item>> edit) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(edit);
    if (!archive.CanSeek || !archive.CanWrite)
      throw new NotSupportedException("UHARC: editing needs a seekable, writable stream.");

    archive.Position = 0;
    var original = new byte[archive.Length];
    archive.ReadExactly(original);
    password = string.IsNullOrEmpty(password) ? null : password;

    UharcReader reader;
    try {
      reader = new UharcReader(new MemoryStream(original, writable: false), password);
    } catch (InvalidOperationException e) {
      throw new UnauthorizedAccessException("UHARC: the archive is password protected; editing it needs its password. It was left unchanged.", e);
    } catch (InvalidDataException e) when (e.Data.Contains(UharcReader.WrongPasswordKey)) {
      throw new UnauthorizedAccessException("UHARC: wrong password; the archive was left unchanged.", e);
    }

    if (!reader.CanDecodeMethod)
      throw new NotSupportedException($"UHARC: method {reader.Method} cannot be decoded, so the archive cannot be rebuilt; it was left unchanged.");

    var key = reader.IsEncrypted ? password : null;
    var items = new List<UharcWriter.Item>();
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) {
        items.Add(new(entry.FileName, null, null));
        continue;
      }

      byte[] data;
      try {
        data = reader.Extract(entry);
      } catch (InvalidDataException e) when (key != null) {
        throw new UnauthorizedAccessException("UHARC: the stream does not decode with this password; the archive was left unchanged.", e);
      }

      items.Add(new(entry.FileName, data, entry.LastModified, entry.Attributes));
    }

    edit(items);
    if (!items.Any(i => i.Data != null))
      throw new NotSupportedException("UHARC: an archive must keep at least one file (the original tools refuse an archive of none); it was left unchanged.");

    // 0.3's second store code (80) is written as 0: uharc_u.exe extracts no file from a stream
    // stored under 80.
    var method = reader.Version == 3 && reader.Method == UharcMethods.Store03Alias ? UharcMethods.Store : reader.Method;
    var stub = reader.ArchiveStart > 0 ? original[..reader.ArchiveStart] : null;
    using var rebuilt = new MemoryStream();
    UharcWriter.Write(rebuilt, items, UharcWriter.Mode.Store, DateTime.Now, key, stub, version: reader.Version, forcedMethod: method);
    var image = rebuilt.ToArray();
    Verify(image, key, items);

    archive.Position = 0;
    archive.SetLength(0);
    archive.Write(image);
    archive.Flush();
  }

  /// <summary>Adds files (and directories), replacing a file of the same name and keeping its attribute byte.</summary>
  public static void Add(List<UharcWriter.Item> items, IEnumerable<(string Name, byte[]? Data, DateTime? Modified)> additions) {
    foreach (var (rawName, data, modified) in additions) {
      var name = Normalize(rawName);
      if (name.Length == 0)
        continue;
      var at = items.FindIndex(i => (i.Data == null) == (data == null) && string.Equals(Normalize(i.Name), name, StringComparison.OrdinalIgnoreCase));
      if (data == null) {
        if (at < 0)
          items.Add(new(name, null, null));
        continue;
      }

      if (at < 0)
        items.Add(new(name, data, modified));
      else
        items[at] = items[at] with { Data = data, LastModified = modified };
    }
  }

  /// <summary>Removes files and directories by name; a directory takes everything beneath it. Unknown names are ignored.</summary>
  public static void Remove(List<UharcWriter.Item> items, IEnumerable<string> names) {
    foreach (var raw in names) {
      var name = Normalize(raw);
      if (name.Length != 0)
        items.RemoveAll(i => string.Equals(Normalize(i.Name), name, StringComparison.OrdinalIgnoreCase)
          || Normalize(i.Name).StartsWith(name + "/", StringComparison.OrdinalIgnoreCase));
    }
  }

  private static string Normalize(string name) => name.Replace('\\', '/').Trim('/');

  /// <summary>Reads the rebuilt archive back: every file must come back as it went in.</summary>
  private static void Verify(byte[] image, string? password, List<UharcWriter.Item> items) {
    var check = new UharcReader(new MemoryStream(image, writable: false), password);
    var expected = items.Where(i => i.Data != null).ToList();
    var files = check.Entries.Where(e => !e.IsDirectory).ToList();
    if (files.Count != expected.Count)
      throw new InvalidOperationException("UHARC: the rebuilt archive lost a file; the archive was left unchanged.");
    foreach (var entry in files) {
      var item = expected.FirstOrDefault(i => string.Equals(Normalize(i.Name), entry.FileName, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"UHARC: the rebuilt archive holds an unexpected '{entry.FileName}'; the archive was left unchanged.");
      if (!check.Extract(entry).AsSpan().SequenceEqual(item.Data))
        throw new InvalidOperationException($"UHARC: '{entry.FileName}' does not come back from the rebuilt archive; the archive was left unchanged.");
    }
  }
}
