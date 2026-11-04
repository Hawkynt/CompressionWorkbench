namespace FileFormat.Mcm;

/// <summary>
/// The introsort of the SGI/HP STL lineage that the GCC runtime mcm was built with uses for
/// <c>std::sort</c>: median-of-three quicksort down to 16-element runs, heapsort past twice the
/// depth log2(n), then one insertion sort. It is not stable, so where mcm sorts words with equal
/// keys only the same sequence of compares and swaps reproduces its order.
/// </summary>
internal static class McmIntroSort {
  private const int Threshold = 16;

  /// <summary>Sorts <paramref name="items"/> ascending under <paramref name="less"/>.</summary>
  public static void Sort<T>(T[] items, Func<T, T, bool> less) {
    if (items.Length < 2)
      return;
    IntroLoop(items, 0, items.Length, 2 * Log2(items.Length), less);
    FinalInsertionSort(items, 0, items.Length, less);
  }

  /// <summary>Sorts the sequence read back to front, as <c>std::sort(v.rbegin(), v.rend(), less)</c> does.</summary>
  public static void SortReversed<T>(T[] items, Func<T, T, bool> less) {
    Array.Reverse(items);
    Sort(items, less);
    Array.Reverse(items);
  }

  private static int Log2(int n) => 31 - int.LeadingZeroCount(n);

  private static void IntroLoop<T>(T[] a, int first, int last, int depth, Func<T, T, bool> less) {
    while (last - first > Threshold) {
      if (depth == 0) {
        HeapSort(a, first, last, less);
        return;
      }
      --depth;
      var cut = PartitionPivot(a, first, last, less);
      IntroLoop(a, cut, last, depth, less);
      last = cut;
    }
  }

  private static int PartitionPivot<T>(T[] a, int first, int last, Func<T, T, bool> less) {
    var mid = first + (last - first) / 2;
    MoveMedianToFirst(a, first, first + 1, mid, last - 1, less);
    return UnguardedPartition(a, first + 1, last, first, less);
  }

  private static void MoveMedianToFirst<T>(T[] a, int result, int x, int y, int z, Func<T, T, bool> less) {
    if (less(a[x], a[y])) {
      if (less(a[y], a[z]))
        Swap(a, result, y);
      else if (less(a[x], a[z]))
        Swap(a, result, z);
      else
        Swap(a, result, x);
    } else if (less(a[x], a[z]))
      Swap(a, result, x);
    else if (less(a[y], a[z]))
      Swap(a, result, z);
    else
      Swap(a, result, y);
  }

  private static int UnguardedPartition<T>(T[] a, int first, int last, int pivot, Func<T, T, bool> less) {
    for (;;) {
      while (less(a[first], a[pivot]))
        ++first;
      --last;
      while (less(a[pivot], a[last]))
        --last;
      if (first >= last)
        return first;
      Swap(a, first, last);
      ++first;
    }
  }

  private static void FinalInsertionSort<T>(T[] a, int first, int last, Func<T, T, bool> less) {
    if (last - first > Threshold) {
      InsertionSort(a, first, first + Threshold, less);
      for (var i = first + Threshold; i < last; ++i)
        UnguardedLinearInsert(a, i, less);
    } else
      InsertionSort(a, first, last, less);
  }

  private static void InsertionSort<T>(T[] a, int first, int last, Func<T, T, bool> less) {
    if (first == last)
      return;
    for (var i = first + 1; i < last; ++i)
      if (less(a[i], a[first])) {
        var value = a[i];
        Array.Copy(a, first, a, first + 1, i - first);
        a[first] = value;
      } else
        UnguardedLinearInsert(a, i, less);
  }

  private static void UnguardedLinearInsert<T>(T[] a, int last, Func<T, T, bool> less) {
    var value = a[last];
    var next = last - 1;
    while (less(value, a[next])) {
      a[last] = a[next];
      last = next;
      --next;
    }
    a[last] = value;
  }

  private static void HeapSort<T>(T[] a, int first, int last, Func<T, T, bool> less) {
    var length = last - first;
    if (length >= 2)
      for (var parent = (length - 2) / 2; ; --parent) {
        AdjustHeap(a, first, parent, length, a[first + parent], less);
        if (parent == 0)
          break;
      }
    while (last - first > 1) {
      --last;
      var value = a[last];
      a[last] = a[first];
      AdjustHeap(a, first, 0, last - first, value, less);
    }
  }

  private static void AdjustHeap<T>(T[] a, int first, int hole, int length, T value, Func<T, T, bool> less) {
    var top = hole;
    var child = hole;
    while (child < (length - 1) / 2) {
      child = 2 * (child + 1);
      if (less(a[first + child], a[first + child - 1]))
        --child;
      a[first + hole] = a[first + child];
      hole = child;
    }
    if ((length & 1) == 0 && child == (length - 2) / 2) {
      child = 2 * (child + 1);
      a[first + hole] = a[first + child - 1];
      hole = child - 1;
    }
    var parent = (hole - 1) / 2;
    while (hole > top && less(a[first + parent], value)) {
      a[first + hole] = a[first + parent];
      hole = parent;
      parent = (hole - 1) / 2;
    }
    a[first + hole] = value;
  }

  private static void Swap<T>(T[] a, int i, int j) => (a[i], a[j]) = (a[j], a[i]);
}
