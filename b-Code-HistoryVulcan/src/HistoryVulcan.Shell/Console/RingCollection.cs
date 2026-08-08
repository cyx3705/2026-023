using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;

namespace HistoryVulcan.Shell.Console;

/// <summary>
/// 控制台滚动缓冲专用集合(C-05):
/// 追加发单条 Add 通知(虚拟化列表增量更新);
/// 超上限的头部裁剪以“偏移量 + 定期压实”摊销为 O(1),并发 Reset 通知。
/// 仅供 UI 线程使用。
/// </summary>
public sealed class RingCollection<T> : IList, IReadOnlyList<T>, INotifyCollectionChanged, INotifyPropertyChanged
{
    private readonly List<T> _items = new();
    private int _head;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Count => _items.Count - _head;

    public T this[int index] => _items[_head + index];

    public void Add(T item)
    {
        _items.Add(item);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        CollectionChanged?.Invoke(
            this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, Count - 1));
    }

    /// <summary>把总量裁剪到 capacity(丢弃最旧);发生裁剪时发 Reset。</summary>
    public void TrimTo(int capacity)
    {
        var excess = Count - capacity;
        if (excess <= 0)
            return;

        _head += excess;
        if (_head > 16_384)
        {
            _items.RemoveRange(0, _head);
            _head = 0;
        }

        RaiseReset();
    }

    public void Clear()
    {
        if (Count == 0)
            return;
        _items.Clear();
        _head = 0;
        RaiseReset();
    }

    /// <summary>当前内容快照(导出用)。</summary>
    public List<T> ToList()
    {
        var list = new List<T>(Count);
        for (var i = 0; i < Count; i++)
            list.Add(this[i]);
        return list;
    }

    private void RaiseReset()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        CollectionChanged?.Invoke(
            this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ---------------------------------------------------------------- IList(WPF 虚拟化需要)

    bool IList.IsFixedSize => false;

    bool IList.IsReadOnly => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => Clear();

    bool IList.Contains(object? value) => value is T t && _items.IndexOf(t, _head) >= 0;

    int IList.IndexOf(object? value)
    {
        if (value is not T t)
            return -1;
        var i = _items.IndexOf(t, _head);
        return i < 0 ? -1 : i - _head;
    }

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    void ICollection.CopyTo(Array array, int index)
    {
        for (var i = 0; i < Count; i++)
            array.SetValue(this[i], index + i);
    }
}
