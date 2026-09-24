namespace ContosoPizza.Collector.Otlp;

/// <summary>
/// A minimal protobuf wire-format reader, enough for the OTLP messages this
/// collector accepts.
///
/// Hand-written rather than generated because there is no published .NET
/// package for the OTLP protos, and pulling in Grpc.Tools to run protoc at
/// build time would add a native toolchain dependency to a project whose whole
/// reason to exist is being small enough to drop onto a customer's server. The
/// wire format is four wire types and a varint; the risk is in the message
/// definitions, not in this.
///
/// Reading the wire format needs no schema: unknown fields are skipped by wire
/// type, so a newer OTLP release adding fields cannot break this, and the two
/// encodings of a repeated scalar (packed and not) are the only subtlety.
/// </summary>
internal ref struct ProtoReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public ProtoReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public readonly bool End => _pos >= _data.Length;

    public bool TryReadTag(out int fieldNumber, out WireType wireType)
    {
        fieldNumber = 0;
        wireType = WireType.Varint;

        if (End)
        {
            return false;
        }

        ulong tag = ReadVarint();

        if (tag == 0)
        {
            // Field number 0 is not legal; treating it as end-of-message rather
            // than looping is what stops a malformed body spinning a thread.
            return false;
        }

        fieldNumber = (int)(tag >> 3);
        wireType = (WireType)(int)(tag & 0x7);
        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        int shift = 0;

        while (shift < 64 && _pos < _data.Length)
        {
            byte b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }

        throw new InvalidDataException("Malformed varint");
    }

    public ReadOnlySpan<byte> ReadLengthDelimited()
    {
        int length = checked((int)ReadVarint());

        if (length < 0 || _pos + length > _data.Length)
        {
            throw new InvalidDataException("Length-delimited field runs past the end of the message");
        }

        var slice = _data.Slice(_pos, length);
        _pos += length;
        return slice;
    }

    public ulong ReadFixed64()
    {
        if (_pos + 8 > _data.Length)
        {
            throw new InvalidDataException("Truncated fixed64");
        }

        ulong value = BitConverter.ToUInt64(_data.Slice(_pos, 8));
        _pos += 8;
        return value;
    }

    public uint ReadFixed32()
    {
        if (_pos + 4 > _data.Length)
        {
            throw new InvalidDataException("Truncated fixed32");
        }

        uint value = BitConverter.ToUInt32(_data.Slice(_pos, 4));
        _pos += 4;
        return value;
    }

    public double ReadDouble() => BitConverter.UInt64BitsToDouble(ReadFixed64());

    public string ReadString() => System.Text.Encoding.UTF8.GetString(ReadLengthDelimited());

    /// <summary>Skip a field whose number we do not recognise.</summary>
    public void Skip(WireType wireType)
    {
        switch (wireType)
        {
            case WireType.Varint:
                ReadVarint();
                break;
            case WireType.Fixed64:
                ReadFixed64();
                break;
            case WireType.LengthDelimited:
                ReadLengthDelimited();
                break;
            case WireType.Fixed32:
                ReadFixed32();
                break;
            case WireType.StartGroup:
            case WireType.EndGroup:
                // Groups were removed from proto3 and no OTLP message uses
                // them. There is no length prefix to skip past, so carrying on
                // would mean reading the rest of the message at a wrong offset
                // and reporting plausible nonsense.
                throw new InvalidDataException("Group wire types are not supported");
            default:
                throw new InvalidDataException($"Unknown wire type {wireType}");
        }
    }
}

internal enum WireType
{
    Varint = 0,
    Fixed64 = 1,
    LengthDelimited = 2,
    StartGroup = 3,
    EndGroup = 4,
    Fixed32 = 5,
}
