using System.Buffers.Binary;

namespace ShortP2P.LoRa.Link;

/// <summary>
///     Minimal hand-rolled protobuf subset for the Meshtastic serial API.
///     Field numbers follow meshtastic/protobufs (mesh.proto):
///     MeshPacket: from=1(fixed32), to=2(fixed32), channel=3, decoded=4(Data), id=6(fixed32), hop_limit=9.
///     Data: portnum=1(varint), payload=2(bytes). PortNum PRIVATE_APP = 256.
///     MyNodeInfo: my_node_num=1(varint). FromRadio: packet=2, my_info=3. ToRadio: packet=1, want_config_id=3.
///     LoRaConfig: use_preset=1, modem_preset=2, region=7, tx_power=10.
///     ModemPreset numbers follow config.proto (no generated enum in this repo):
///     LONG_FAST=0, MEDIUM_SLOW=3, MEDIUM_FAST=4, SHORT_SLOW=5, SHORT_FAST=6, LONG_MODERATE=7.
///     Serial framing: 0x94 0xC3 + uint16 BE length + protobuf payload.
/// </summary>
internal static class MeshtasticProto
{
    public const byte Start1 = 0x94;
    public const byte Start2 = 0xC3;
    public const int MaxPacketBytes = 512;

    public const uint PortNumPrivateApp = 256;
    public const uint PortNumAdminApp = 6;

    /// <summary>AdminMessage.ConfigType.LORA_CONFIG</summary>
    public const uint AdminConfigTypeLora = 5;

    public static byte[] BuildToRadioWantConfig(uint configId)
    {
        using var ms = new MemoryStream();
        WriteVarint(ms, (3 << 3) | 0); // want_config_id, varint
        WriteVarint(ms, configId);
        return ms.ToArray();
    }

    public static byte[] BuildToRadioPacket(uint to, uint packetId, byte[] payload, byte hopLimit) =>
        BuildToRadioPacket(to, packetId, payload, hopLimit, PortNumPrivateApp);

    public static byte[] BuildToRadioPacket(uint to, uint packetId, byte[] payload, byte hopLimit, uint portNum)
    {
        using var data = new MemoryStream();
        WriteVarint(data, (1 << 3) | 0); // portnum
        WriteVarint(data, portNum);
        WriteVarint(data, (2 << 3) | 2); // payload, length-delimited
        WriteVarint(data, (uint)payload.Length);
        data.Write(payload, 0, payload.Length);

        var dataBytes = data.ToArray();

        using var packet = new MemoryStream();
        WriteVarint(packet, (2 << 3) | 5); // to, fixed32
        WriteFixed32(packet, to);
        WriteVarint(packet, (4 << 3) | 2); // decoded
        WriteVarint(packet, (uint)dataBytes.Length);
        packet.Write(dataBytes, 0, dataBytes.Length);
        WriteVarint(packet, (6 << 3) | 5); // id, fixed32
        WriteFixed32(packet, packetId);
        WriteVarint(packet, (9 << 3) | 0); // hop_limit
        WriteVarint(packet, hopLimit);

        var packetBytes = packet.ToArray();

        using var toRadio = new MemoryStream();
        WriteVarint(toRadio, (1 << 3) | 2); // packet
        WriteVarint(toRadio, (uint)packetBytes.Length);
        toRadio.Write(packetBytes, 0, packetBytes.Length);
        return toRadio.ToArray();
    }

    /// <summary>AdminMessage.get_config_request = LORA_CONFIG (to obtain session_passkey on modern firmware).</summary>
    public static byte[] BuildAdminGetLoraConfig()
    {
        using var ms = new MemoryStream();
        WriteVarint(ms, (5 << 3) | 0); // get_config_request
        WriteVarint(ms, AdminConfigTypeLora);
        return ms.ToArray();
    }

    /// <summary>
    ///     AdminMessage.set_config with a full Config.lora blob (must preserve modem/region/channel).
    ///     Never send a LoRaConfig that contains only tx_power — firmware replaces the whole section.
    /// </summary>
    public static byte[] BuildAdminSetLoraConfig(ReadOnlySpan<byte> loraConfig, ReadOnlySpan<byte> sessionPasskey)
    {
        if (loraConfig.Length == 0)
            throw new ArgumentException("LoRaConfig bytes are required.", nameof(loraConfig));

        using var config = new MemoryStream();
        WriteVarint(config, (6 << 3) | 2); // Config.lora
        WriteVarint(config, (uint)loraConfig.Length);
        config.Write(loraConfig);

        var configBytes = config.ToArray();
        using var admin = new MemoryStream();
        WriteVarint(admin, (34 << 3) | 2); // set_config
        WriteVarint(admin, (uint)configBytes.Length);
        admin.Write(configBytes, 0, configBytes.Length);
        if (sessionPasskey.Length > 0)
        {
            WriteVarint(admin, (101 << 3) | 2); // session_passkey
            WriteVarint(admin, (uint)sessionPasskey.Length);
            admin.Write(sessionPasskey);
        }

        return admin.ToArray();
    }

    /// <summary>
    ///     Copy an existing LoRaConfig protobuf and replace/add tx_power (field 10).
    ///     Other fields (region, modem, channel_num, …) stay intact.
    /// </summary>
    public static byte[] PatchLoRaConfigTxPower(ReadOnlySpan<byte> loraConfig, int txPowerDbm)
    {
        using var ms = new MemoryStream(loraConfig.Length + 8);
        var o = 0;
        while (o < loraConfig.Length)
        {
            var fieldStart = o;
            if (!TryReadVarint(loraConfig, ref o, out var tag))
                break;
            var number = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            if (!TrySkipFieldValue(loraConfig, ref o, wire))
                break;
            if (number != 10)
                ms.Write(loraConfig[fieldStart..o]);
        }

        WriteVarint(ms, (10 << 3) | 0); // tx_power
        WriteVarint(ms, unchecked((uint)txPowerDbm));
        return ms.ToArray();
    }

    /// <summary>
    ///     Read use_preset (1), modem_preset (2), region (7), tx_power (10) and
    ///     override_duty_cycle (12) from a LoRaConfig blob.
    ///     Missing fields keep defaults: use_preset false, modem LONG_FAST (0), region UNSET (0),
    ///     tx_power 0, override_duty_cycle false. Duty-cycle percent comes from the region catalog.
    /// </summary>
    public static LoRaBoardConfigInfo ParseLoRaConfig(ReadOnlySpan<byte> loraConfig)
    {
        var usePreset = false;
        var modem = 0;
        var region = 0;
        var txPower = 0;
        var overrideDuty = false;
        if (loraConfig.Length > 0)
        {
            ReadFields(loraConfig, (number, wireType, varint, _) =>
            {
                if (wireType != 0)
                    return;
                switch (number)
                {
                    case 1:
                        usePreset = varint != 0;
                        break;
                    case 2:
                        modem = unchecked((int)varint);
                        break;
                    case 7:
                        region = unchecked((int)varint);
                        break;
                    case 10:
                        txPower = unchecked((int)varint);
                        break;
                    case 12:
                        overrideDuty = varint != 0;
                        break;
                }
            });
        }

        var regionCode = MeshtasticRegionCodes.CodeForProto(region);
        return new LoRaBoardConfigInfo(
            loraConfig.Length == 0 ? [] : loraConfig.ToArray(),
            region,
            regionCode,
            modem,
            MeshtasticRegionCodes.ModemPresetName(modem),
            txPower,
            usePreset,
            overrideDuty,
            LoRaBoardConfigInfo.DutyCyclePercentForRegion(regionCode));
    }

    /// <summary>
    ///     Copy an existing LoRaConfig and set use_preset (field 1) plus modem_preset (field 2).
    ///     Every other field (region, channel, tx_power, bandwidth, …) is kept.
    ///     <paramref name="modemPreset"/> must be one of the universal preset numbers
    ///     (<see cref="LoRaModemPresets"/>). An empty blob is rejected so a preset-only
    ///     set_config cannot wipe the section.
    /// </summary>
    public static byte[] PatchLoRaConfigModemPreset(ReadOnlySpan<byte> loraConfig, int modemPreset)
    {
        if (loraConfig.Length == 0)
            throw new ArgumentException("LoRaConfig bytes are required.", nameof(loraConfig));
        if (!global::ShortP2P.LoRa.LoRaModemPresets.IsAllowedProtoValue(modemPreset))
            throw new ArgumentOutOfRangeException(nameof(modemPreset), modemPreset,
                "Modem preset is not in the universal set (every region, current firmware).");

        using var ms = new MemoryStream(loraConfig.Length + 8);
        var o = 0;
        while (o < loraConfig.Length)
        {
            var fieldStart = o;
            if (!TryReadVarint(loraConfig, ref o, out var tag))
                break;
            var number = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            if (!TrySkipFieldValue(loraConfig, ref o, wire))
                break;
            if (number is not (1 or 2))
                ms.Write(loraConfig[fieldStart..o]);
        }

        WriteVarint(ms, (1 << 3) | 0); // use_preset
        WriteVarint(ms, 1);
        WriteVarint(ms, (2 << 3) | 0); // modem_preset
        WriteVarint(ms, unchecked((uint)modemPreset));
        return ms.ToArray();
    }

    /// <summary>Extract AdminMessage.session_passkey (field 101) when present.</summary>
    public static bool TryReadAdminSessionPasskey(ReadOnlySpan<byte> adminPayload, out byte[] passkey)
    {
        passkey = [];
        byte[]? found = null;
        ReadFields(adminPayload, (number, wireType, _, bytes) =>
        {
            if (number == 101 && wireType == 2 && bytes.Length > 0)
                found = bytes.ToArray();
        });
        if (found == null)
            return false;
        passkey = found;
        return true;
    }

    /// <summary>
    ///     AdminMessage.get_config_response (field 6) → Config.lora (field 6) raw bytes.
    /// </summary>
    public static bool TryReadAdminGetConfigResponseLora(ReadOnlySpan<byte> adminPayload, out byte[] loraConfig)
    {
        loraConfig = [];
        byte[]? configBytes = null;
        ReadFields(adminPayload, (number, wireType, _, bytes) =>
        {
            if (number == 6 && wireType == 2 && bytes.Length > 0)
                configBytes = bytes.ToArray();
        });
        if (configBytes == null)
            return false;

        byte[]? lora = null;
        ReadFields(configBytes, (number, wireType, _, bytes) =>
        {
            if (number == 6 && wireType == 2 && bytes.Length > 0)
                lora = bytes.ToArray();
        });
        if (lora == null)
            return false;
        loraConfig = lora;
        return true;
    }

    public static byte[] Frame(byte[] protobufPayload)
    {
        if (protobufPayload.Length > MaxPacketBytes)
            throw new InvalidOperationException($"Meshtastic frame too large: {protobufPayload.Length} bytes.");
        var framed = new byte[4 + protobufPayload.Length];
        framed[0] = Start1;
        framed[1] = Start2;
        BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(2, 2), (ushort)protobufPayload.Length);
        protobufPayload.CopyTo(framed, 4);
        return framed;
    }

    /// <summary>One parsed protobuf field. Bytes is valid only for length-delimited / fixed fields.</summary>
    internal delegate void ProtoFieldHandler(int number, int wireType, uint varint, ReadOnlySpan<byte> bytes);

    /// <summary>
    ///     Parse a FromRadio payload. Handles packet, my_info, node_info (field 4) and
    ///     config_complete_id (field 7) from the Meshtastic mesh protobufs.
    /// </summary>
    /// <param name="onSkippedPacket">
    ///     Fired for MeshPackets that are not delivered as PrivateApp (encrypted-only, other portnum, empty payload).
    ///     Useful to distinguish "no RF on serial" from "RF present but not PrivateApp".
    /// </param>
    public static void ReadFromRadio(ReadOnlySpan<byte> payload, Action<MeshLinkFrame>? onPacket,
        Action<uint>? onMyNodeNum, Action<MeshtasticRadioNode>? onNode = null, Action<uint>? onConfigComplete = null,
        Action<byte[]>? onAdminPayload = null,
        Action<uint, uint, uint?, string>? onSkippedPacket = null)
    {
        ReadFields(payload, (number, wireType, varint, bytes) =>
        {
            switch (number)
            {
                case 2 when wireType == 2:
                    TryReadMeshPacket(bytes, onPacket, onAdminPayload, onSkippedPacket);
                    break;
                case 3 when wireType == 2:
                    TryReadMyNodeInfo(bytes, onMyNodeNum);
                    break;
                case 4 when wireType == 2:
                    TryReadNodeInfo(bytes, onNode);
                    break;
                case 7 when wireType == 0:
                    onConfigComplete?.Invoke(varint);
                    break;
            }
        });
    }

    private static void TryReadMeshPacket(ReadOnlySpan<byte> payload, Action<MeshLinkFrame>? onPacket,
        Action<byte[]>? onAdminPayload = null, Action<uint, uint, uint?, string>? onSkipped = null)
    {
        uint from = 0, to = 0;
        byte[]? decoded = null;
        var hasEncrypted = false;

        ReadFields(payload, (number, wireType, _, bytes) =>
        {
            switch (number, wireType)
            {
                case (1, 5):
                    from = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                    break;
                case (2, 5):
                    to = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                    break;
                case (4, 2):
                    decoded = bytes.ToArray();
                    break;
                case (5, 2):
                    // MeshPacket.encrypted — board could not (or did not) decrypt for this client.
                    hasEncrypted = true;
                    break;
            }
        });

        if (decoded == null)
        {
            if (hasEncrypted)
                onSkipped?.Invoke(from, to, null, "encrypted-only (no decoded) — channel/PSK mismatch or not for us");
            return;
        }

        uint? portNumber = null;
        byte[]? data = null;
        ReadFields(decoded, (number, wireType, varint, bytes) =>
        {
            switch (number, wireType)
            {
                case (1, 0):
                    portNumber = varint;
                    break;
                case (2, 2):
                    data = bytes.ToArray();
                    break;
            }
        });

        if (data is not { Length: > 0 })
        {
            onSkipped?.Invoke(from, to, portNumber, "decoded Data with empty payload");
            return;
        }

        if (portNumber == PortNumAdminApp)
        {
            onAdminPayload?.Invoke(data);
            return;
        }

        if (portNumber == PortNumPrivateApp && from != 0)
        {
            onPacket?.Invoke(new MeshLinkFrame(from, to, data));
            return;
        }

        onSkipped?.Invoke(from, to, portNumber,
            portNumber == PortNumPrivateApp
                ? "PrivateApp with from=0"
                : $"portnum={portNumber?.ToString() ?? "missing"} (not PrivateApp {PortNumPrivateApp})");
    }

    private static void TryReadMyNodeInfo(ReadOnlySpan<byte> payload, Action<uint>? onMyNodeNum)
    {
        ReadFields(payload, (number, wireType, varint, _) =>
        {
            if (number == 1 && wireType == 0)
                onMyNodeNum?.Invoke(varint);
        });
    }

    /// <summary>NodeInfo: num=1, user=2 (User.id=1, long_name=2, short_name=3).</summary>
    private static void TryReadNodeInfo(ReadOnlySpan<byte> payload, Action<MeshtasticRadioNode>? onNode)
    {
        if (onNode == null)
            return;

        uint num = 0;
        var longName = "";
        var shortName = "";
        var userId = "";
        ReadFields(payload, (number, wireType, varint, bytes) =>
        {
            switch (number, wireType)
            {
                case (1, 0):
                    num = varint;
                    break;
                case (2, 2):
                    ReadFields(bytes, (n, w, _, b) =>
                    {
                        if (w != 2)
                            return;
                        var text = System.Text.Encoding.UTF8.GetString(b);
                        switch (n)
                        {
                            case 1:
                                userId = text;
                                break;
                            case 2:
                                longName = text;
                                break;
                            case 3:
                                shortName = text;
                                break;
                        }
                    });
                    break;
            }
        });

        if (num == 0 || num == 0xFFFFFFFF)
            return;
        onNode(new MeshtasticRadioNode(num, longName, shortName, userId));
    }

    /// <summary>Walk top-level protobuf fields. Unknown / malformed fields end the walk.</summary>
    internal static void ReadFields(ReadOnlySpan<byte> payload, ProtoFieldHandler handler)
    {
        var o = 0;
        while (o < payload.Length)
        {
            if (!TryReadVarint(payload, ref o, out var tag))
                return;
            var number = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            switch (wire)
            {
                case 0: // varint
                    if (!TryReadVarint(payload, ref o, out var v))
                        return;
                    handler(number, wire, v, default);
                    break;
                case 1: // 64-bit
                    if (o + 8 > payload.Length)
                        return;
                    handler(number, wire, 0, payload.Slice(o, 8));
                    o += 8;
                    break;
                case 2: // length-delimited
                    if (!TryReadVarint(payload, ref o, out var len) || o + len > payload.Length)
                        return;
                    handler(number, wire, 0, payload.Slice(o, (int)len));
                    o += (int)len;
                    break;
                case 5: // 32-bit
                    if (o + 4 > payload.Length)
                        return;
                    handler(number, wire, 0, payload.Slice(o, 4));
                    o += 4;
                    break;
                default:
                    return;
            }
        }
    }

    private static bool TrySkipFieldValue(ReadOnlySpan<byte> data, ref int offset, int wireType)
    {
        switch (wireType)
        {
            case 0:
                return TryReadVarint(data, ref offset, out _);
            case 1:
                if (offset + 8 > data.Length)
                    return false;
                offset += 8;
                return true;
            case 2:
                if (!TryReadVarint(data, ref offset, out var len) || offset + len > data.Length)
                    return false;
                offset += (int)len;
                return true;
            case 5:
                if (offset + 4 > data.Length)
                    return false;
                offset += 4;
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int offset, out uint value)
    {
        value = 0;
        var shift = 0;
        while (offset < data.Length && shift < 35)
        {
            var b = data[offset++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return true;
            shift += 7;
        }

        return false;
    }

    private static void WriteVarint(Stream stream, uint value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static void WriteFixed32(Stream stream, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        stream.Write(b);
    }
}
