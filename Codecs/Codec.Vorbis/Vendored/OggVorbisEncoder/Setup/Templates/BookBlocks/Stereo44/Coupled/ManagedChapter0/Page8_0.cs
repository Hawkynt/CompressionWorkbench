namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.ManagedChapter0;

/// <summary>
/// Represents a page 8 0.
/// </summary>
public class Page8_0 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         1, 3, 3,11,11,11,11,11,11, 3, 7, 6,11,11,11,11,
        11,11, 4, 8, 7,11,11,11,11,11,11,11,11,11,11,11,
        11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,
        11,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,
        12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,
        12,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -516186112;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1627103232;
    /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 4;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = {
        4,
        3,
        5,
        2,
        6,
        1,
        7,
        0,
        8,
    };
}
