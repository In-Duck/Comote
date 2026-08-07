using System;
using System.Security.Cryptography;

namespace Viewer;

internal sealed class ConnectionGenerationState
{
    private long _current = CreateInitialGeneration();
    private long _answerGeneration;
    private long _connectedGeneration;
    private long _terminatedGeneration;

    public long Current => _current;

    public long BeginNew()
    {
        _current = checked(_current + 1);
        _answerGeneration = 0;
        _connectedGeneration = 0;
        _terminatedGeneration = 0;
        return _current;
    }

    public bool IsCurrent(long generation) =>
        generation == _current &&
        generation != 0 &&
        _terminatedGeneration != generation;

    public bool TryAcceptAnswer(long generation)
    {
        if (!IsCurrent(generation) ||
            _answerGeneration == generation)
        {
            return false;
        }

        _answerGeneration = generation;
        return true;
    }

    public bool MarkConnected(long generation)
    {
        if (!IsCurrent(generation))
        {
            return false;
        }

        _connectedGeneration = generation;
        return true;
    }

    public bool IsConnected(long generation) =>
        IsCurrent(generation) &&
        _connectedGeneration == generation;

    public bool Terminate(long generation)
    {
        if (!IsCurrent(generation))
        {
            return false;
        }

        _terminatedGeneration = generation;
        _connectedGeneration = 0;
        return true;
    }

    private static long CreateInitialGeneration()
    {
        while (true)
        {
            long candidate =
                BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) &
                (long.MaxValue >> 1);
            if (candidate > 0)
            {
                return candidate;
            }
        }
    }
}
