namespace SamsungSwitchWatch.Core.Telnet;

internal sealed class TelnetNegotiator
{
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
                        responses.AddRange([Iac, Wont, value]);
                    }
                    else if (_verb == Will)
                    {
                        responses.AddRange([Iac, Dont, value]);
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
