namespace Comote.Input;

public sealed class HidKeyboardState
{
    private readonly SortedSet<byte> _keys = [];

    public byte Modifiers { get; private set; }

    public IReadOnlyCollection<byte> Keys => _keys;

    public bool TrySetKey(
        HidKey key,
        bool isDown,
        out string? error)
    {
        error = null;
        if (key.IsModifier)
        {
            var mask = key.ModifierMask;
            Modifiers = isDown
                ? (byte)(Modifiers | mask)
                : (byte)(Modifiers & ~mask);
            return true;
        }

        if (key.Usage == 0)
        {
            error = "The key has no HID usage.";
            return false;
        }

        if (isDown)
        {
            if (_keys.Contains(key.Usage))
            {
                return true;
            }
            if (_keys.Count >= 6)
            {
                error = "The six-key rollover limit was reached.";
                return false;
            }
            _keys.Add(key.Usage);
        }
        else
        {
            _keys.Remove(key.Usage);
        }
        return true;
    }

    public byte[] GetOrderedKeys()
    {
        var result = new byte[6];
        _keys.CopyTo(result);
        return result;
    }

    public HidKeyboardSnapshot Capture() =>
        new(Modifiers, [.. _keys]);

    public void Restore(HidKeyboardSnapshot snapshot)
    {
        Modifiers = snapshot.Modifiers;
        _keys.Clear();
        foreach (var usage in snapshot.Keys)
        {
            _keys.Add(usage);
        }
    }

    public void Clear()
    {
        Modifiers = 0;
        _keys.Clear();
    }
}

public readonly record struct HidKeyboardSnapshot(
    byte Modifiers,
    byte[] Keys);

public readonly record struct HidKey(
    byte Usage,
    byte ModifierMask)
{
    public bool IsModifier => ModifierMask != 0;
}
