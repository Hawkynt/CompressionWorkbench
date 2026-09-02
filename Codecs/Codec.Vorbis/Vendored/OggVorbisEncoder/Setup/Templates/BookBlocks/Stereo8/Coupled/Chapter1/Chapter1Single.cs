namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo8.Coupled.Chapter1;

/// <summary>
/// Represents a chapter 1 single.
/// </summary>
public class Chapter1Single : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         4, 6,18, 8,11, 8, 8, 9, 9,10, 4, 4,18, 5, 9, 5,
         6, 7, 8,10,18,18,18,18,17,17,17,17,17,17, 7, 5,
        17, 6,11, 6, 7, 8, 9,12,12, 9,17,12, 8, 8, 9,10,
        10,13, 7, 5,17, 6, 8, 4, 5, 6, 8,10, 6, 5,17, 6,
         8, 5, 4, 5, 7, 9, 7, 7,17, 8, 9, 6, 5, 5, 6, 8,
         8, 8,17, 9,11, 8, 6, 6, 6, 7, 9,10,17,12,12,10,
         9, 7, 7, 8,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)0;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = 0;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 0;
    /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 0;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = null;
}
