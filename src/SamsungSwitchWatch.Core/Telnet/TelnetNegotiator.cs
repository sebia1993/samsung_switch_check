namespace SamsungSwitchWatch.Core.Telnet;

internal sealed class TelnetNegotiator
{
    private const byte Naws = 31;
    private const byte Se = 240;
    private const byte Sb = 250;
    private const byte Will = 251;
    private const byte Wont = 252;
    private const byte Do = 253;
    private const byte Dont = 254;
    private const byte Iac = 255;

    private State _state;
    private byte _verb;
    private int _negotiationBytesWithoutText;
    private readonly ushort _terminalWidth;
    private readonly ushort _terminalHeight;
    private bool _nawsEnabled;

    public TelnetNegotiator(ushort terminalWidth, ushort terminalHeight)
    {
        if (terminalWidth == 0 || terminalHeight == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(terminalWidth), "Telnet terminal dimensions must be positive.");
        }

        _terminalWidth = terminalWidth;
        _terminalHeight = terminalHeight;
    }

    public TelnetFrame Process(ReadOnlySpan<byte> input, int maximumNegotiationBytesWithoutText)
    {
        var text = new List<byte>(input.Length);
        var responses = new List<byte>();

        foreach (var value in input)
        {
            switch (_state)
            {
                case State.Data:
                    if (value == Iac)
                    {
                        _state = State.Iac;
                        _negotiationBytesWithoutText++;
                    }
                    else
                    {
                        text.Add(value);
                        _negotiationBytesWithoutText = 0;
                    }

                    break;

                case State.Iac:
                    _negotiationBytesWithoutText++;
                    if (value == Iac)
                    {
                        text.Add(Iac);
                        _state = State.Data;
                        _negotiationBytesWithoutText = 0;
                    }
                    else if (value is Will or Wont or Do or Dont)
                    {
                        _verb = value;
                        _state = State.Option;
                    }
                    else if (value == Sb)
                    {
                        _state = State.SubNegotiation;
                    }
                    else
                    {
                        _state = State.Data;
                    }

                    break;

                case State.Option:
                    _negotiationBytesWithoutText++;
                    if (_verb == Do)
                    {
                        if (value == Naws)
                        {
                            if (!_nawsEnabled)
                            {
                                responses.AddRange([Iac, Will, Naws]);
                                _nawsEnabled = true;
                            }

                            AddWindowSizeResponse(responses);
                        }
                        else
                        {
                            responses.AddRange([Iac, Wont, value]);
                        }
                    }
                    else if (_verb == Will)
                    {
                        responses.AddRange([Iac, Dont, value]);
                    }
                    else if (_verb == Dont && value == Naws && _nawsEnabled)
                    {
                        responses.AddRange([Iac, Wont, Naws]);
                        _nawsEnabled = false;
                    }

                    _state = State.Data;
                    break;

                case State.SubNegotiation:
                    _negotiationBytesWithoutText++;
                    if (value == Iac)
                    {
                        _state = State.SubNegotiationIac;
                    }

                    break;

                case State.SubNegotiationIac:
                    _negotiationBytesWithoutText++;
                    _state = value == Se ? State.Data : State.SubNegotiation;
                    break;
            }

            if (_negotiationBytesWithoutText > maximumNegotiationBytesWithoutText)
            {
                throw new TelnetProtocolException("Telnet option negotiation exceeded the safe control-byte limit.");
            }
        }

        return new TelnetFrame(text.ToArray(), responses.ToArray());
    }

    private void AddWindowSizeResponse(List<byte> responses)
    {
        responses.AddRange(
        [
            Iac,
            Sb,
            Naws,
            (byte)(_terminalWidth >> 8),
            (byte)_terminalWidth,
            (byte)(_terminalHeight >> 8),
            (byte)_terminalHeight,
            Iac,
            Se
        ]);
    }

    private enum State
    {
        Data,
        Iac,
        Option,
        SubNegotiation,
        SubNegotiationIac
    }
}

internal sealed record TelnetFrame(byte[] Text, byte[] Responses);

internal sealed class TelnetProtocolException(string message) : Exception(message);
