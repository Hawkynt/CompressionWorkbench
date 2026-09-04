namespace OggVorbisEncoder.Setup;

/// <summary>
/// Represents a residue entry.
/// </summary>
public class ResidueEntry
{
    /// <summary>
    /// Initializes a new instance of <see cref="ResidueEntry"/>.
    /// </summary>
    public ResidueEntry(
        int begin,
        int end,
        int grouping,
        int partitions,
        int partitionValues,
        int groupBook,
        int[] secondStages,
        int[] bookList,
        int[] classMetric1,
        int[] classMetric2,
        ResidueType residueType)
    {
        Begin = begin;
        End = end;
        Grouping = grouping;
        Partitions = partitions;
        PartitionValues = partitionValues;
        GroupBook = groupBook;
        SecondStages = secondStages.ToFixedLength(64);
        BookList = bookList.ToFixedLength(512);
        ClassMetric1 = classMetric1.ToFixedLength(64);
        ClassMetric2 = classMetric2.ToFixedLength(64);
        ResidueType = residueType;
    }

    /// <summary>
    /// Gets the begin.
    /// </summary>
    public int Begin { get; }
    /// <summary>
    /// Gets or sets the end.
    /// </summary>
    public int End { get; set; }
    /// <summary>
    /// Gets the partitions.
    /// </summary>
    public int Partitions { get; }
    /// <summary>
    /// Gets the partition values.
    /// </summary>
    public int PartitionValues { get; }
    /// <summary>
    /// Gets or sets the group book.
    /// </summary>
    public int GroupBook { get; set; }
    /// <summary>
    /// Gets the second stages.
    /// </summary>
    public int[] SecondStages { get; }
    /// <summary>
    /// Gets the book list.
    /// </summary>
    public int[] BookList { get; }
    /// <summary>
    /// Gets the class metric 1.
    /// </summary>
    public int[] ClassMetric1 { get; }
    /// <summary>
    /// Gets the class metric 2.
    /// </summary>
    public int[] ClassMetric2 { get; }

    /// <summary>
    /// Gets the residue type.
    /// </summary>
    public ResidueType ResidueType { get; }
    /// <summary>
    /// Gets the grouping.
    /// </summary>
    public int Grouping { get; }

    /// <summary>
    /// Performs the clone operation. The array members are copied: the setup templates are
    /// process-wide singletons and <c>FillBooks</c> writes per-stream book ids into
    /// <see cref="SecondStages"/> and <see cref="BookList"/>, so sharing the arrays would leak one
    /// encoder's book numbering into the next stream that clones the same template.
    /// </summary>
    public ResidueEntry Clone(ResidueType residueTypeOverride, int groupingOverride)
        => new ResidueEntry(
            Begin,
            End,
            groupingOverride,
            Partitions,
            PartitionValues,
            GroupBook,
            (int[])SecondStages.Clone(),
            (int[])BookList.Clone(),
            (int[])ClassMetric1.Clone(),
            (int[])ClassMetric2.Clone(),
            residueTypeOverride);
}
