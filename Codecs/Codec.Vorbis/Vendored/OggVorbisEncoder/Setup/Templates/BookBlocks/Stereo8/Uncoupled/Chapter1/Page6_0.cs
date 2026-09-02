namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo8.Uncoupled.Chapter1;

/// <summary>
/// Represents a page 6 0.
/// </summary>
public class Page6_0 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         3, 4, 4, 6, 6, 7, 7, 9, 9, 4, 4, 5, 6, 6, 7, 7,
         9, 9, 4, 4, 4, 6, 6, 7, 7, 9, 9, 6, 6, 6, 7, 7,
         8, 8, 9, 9, 6, 6, 6, 7, 7, 8, 8, 9, 9, 7, 7, 7,
         8, 8, 8, 9,10,10, 7, 7, 7, 8, 8, 9, 8,10,10, 9,
         9, 9, 9, 9,10,10,10,10, 9, 9, 9, 9, 9,10,10,10,
        10,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -531628032;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1611661312;
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
