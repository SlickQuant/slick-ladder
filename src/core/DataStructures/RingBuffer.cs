using System.Runtime.CompilerServices;

namespace SlickLadder.Core.DataStructures;

/// <summary>
/// Lock-free single-producer single-consumer (SPSC) ring buffer.
/// Optimized for ultra-low latency with pre-allocated memory.
/// </summary>
/// <typeparam name="T">Element type (must be value type for best performance)</typeparam>
public class RingBuffer<T> where T : struct
{
    private readonly T[] _buffer;
    private readonly int _capacity;
    private readonly int _mask;

    // Padding to prevent false sharing between head and tail
    private long _headPadding0, _headPadding1, _headPadding2, _headPadding3;
    private long _headPadding4, _headPadding5, _headPadding6, _headPadding7;

    private volatile int _head;

    private long _tailPadding0, _tailPadding1, _tailPadding2, _tailPadding3;
    private long _tailPadding4, _tailPadding5, _tailPadding6, _tailPadding7;

    private volatile int _tail;

    /// <summary>
    /// Current number of elements in the buffer (approximate, may be stale)
    /// </summary>
    public int Count => (_tail - _head) & _mask;

    /// <summary>
    /// Whether the buffer is empty (approximate)
    /// </summary>
    public bool IsEmpty => _head == _tail;

    /// <summary>
    /// Whether the buffer is full (approximate)
    /// </summary>
    public bool IsFull => Count == _capacity - 1;

    /// <summary>
    /// Maximum capacity of the buffer
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Create a ring buffer with specified capacity (must be power of 2)
    /// </summary>
    public RingBuffer(int capacity)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException("Capacity must be a power of 2", nameof(capacity));
        }

        _capacity = capacity;
        _mask = capacity - 1;
        _buffer = new T[capacity];
        _head = 0;
        _tail = 0;
    }

    /// <summary>
    /// Try to write an element to the buffer (producer side).
    /// Returns true if successful, false if buffer is full.
    /// Lock-free, wait-free operation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(T item)
    {
        var currentTail = _tail;
        var nextTail = (currentTail + 1) & _mask;

        // Check if buffer is full
        if (nextTail == _head)
        {
            return false;
        }

        // Write the item
        _buffer[currentTail] = item;

        // Update tail (visible to consumer)
        Thread.MemoryBarrier(); // Ensure write completes before tail update
        _tail = nextTail;

        return true;
    }

    /// <summary>
    /// Try to read an element from the buffer (consumer side).
    /// Returns true if successful, false if buffer is empty.
    /// Lock-free, wait-free operation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRead(out T item)
    {
        var currentHead = _head;

        // Check if buffer is empty
        if (currentHead == _tail)
        {
            item = default;
            return false;
        }

        // Read the item
        item = _buffer[currentHead];

        // Update head (visible to producer)
        Thread.MemoryBarrier(); // Ensure read completes before head update
        _head = (currentHead + 1) & _mask;

        return true;
    }

    /// <summary>
    /// Peek at the next element without removing it.
    /// Returns true if successful, false if buffer is empty.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPeek(out T item)
    {
        var currentHead = _head;

        if (currentHead == _tail)
        {
            item = default;
            return false;
        }

        item = _buffer[currentHead];
        return true;
    }

    /// <summary>
    /// Clear all elements from the buffer.
    /// WARNING: Not thread-safe! Only call when no concurrent access.
    /// </summary>
    public void Clear()
    {
        _head = 0;
        _tail = 0;
        Array.Clear(_buffer, 0, _buffer.Length);
    }

    /// <summary>
    /// Write multiple elements in batch.
    /// Returns the number of elements actually written.
    /// More efficient than multiple TryWrite calls.
    /// </summary>
    public int WriteBatch(ReadOnlySpan<T> items)
    {
        var written = 0;
        var currentTail = _tail;

        for (int i = 0; i < items.Length; i++)
        {
            var nextTail = (currentTail + 1) & _mask;

            // Check if buffer is full
            if (nextTail == _head)
            {
                break;
            }

            _buffer[currentTail] = items[i];
            currentTail = nextTail;
            written++;
        }

        if (written > 0)
        {
            Thread.MemoryBarrier();
            _tail = currentTail;
        }

        return written;
    }

    /// <summary>
    /// Read multiple elements in batch.
    /// Returns the number of elements actually read.
    /// More efficient than multiple TryRead calls.
    /// </summary>
    public int ReadBatch(Span<T> destination)
    {
        var read = 0;
        var currentHead = _head;

        for (int i = 0; i < destination.Length; i++)
        {
            // Check if buffer is empty
            if (currentHead == _tail)
            {
                break;
            }

            destination[i] = _buffer[currentHead];
            currentHead = (currentHead + 1) & _mask;
            read++;
        }

        if (read > 0)
        {
            Thread.MemoryBarrier();
            _head = currentHead;
        }

        return read;
    }
}
