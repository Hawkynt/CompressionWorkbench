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
    /// <summary>
  /// Gets or sets the object name.
  /// </summary>
public string? ObjectName { get; init; }          // 2:5  — equivalent to XMP Title
    /// <summary>
  /// Gets or sets the instructions.
  /// </summary>
public string? Instructions { get; init; }        // 2:40 — photoshop:Instructions
    /// <summary>
  /// Gets or sets the keywords.
  /// </summary>
public IReadOnlyList<string>? Keywords { get; init; }  // 2:25 — repeatable
    /// <summary>
  /// Gets or sets the date created yyyy mm dd.
  /// </summary>
public string? DateCreatedYyyyMmDd { get; init; } // 2:55 — CCYYMMDD
    /// <summary>
  /// Gets or sets the time created hh mm ss zz.
  /// </summary>
public string? TimeCreatedHhMmSsZz { get; init; } // 2:60 — HHMMSS±HHMM
    /// <summary>
  /// Gets or sets the by line.
  /// </summary>
public string? ByLine { get; init; }              // 2:80 — dc:creator
    /// <summary>
  /// Gets or sets the city.
  /// </summary>
public string? City { get; init; }                // 2:90
    /// <summary>
  /// Gets or sets the sub location.
  /// </summary>
public string? SubLocation { get; init; }         // 2:92
    /// <summary>
  /// Gets or sets the province state.
  /// </summary>
public string? ProvinceState { get; init; }       // 2:95
    /// <summary>
  /// Gets or sets the country code.
  /// </summary>
public string? CountryCode { get; init; }         // 2:100
    /// <summary>
  /// Gets or sets the country name.
  /// </summary>
public string? CountryName { get; init; }         // 2:101
    /// <summary>
  /// Gets or sets the headline.
  /// </summary>
public string? Headline { get; init; }            // 2:105 — photoshop:Headline
    /// <summary>
  /// Gets or sets the credit.
  /// </summary>
public string? Credit { get; init; }              // 2:110 — photoshop:Credit
    /// <summary>
  /// Gets or sets the source.
  /// </summary>
public string? Source { get; init; }              // 2:115 — photoshop:Source
    /// <summary>
  /// Gets or sets the caption.
  /// </summary>
public string? Caption { get; init; }             // 2:120
    /// <summary>
  /// Gets or sets the copyright notice.
  /// </summary>
public string? CopyrightNotice { get; init; }     // 2:116
    /// <summary>
  /// Gets or sets the description writer.
  /// </summary>
public string? DescriptionWriter { get; init; }   // 2:122
    /// <summary>
  /// Gets or sets the transmission reference.
  /// </summary>
public string? TransmissionReference { get; init; } // 2:103 — photoshop:TransmissionReference
    /// <summary>
  /// Gets or sets the creator job title.
  /// </summary>
public string? CreatorJobTitle { get; init; }     // 2:85 — Iptc4xmpCore:CiJobtitle — dc:rights

    /// <summary>
  /// Gets a value indicating whether is empty.
  /// </summary>
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

    /// <summary>
  /// Defines the ds coded character set constant value.
  /// </summary>
public const byte DsCodedCharacterSet = 90;  // record 1
    /// <summary>
  /// Defines the ds object name constant value.
  /// </summary>
public const byte DsObjectName = 5;
    /// <summary>
  /// Defines the ds transmission reference constant value.
  /// </summary>
public const byte DsTransmissionReference = 103;
    /// <summary>
  /// Defines the ds description writer constant value.
  /// </summary>
public const byte DsDescriptionWriter = 122;
    /// <summary>
  /// Defines the ds creator job title constant value.
  /// </summary>
public const byte DsCreatorJobTitle = 85;
    /// <summary>
  /// Defines the ds instructions constant value.
  /// </summary>
public const byte DsInstructions = 40;
    /// <summary>
  /// Defines the ds date created constant value.
  /// </summary>
public const byte DsDateCreated = 55;
    /// <summary>
  /// Defines the ds time created constant value.
  /// </summary>
public const byte DsTimeCreated = 60;
    /// <summary>
  /// Defines the ds keywords constant value.
  /// </summary>
public const byte DsKeywords = 25;
    /// <summary>
  /// Defines the ds by line constant value.
  /// </summary>
public const byte DsByLine = 80;
    /// <summary>
  /// Defines the ds city constant value.
  /// </summary>
public const byte DsCity = 90;
    /// <summary>
  /// Defines the ds sub location constant value.
  /// </summary>
public const byte DsSubLocation = 92;
    /// <summary>
  /// Defines the ds province state constant value.
  /// </summary>
public const byte DsProvinceState = 95;
    /// <summary>
  /// Defines the ds country code constant value.
  /// </summary>
public const byte DsCountryCode = 100;
    /// <summary>
  /// Defines the ds country name constant value.
  /// </summary>
public const byte DsCountryName = 101;
    /// <summary>
  /// Defines the ds headline constant value.
  /// </summary>
public const byte DsHeadline = 105;
    /// <summary>
  /// Defines the ds credit constant value.
  /// </summary>
public const byte DsCredit = 110;
    /// <summary>
  /// Defines the ds source constant value.
  /// </summary>
public const byte DsSource = 115;
    /// <summary>
  /// Defines the ds copyright notice constant value.
  /// </summary>
public const byte DsCopyrightNotice = 116;
    /// <summary>
  /// Defines the ds caption constant value.
  /// </summary>
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
