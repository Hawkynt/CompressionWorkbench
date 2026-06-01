using System.Text.Json;
using System.Text.Json.Serialization;

namespace Compression.Registry.Layout;

/// <summary>
/// Strategy for files that match no <see cref="LayoutZone"/> in a
/// <see cref="LayoutTemplate"/>.
/// </summary>
public enum LeftoverStrategy {
  /// <summary>Place leftover files in the gaps between zones (default).</summary>
  FillGaps,
  /// <summary>Place leftover files after the last zone.</summary>
  AppendAtEnd,
}

/// <summary>
/// A reusable layout description: a named collection of <see cref="LayoutZone"/>s
/// plus a strategy for leftover files. Round-trippable to / from JSON via
/// <see cref="ToJson"/> and <see cref="FromJson(string)"/>.
///
/// <para>Example JSON:</para>
/// <code>
/// {
///   "name": "Hot at start, cold at end",
///   "metadataZone": "Middle",
///   "leftoverStrategy": "fill_gaps",
///   "zones": [
///     { "name": "boot",   "range": "0%-5%",   "sortBy": ["name asc"] },
///     { "name": "hot",    "range": "5%-40%",  "filter": "lastModified >= quartile(0.75)",
///                                              "sortBy": ["lastModified desc", "size desc"] },
///     { "name": "frozen", "range": "85%-100%","filter": "lastModified &lt;= quartile(0.25)" }
///   ]
/// }
/// </code>
/// </summary>
public sealed record LayoutTemplate {
  /// <summary>Human-readable template name (used in UI / logs).</summary>
  public required string Name { get; init; }

  /// <summary>Metadata zone placement; defaults to <see cref="MetadataZone.Unchanged"/>.</summary>
  public MetadataZone MetadataZone { get; init; } = MetadataZone.Unchanged;

  /// <summary>Ordered list of zones. Zones may overlap; first match wins.</summary>
  public IReadOnlyList<LayoutZone> Zones { get; init; } = [];

  /// <summary>
  /// What to do with files matching no zone. Stored as text (<c>"fill_gaps"</c>
  /// or <c>"append_at_end"</c>) so the JSON format stays human-readable, but
  /// the canonical accessor is <see cref="LeftoverStrategy"/>.
  /// </summary>
  public string LeftoverStrategyText { get; init; } = "fill_gaps";

  /// <summary>Parsed form of <see cref="LeftoverStrategyText"/>.</summary>
  [JsonIgnore]
  public LeftoverStrategy LeftoverStrategy => this.LeftoverStrategyText.ToLowerInvariant() switch {
    "append_at_end" or "appendatend" or "append-at-end" => Layout.LeftoverStrategy.AppendAtEnd,
    _ => Layout.LeftoverStrategy.FillGaps,
  };

  /// <summary>Serialises to indented JSON.</summary>
  public string ToJson() {
    var dto = new LayoutTemplateJsonDto {
      Name = this.Name,
      MetadataZone = this.MetadataZone.ToString(),
      LeftoverStrategy = this.LeftoverStrategyText,
      Zones = [.. this.Zones.Select(z => new LayoutZoneJsonDto {
        Name = z.Name,
        Range = z.Range,
        Filter = z.Filter,
        SortBy = [.. z.SortBy.Select(k => k.ToString())],
      })],
    };
    return JsonSerializer.Serialize(dto, JsonOptions);
  }

  /// <summary>
  /// Parses a layout template from JSON. Throws <see cref="FormatException"/>
  /// when required fields are missing or any embedded expression fails to
  /// parse.
  /// </summary>
  public static LayoutTemplate FromJson(string json) {
    ArgumentNullException.ThrowIfNull(json);
    LayoutTemplateJsonDto? dto;
    try {
      dto = JsonSerializer.Deserialize<LayoutTemplateJsonDto>(json, JsonOptions);
    } catch (JsonException ex) {
      throw new FormatException($"Layout template JSON is malformed: {ex.Message}", ex);
    }
    if (dto is null) throw new FormatException("Layout template JSON deserialised to null.");
    if (string.IsNullOrWhiteSpace(dto.Name))
      throw new FormatException("Layout template 'name' is required.");

    var metaZone = MetadataZone.Unchanged;
    if (!string.IsNullOrWhiteSpace(dto.MetadataZone)
        && !Enum.TryParse(dto.MetadataZone, ignoreCase: true, out metaZone))
      throw new FormatException($"Unknown metadataZone '{dto.MetadataZone}'.");

    var zones = new List<LayoutZone>();
    if (dto.Zones is not null) {
      for (var i = 0; i < dto.Zones.Count; i++) {
        var z = dto.Zones[i];
        if (z is null) throw new FormatException($"Zone at index {i} is null.");
        if (string.IsNullOrWhiteSpace(z.Name))
          throw new FormatException($"Zone at index {i} is missing 'name'.");
        if (string.IsNullOrWhiteSpace(z.Range))
          throw new FormatException($"Zone '{z.Name}' is missing 'range'.");

        // Eager validation: parse range + filter + sort keys now so errors
        // surface here instead of during defrag.
        try { _ = RangeSpec.Parse(z.Range); }
        catch (FormatException ex) { throw new FormatException($"Zone '{z.Name}' has invalid range '{z.Range}': {ex.Message}", ex); }

        if (!string.IsNullOrWhiteSpace(z.Filter)) {
          try { _ = FilterExpression.Parse(z.Filter); }
          catch (FormatException ex) { throw new FormatException($"Zone '{z.Name}' has invalid filter: {ex.Message}", ex); }
        }

        var sortKeys = new List<DefragSortKey>();
        if (z.SortBy is not null) {
          foreach (var s in z.SortBy) {
            if (string.IsNullOrWhiteSpace(s)) continue;
            try { sortKeys.Add(DefragSortKey.Parse(s)); }
            catch (FormatException ex) { throw new FormatException($"Zone '{z.Name}' has invalid sort key '{s}': {ex.Message}", ex); }
          }
        }

        zones.Add(new LayoutZone {
          Name = z.Name,
          Range = z.Range,
          Filter = string.IsNullOrWhiteSpace(z.Filter) ? null : z.Filter,
          SortBy = sortKeys,
        });
      }
    }

    return new LayoutTemplate {
      Name = dto.Name,
      MetadataZone = metaZone,
      Zones = zones,
      LeftoverStrategyText = string.IsNullOrWhiteSpace(dto.LeftoverStrategy) ? "fill_gaps" : dto.LeftoverStrategy,
    };
  }

  /// <summary>Loads a template from a file on disk.</summary>
  public static LayoutTemplate Load(string path) {
    ArgumentNullException.ThrowIfNull(path);
    return FromJson(File.ReadAllText(path));
  }

  /// <summary>Saves the template to <paramref name="path"/> as indented JSON.</summary>
  public void Save(string path) {
    ArgumentNullException.ThrowIfNull(path);
    File.WriteAllText(path, this.ToJson());
  }

  private static readonly JsonSerializerOptions JsonOptions = new() {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  };

  // ── Internal DTO types ─────────────────────────────────────────────────
  // System.Text.Json can't directly bind to required-init records when some
  // fields are absent. The DTOs are loose so a JSON file with only a name
  // still parses cleanly (the validator above produces nice errors).

  private sealed class LayoutTemplateJsonDto {
    public string? Name { get; set; }
    public string? MetadataZone { get; set; }
    public string? LeftoverStrategy { get; set; }
    public List<LayoutZoneJsonDto>? Zones { get; set; }
  }

  private sealed class LayoutZoneJsonDto {
    public string? Name { get; set; }
    public string? Range { get; set; }
    public string? Filter { get; set; }
    public List<string>? SortBy { get; set; }
  }
}
