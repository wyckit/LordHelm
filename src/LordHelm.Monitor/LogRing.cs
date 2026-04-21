namespace LordHelm.Monitor;

public sealed class LogRing
{
    private readonly object _gate = new();
    private readonly string[] _buffer;
    private int _head;
    private int _count;

    public LogRing(int capacity = 512)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new string[capacity];
    }

    public void Append(string line)
    {
        lock (_gate)
        {
            _buffer[_head] = line;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            var result = new string[_count];
            var start = (_head - _count + _buffer.Length) % _buffer.Length;
            for (int i = 0; i < _count; i++)
                result[i] = _buffer[(start + i) % _buffer.Length];
            return result;
        }
    }
}
