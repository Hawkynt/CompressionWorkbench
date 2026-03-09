namespace Compression.Core.DataStructures;

/// <summary>
/// A binary min-heap for use in Huffman tree construction and similar algorithms.
/// </summary>
/// <typeparam name="T">The element type. Must implement <see cref="IComparable{T}"/>.</typeparam>
public sealed class MinHeap<T> where T : IComparable<T> {
  private readonly List<T> _items = [];

  /// <summary>
  /// Gets the number of elements in the heap.
  /// </summary>
  public int Count => this._items.Count;

  /// <summary>
  /// Inserts an item into the heap.
  /// </summary>
  /// <param name="item">The item to insert.</param>
  public void Insert(T item) {
    this._items.Add(item);
    SiftUp(this._items.Count - 1);
  }

  /// <summary>
  /// Removes and returns the minimum element.
  /// </summary>
  /// <returns>The minimum element.</returns>
  /// <exception cref="InvalidOperationException">The heap is empty.</exception>
  public T ExtractMin() {
    if (this._items.Count == 0)
      throw new InvalidOperationException("Heap is empty.");

    T min = this._items[0];
    int last = this._items.Count - 1;
    this._items[0] = this._items[last];
    this._items.RemoveAt(last);

    if (this._items.Count > 0)
      SiftDown(0);

    return min;
  }

  /// <summary>
  /// Returns the minimum element without removing it.
  /// </summary>
  /// <returns>The minimum element.</returns>
  /// <exception cref="InvalidOperationException">The heap is empty.</exception>
  public T Peek() {
    if (this._items.Count == 0)
      throw new InvalidOperationException("Heap is empty.");

    return this._items[0];
  }

  private void SiftUp(int index) {
    while (index > 0) {
      int parent = (index - 1) / 2;
      if (this._items[index].CompareTo(this._items[parent]) < 0) {
        (this._items[index], this._items[parent]) = (this._items[parent], this._items[index]);
        index = parent;
      }
      else
        break;
    }
  }

  private void SiftDown(int index) {
    int count = this._items.Count;
    while (true) {
      int left = 2 * index + 1;
      int right = 2 * index + 2;
      int smallest = index;

      if (left < count && this._items[left].CompareTo(this._items[smallest]) < 0)
        smallest = left;
      if (right < count && this._items[right].CompareTo(this._items[smallest]) < 0)
        smallest = right;

      if (smallest != index) {
        (this._items[index], this._items[smallest]) = (this._items[smallest], this._items[index]);
        index = smallest;
      }
      else
        break;
    }
  }
}
