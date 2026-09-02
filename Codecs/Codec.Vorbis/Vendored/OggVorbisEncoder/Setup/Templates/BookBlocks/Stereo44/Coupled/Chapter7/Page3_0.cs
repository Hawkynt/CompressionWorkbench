namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter7;

/// <summary>
/// Represents a page 3 0.
/// </summary>
public class Page3_0 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         2, 4, 4, 5, 5, 7, 7, 9, 9, 0, 4, 4, 6, 6, 7, 7,
         9, 9, 0, 4, 4, 6, 6, 7, 7, 9, 9, 0, 5, 5, 6, 6,
         8, 8,10,10, 0, 0, 0, 6, 6, 8, 8,10,10, 0, 0, 0,
         7, 7, 9, 9,10,10, 0, 0, 0, 7, 7, 8, 8,10,10, 0,
         0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
         0,
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
