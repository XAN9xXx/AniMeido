using System.Text;

namespace AniMeido.Plugin.AI.Services;

internal sealed class StreamingTextBatcher
{
    private readonly StringBuilder _buffer = new();
    private int _drainedLength;

    public int Length => _buffer.Length;

    public void Append(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _buffer.Append(value);
        }
    }

    public string Drain()
    {
        if (_drainedLength >= _buffer.Length)
        {
            return string.Empty;
        }

        var value = _buffer.ToString(
            _drainedLength,
            _buffer.Length - _drainedLength);
        _drainedLength = _buffer.Length;
        return value;
    }
}
