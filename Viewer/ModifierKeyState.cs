namespace Viewer;

internal sealed class ModifierKeyState
{
    private bool _leftControl;
    private bool _rightControl;
    private bool _leftShift;
    private bool _rightShift;
    private bool _leftAlt;
    private bool _rightAlt;
    private bool _leftWindows;
    private bool _rightWindows;

    public bool Control => _leftControl || _rightControl;
    public bool Shift => _leftShift || _rightShift;
    public bool Alt => _leftAlt || _rightAlt;
    public bool Windows => _leftWindows || _rightWindows;

    public void Update(ushort virtualKey, bool isDown)
    {
        switch (virtualKey)
        {
            case 0xA2:
                _leftControl = isDown;
                break;
            case 0xA3:
                _rightControl = isDown;
                break;
            case 0xA0:
                _leftShift = isDown;
                break;
            case 0xA1:
                _rightShift = isDown;
                break;
            case 0xA4:
                _leftAlt = isDown;
                break;
            case 0xA5:
                _rightAlt = isDown;
                break;
            case 0x5B:
                _leftWindows = isDown;
                break;
            case 0x5C:
                _rightWindows = isDown;
                break;
        }
    }

    public void Reset()
    {
        _leftControl = false;
        _rightControl = false;
        _leftShift = false;
        _rightShift = false;
        _leftAlt = false;
        _rightAlt = false;
        _leftWindows = false;
        _rightWindows = false;
    }
}
