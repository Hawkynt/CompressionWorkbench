namespace Compression.Registry.Layout;

/// <summary>
/// File-metadata fields the layout-template sorter can order files by.
/// Mirrors the fields available on <see cref="IFilterFileContext"/>.
/// </summary>
public enum DefragSortField {
  /// <summary>File name (final path segment), ordinal compare.</summary>
  Name,
  /// <summary>Full path, ordinal compare.</summary>
  Path,
  /// <summary>File extension including leading dot, ordinal-ignore-case compare.</summary>
  Extension,
  /// <summary>File size in bytes.</summary>
  Size,
  /// <summary>Last-modified timestamp. Null sorts last.</summary>
  LastModified,
  /// <summary>Last-accessed timestamp. Null sorts last.</summary>
  LastAccessed,
  /// <summary>Created timestamp. Null sorts last.</summary>
  Created,
  /// <summary>
  /// Attribute bitmask. Files with attributes &gt; 0 sort first ascending —
  /// useful for clustering system / hidden / read-only files apart from
  /// the bulk.
  /// </summary>
  Attributes,
}

/// <summary>Sort order for a <see cref="DefragSortKey"/>.</summary>
public enum SortDirection {
  /// <summary>Ascending (smallest / earliest first).</summary>
  Ascending,
  /// <summary>Descending (largest / most-recent first).</summary>
  Descending,
}

/// <summary>
/// One ordering rule applied within a <see cref="LayoutZone"/>. A zone may
/// list several keys; they are applied in order with later keys breaking
/// ties of earlier ones. Round-trippable via <see cref="Parse(string)"/>
/// and <see cref="ToString"/>.
/// </summary>
public sealed record DefragSortKey(DefragSortField Field, SortDirection Direction) {

  /// <summary>
  /// Parses a textual sort key. Accepted forms (whitespace-insensitive,
  /// case-insensitive on identifiers):
  /// <list type="bullet">
  ///   <item><c>name</c> — defaults to ascending.</item>
  ///   <item><c>name asc</c> / <c>name ascending</c></item>
  ///   <item><c>lastModified desc</c> / <c>last_modified descending</c></item>
  ///   <item><c>size desc</c></item>
  /// </list>
  /// Identifier matching accepts <c>camelCase</c>, <c>snake_case</c>,
  /// <c>kebab-case</c>, and the enum's own ToString form.
  /// </summary>
  public static DefragSortKey Parse(string s) {
    ArgumentNullException.ThrowIfNull(s);
    var trimmed = s.Trim();
    if (trimmed.Length == 0) throw new FormatException("Empty sort key.");

    string fieldPart, directionPart;
    var spaceIdx = trimmed.IndexOfAny([' ', '\t']);
    if (spaceIdx < 0) {
      fieldPart = trimmed;
      directionPart = "asc";
    } else {
      fieldPart = trimmed[..spaceIdx];
      directionPart = trimmed[(spaceIdx + 1)..].Trim();
    }

    var field = ParseField(fieldPart);
    var direction = ParseDirection(directionPart);
    return new DefragSortKey(field, direction);
  }

  private static DefragSortField ParseField(string s) {
    // Strip separators so "last_modified" / "last-modified" / "LastModified" all match.
    var normalised = NormaliseIdent(s);
    return normalised switch {
      "name" => DefragSortField.Name,
      "path" => DefragSortField.Path,
      "extension" or "ext" => DefragSortField.Extension,
      "size" or "length" => DefragSortField.Size,
      "lastmodified" or "mtime" or "modified" => DefragSortField.LastModified,
      "lastaccessed" or "atime" or "accessed" => DefragSortField.LastAccessed,
      "created" or "ctime" or "creationtime" => DefragSortField.Created,
      "attributes" or "attrs" or "attr" => DefragSortField.Attributes,
      _ => throw new FormatException($"Unknown sort field '{s}'."),
    };
  }

  private static SortDirection ParseDirection(string s) {
    var normalised = s.Trim().ToLowerInvariant();
    return normalised switch {
      "" or "asc" or "ascending" or "up" or "+" => SortDirection.Ascending,
      "desc" or "descending" or "down" or "-" => SortDirection.Descending,
      _ => throw new FormatException($"Unknown sort direction '{s}'."),
    };
  }

  private static string NormaliseIdent(string s) {
    Span<char> buf = stackalloc char[s.Length];
    var idx = 0;
    foreach (var c in s) {
      if (c == '_' || c == '-' || c == ' ') continue;
      buf[idx++] = char.ToLowerInvariant(c);
    }
    return new string(buf[..idx]);
  }

  /// <inheritdoc/>
  public override string ToString()
    => $"{FieldToText(this.Field)} {(this.Direction == SortDirection.Ascending ? "asc" : "desc")}";

  private static string FieldToText(DefragSortField f) => f switch {
    DefragSortField.Name => "name",
    DefragSortField.Path => "path",
    DefragSortField.Extension => "extension",
    DefragSortField.Size => "size",
    DefragSortField.LastModified => "lastModified",
    DefragSortField.LastAccessed => "lastAccessed",
    DefragSortField.Created => "created",
    DefragSortField.Attributes => "attributes",
    _ => f.ToString().ToLowerInvariant(),
  };
}
