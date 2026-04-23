#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.JpegArchive;

/// <summary>
/// A subset of the IPTC-IIM "Application Record" (record 2) fields that
/// photo tools read almost universally. IIM is the binary metadata format
/// that predates XMP; many legacy apps (Windows Explorer details pane,
/// older Lightroom, most DAMs) still read it preferentially.
///
/// Field reference: https://www.iptc.org/std/photometadata/documentation/mapping-guidelines/
/// </summary>
public sealed record IptcFields {
  public string? ObjectName { get; init; }          // 2:5  — equivalent to XMP Title
  public string? Instructions { get; init; }        // 2:40 — photoshop:Instructions
  public IReadOnlyList<string>? Keywords { get; init; }  // 2:25 — repeatable
  public string? DateCreatedYyyyMmDd { get; init; } // 2:55 — CCYYMMDD
  public string? TimeCreatedHhMmSsZz { get; init; } // 2:60 — HHMMSS±HHMM
  public string? ByLine { get; init; }              // 2:80 — dc:creator
  public string? City { get; init; }                // 2:90
  public string? SubLocation { get; init; }         // 2:92
  public string? ProvinceState { get; init; }       // 2:95
  public string? CountryCode { get; init; }         // 2:100
  public string? CountryName { get; init; }         // 2:101
  public string? Headline { get; init; }            // 2:105 — photoshop:Headline
  public string? Credit { get; init; }              // 2:110 — photoshop:Credit
  public string? Source { get; init; }              // 2:115 — photoshop:Source
  public string? Caption { get; init; }             // 2:120
  public string? CopyrightNotice { get; init; }     // 2:116
  public string? DescriptionWriter { get; init; }   // 2:122
  public string? TransmissionReference { get; init; } // 2:103 — photoshop:TransmissionReference
  public string? CreatorJobTitle { get; init; }     // 2:85 — Iptc4xmpCore:CiJobtitle — dc:rights

  public bool IsEmpty =>
    string.IsNullOrWhiteSpace(this.ObjectName)
    && (this.Keywords is null || this.Keywords.Count == 0)
    && string.IsNullOrWhiteSpace(this.City)
    && string.IsNullOrWhiteSpace(this.SubLocation)
    && string.IsNullOrWhiteSpace(this.ProvinceState)
    && string.IsNullOrWhiteSpace(this.CountryCode)
    && string.IsNullOrWhiteSpace(this.CountryName)
    && string.IsNullOrWhiteSpace(this.Caption)
    && string.IsNullOrWhiteSpace(this.Instructions)
    && string.IsNullOrWhiteSpace(this.DateCreatedYyyyMmDd)
    && string.IsNullOrWhiteSpace(this.TimeCreatedHhMmSsZz)
    && string.IsNullOrWhiteSpace(this.ByLine)
    && string.IsNullOrWhiteSpace(this.Headline)
    && string.IsNullOrWhiteSpace(this.Credit)
    && string.IsNullOrWhiteSpace(this.Source)
    && string.IsNullOrWhiteSpace(this.CopyrightNotice)
    && string.IsNullOrWhiteSpace(this.DescriptionWriter)
    && string.IsNullOrWhiteSpace(this.TransmissionReference)
    && string.IsNullOrWhiteSpace(this.CreatorJobTitle);
}

/// <summary>
/// Encodes and decodes IPTC-IIM records — the tag-length-value sequence used
/// inside both JPEG APP13 Photoshop IRBs and TIFF tag 0x83BB.
///
/// Tag marker: <c>0x1C</c> followed by record number (2 = application) and
/// dataset number. Length is big-endian 2 bytes; data follows. The optional
/// 1:90 "Coded Character Set" field (value <c>ESC % G</c>) marks the record
/// as UTF-8 — we always write that first so non-ASCII names/cities round-trip.
/// </summary>
public static class IptcIimEncoder {
  private const byte TagMarker = 0x1C;

  private const byte RecordEnvelope = 1;
  private const byte RecordApplication = 2;

  public const byte DsCodedCharacterSet = 90;  // record 1
  public const byte DsObjectName = 5;
  public const byte DsTransmissionReference = 103;
  public const byte DsDescriptionWriter = 122;
  public const byte DsCreatorJobTitle = 85;
  public const byte DsInstructions = 40;
  public const byte DsDateCreated = 55;
  public const byte DsTimeCreated = 60;
  public const byte DsKeywords = 25;
  public const byte DsByLine = 80;
  public const byte DsCity = 90;
  public const byte DsSubLocation = 92;
  public const byte DsProvinceState = 95;
  public const byte DsCountryCode = 100;
  public const byte DsCountryName = 101;
  public const byte DsHeadline = 105;
  public const byte DsCredit = 110;
  public const byte DsSource = 115;
  public const byte DsCopyrightNotice = 116;
  public const byte DsCaption = 120;

  /// <summary>Writes the given fields to a byte buffer in IPTC-IIM form (no 8BIM wrapper).</summary>
  public static byte[] Encode(IptcFields fields) {
    ArgumentNullException.ThrowIfNull(fields);
    using var ms = new MemoryStream();

    // ESC % G  → "UTF-8" per ISO 2022. Writers that don't look at 1:90 get
    // UTF-8 anyway; aware readers know to decode accordingly.
    WriteDataSet(ms, RecordEnvelope, DsCodedCharacterSet, new byte[] { 0x1B, 0x25, 0x47 });

    WriteString(ms, DsObjectName, fields.ObjectName);
    WriteString(ms, DsTransmissionReference, fields.TransmissionReference);
    WriteString(ms, DsDescriptionWriter, fields.DescriptionWriter);
    WriteString(ms, DsCreatorJobTitle, fields.CreatorJobTitle);
    WriteString(ms, DsInstructions, fields.Instructions);
    WriteString(ms, DsDateCreated, fields.DateCreatedYyyyMmDd);
    WriteString(ms, DsTimeCreated, fields.TimeCreatedHhMmSsZz);
    if (fields.Keywords is { } kws)
      foreach (var kw in kws)
        WriteString(ms, DsKeywords, kw);
    WriteString(ms, DsByLine, fields.ByLine);
    WriteString(ms, DsCity, fields.City);
    WriteString(ms, DsSubLocation, fields.SubLocation);
    WriteString(ms, DsProvinceState, fields.ProvinceState);
    WriteString(ms, DsCountryCode, fields.CountryCode);
    WriteString(ms, DsCountryName, fields.CountryName);
    WriteString(ms, DsHeadline, fields.Headline);
    WriteString(ms, DsCredit, fields.Credit);
    WriteString(ms, DsSource, fields.Source);
    WriteString(ms, DsCopyrightNotice, fields.CopyrightNotice);
    WriteString(ms, DsCaption, fields.Caption);

    return ms.ToArray();
  }

  /// <summary>
  /// Decodes a raw IPTC payload back into typed fields. Skips unknown
  /// datasets silently so future tag additions don't break older readers.
  /// </summary>
  public static IptcFields Decode(ReadOnlySpan<byte> payload) {
    string? title = null, caption = null, city = null, subLocation = null;
    string? state = null, countryCode = null, countryName = null;
    string? instructions = null, dateCreated = null, timeCreated = null;
    string? byLine = null, headline = null, credit = null, source = null, copyrightNotice = null;
    string? descriptionWriter = null, transmissionReference = null, creatorJobTitle = null;
    var keywords = new List<string>();

    var i = 0;
    while (i + 5 <= payload.Length) {
      if (payload[i] != TagMarker)
        break;
      var record = payload[i + 1];
      var dataset = payload[i + 2];
      var length = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(i + 3, 2));
      i += 5;
      if (i + length > payload.Length)
        break;
      var data = payload.Slice(i, length);
      i += length;

      if (record != RecordApplication)
        continue;

      var text = Encoding.UTF8.GetString(data);
      switch (dataset) {
        case DsObjectName:             title = text; break;
        case DsTransmissionReference:  transmissionReference = text; break;
        case DsDescriptionWriter:      descriptionWriter = text; break;
        case DsCreatorJobTitle:        creatorJobTitle = text; break;
        case DsInstructions:    instructions = text; break;
        case DsDateCreated:     dateCreated = text; break;
        case DsTimeCreated:     timeCreated = text; break;
        case DsKeywords:        keywords.Add(text); break;
        case DsByLine:          byLine = text; break;
        case DsCity:            city = text; break;
        case DsSubLocation:     subLocation = text; break;
        case DsProvinceState:   state = text; break;
        case DsCountryCode:     countryCode = text; break;
        case DsCountryName:     countryName = text; break;
        case DsHeadline:        headline = text; break;
        case DsCredit:          credit = text; break;
        case DsSource:          source = text; break;
        case DsCopyrightNotice: copyrightNotice = text; break;
        case DsCaption:         caption = text; break;
      }
    }

    return new IptcFields {
      ObjectName = title,
      Caption = caption,
      City = city,
      SubLocation = subLocation,
      ProvinceState = state,
      CountryCode = countryCode,
      CountryName = countryName,
      Keywords = keywords.Count > 0 ? keywords : null,
      Instructions = instructions,
      DateCreatedYyyyMmDd = dateCreated,
      TimeCreatedHhMmSsZz = timeCreated,
      ByLine = byLine,
      Headline = headline,
      Credit = credit,
      Source = source,
      CopyrightNotice = copyrightNotice,
      DescriptionWriter = descriptionWriter,
      TransmissionReference = transmissionReference,
      CreatorJobTitle = creatorJobTitle
    };
  }

  private static void WriteString(Stream ms, byte dataset, string? value) {
    if (string.IsNullOrEmpty(value))
      return;
    var bytes = Encoding.UTF8.GetBytes(value);
    WriteDataSet(ms, RecordApplication, dataset, bytes);
  }

  private static void WriteDataSet(Stream ms, byte record, byte dataset, byte[] data) {
    // IIM limits a dataset to 32 KB. Longer values technically need an
    // extended-tag form; for the fields we support this never trips.
    if (data.Length > 32_767)
      throw new InvalidDataException($"IPTC dataset {record}:{dataset} exceeds 32 KB.");

    Span<byte> header = stackalloc byte[5];
    header[0] = TagMarker;
    header[1] = record;
    header[2] = dataset;
    BinaryPrimitives.WriteUInt16BigEndian(header.Slice(3, 2), (ushort)data.Length);
    ms.Write(header);
    ms.Write(data);
  }
}
