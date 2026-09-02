using System;

namespace OggVorbisEncoder.Setup;

/// <summary>
/// Represents a code book.
/// </summary>
public class CodeBook
{
    /// <summary>
    /// Initializes a new instance of <see cref="CodeBook"/>.
    /// </summary>
    public CodeBook(
        int dimensions,
        int entries,
        int usedEntries,
        IStaticCodeBook staticBook,
        float[] valueList,
        uint[] codeList,
        int[] decIndex,
        byte[] decCodeLengths,
        uint[] decFirstTable,
        int decFirstTableN,
        int decMaxLength,
        int quantValues,
        int minVal,
        int delta)
    {
        Dimensions = dimensions;
        Entries = entries;
        UsedEntries = usedEntries;
        StaticBook = staticBook;
        ValueList = valueList;
        CodeList = codeList;
        DecIndex = decIndex;
        DecCodeLengths = decCodeLengths;
        DecFirstTable = decFirstTable;
        DecFirstTableN = decFirstTableN;
        DecMaxLength = decMaxLength;
        QuantValues = quantValues;
        MinVal = minVal;
        Delta = delta;
    }

    /// <summary>
    ///     codebook dimensions (elements per vector)
    /// </summary>
    public int Dimensions { get; }

    /// <summary>
    ///     codebook entries
    /// </summary>
    public int Entries { get; }

    /// <summary>
    ///     populated codebook entries
    /// </summary>
    public int UsedEntries { get; }

    /// <summary>
    /// Gets the static book.
    /// </summary>
    public IStaticCodeBook StaticBook { get; }

    /* for encode, the below are entry-ordered, fully populated */
    /* for decode, the below are ordered by bitreversed codeword and only
       used entries are populated */

    /// <summary>
    ///     list of dim*entries actual entry values
    /// </summary>
    public float[] ValueList { get; }

    /// <summary>
    ///     list of bitstream codewords for each entry
    /// </summary>
    public uint[] CodeList { get; }

    /// <summary>
    ///     only used if sparseness collapsed
    /// </summary>
    public int[] DecIndex { get; }

    /// <summary>
    /// Gets the dec code lengths.
    /// </summary>
    public byte[] DecCodeLengths { get; }


    /// <summary>
    /// Gets the dec first table.
    /// </summary>
    public uint[] DecFirstTable { get; }

    /// <summary>
    /// Gets the dec first table n.
    /// </summary>
    public int DecFirstTableN { get; }

    /// <summary>
    /// Gets the dec max length.
    /// </summary>
    public int DecMaxLength { get; }

    /* The current encoder uses only centered, integer-only lattice books. */
    /// <summary>
    /// Gets the quant values.
    /// </summary>
    public int QuantValues { get; }
    /// <summary>
    /// Gets the min val.
    /// </summary>
    public int MinVal { get; }
    /// <summary>
    /// Gets the delta.
    /// </summary>
    public int Delta { get; }

    /// <summary>
    /// Performs the init encode operation.
    /// </summary>
    public static CodeBook InitEncode(IStaticCodeBook source)
    {
        return new CodeBook(
            source.Dimensions,
            source.LengthList.Length,
            source.LengthList.Length,
            source,
            null,
            Encoding.MakeWords(source.LengthList, 0),
            null,
            null,
            null,
            0,
            0,
            source.GetQuantVals(),
            (int)Math.Round(Encoding.UnpackFloat(source.QuantMin)),
            (int)Math.Round(Encoding.UnpackFloat(source.QuantDelta))
        );
    }
}
