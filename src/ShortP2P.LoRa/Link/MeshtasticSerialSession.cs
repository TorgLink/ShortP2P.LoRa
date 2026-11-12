using System.IO;
using System.IO.Ports;
using Microsoft.Extensions.Logging;

namespace ShortP2P.LoRa.Link;

/// <summary>
///     Single-threaded serial engine for ONE Meshtastic board session (REQ-GATE-2).
///     Every member must be called from the gate radio thread; calls from any other thread throw
///     <see cref="InvalidOperationException" />. No locks are needed: the radio thread is the sole owner
///     of the <see cref="SerialPort" />, the node database and the parser buffer.
/// </summary>
internal sealed class MeshtasticSerialSession
{
    private const int AnnounceRetrySeconds = 1;
    private const int RefreshRetrySeconds = 2;
    private const int SerialHeaderBytes = 4;

    private readonly string _portName;
    private readonly int _baudRate;
    private readonly Thread _owner;
    private readonly List<byte> _pending = new(MeshtasticProto.MaxPacketBytes * 2);
    private readonly Dictionary<uint, MeshtasticRadioNode> _nodes = new();
    private readonly byte[] _readBuffer = new byte[4096];
    private long _packetId;

    private SerialPort? _port;
    private TaskCompletionSource<bool>? _refreshWait;
    private uint _refreshWaitId;
    private uint _localNodeNumber;
    private bool _hasLocalNode;
    private DateTime _nextAnnounceRetryUtc = DateTime.MaxValue;
    private DateTime _nextRefreshRetryUtc = DateTime.MaxValue;
    private byte[] _adminPasskey = [];
    private int? _pendingTxPowerDbm;
    private TaskCompletionSource<bool>? _txPowerWait;
    private int? _pendingModemPreset;
    private TaskCompletionSource<bool>? _modemPresetWait;
    private bool _pendingReadOnly;
    private TaskCompletionSource<LoRaBoardConfigInfo>? _readConfigWait;
    private byte[]? _pendingRawLoraConfig;
    private TaskCompletionSource<bool>? _rawConfigWait;

    /// <summary>Raw board frames (raised on the radio thread).</summary>
    internal event Action<MeshLinkFrame>? Received;

    /// <summary>Raised once when the board reports its local node number.</summary>
    internal event Action? NodeNumberAnnounced;

    /// <summary>
    ///     Called by the gate with the on-air byte count of every frame written to the port
    ///     (radio payload ≈ serial frame minus the 4-byte header) so the duty-cycle budget can record it.
    /// </summary>
    internal Action<int>? OnAirtimeWritten;

    internal MeshtasticSerialSession(string portName, int baudRate, Thread owner, ILogger? logger)
    {
        _portName = portName;
        _baudRate = baudRate;
        _owner = owner;
        Logger = logger;
        _packetId = Random.Shared.Next(1, int.MaxValue);
    }

    internal ILogger? Logger { get; }

    internal uint? LocalNodeNumber => _hasLocalNode ? _localNodeNumber : null;

    /// <summary>Open the port and ask the board for its config (it reports my_info → node number).</summary>
    internal void Open()
    {
        EnsureOwner();
        if (_port != null)
            return;

        var port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 5000,
            DtrEnable = true,
            RtsEnable = true
        };
        port.Open();
        _port = port;

        // The board may reset on the DTR edge and miss this frame while booting;
        // Pump retries the want_config until the board reports my_info.
        WriteRaw(MeshtasticProto.Frame(MeshtasticProto.BuildToRadioWantConfig(1)));
        _nextAnnounceRetryUtc = DateTime.UtcNow.AddSeconds(AnnounceRetrySeconds);
        Logger?.LogInformation("Meshtastic serial session opened on {Port} at {Baud}", _portName, _baudRate);
    }

    internal void Close()
    {
        EnsureOwner();
        var port = _port;
        _port = null;
        _refreshWait?.TrySetCanceled();
        _refreshWait = null;
        _nextAnnounceRetryUtc = DateTime.MaxValue;
        _nextRefreshRetryUtc = DateTime.MaxValue;
        _pending.Clear();
        if (port == null)
            return;

        try
        {
            if (port.IsOpen)
                port.Close();
        }
        finally
        {
            port.Dispose();
        }

        Logger?.LogInformation("Meshtastic serial session on {Port} stopped", _portName);
    }

    internal void Send(uint to, ReadOnlyMemory<byte> payload)
    {
        EnsureOwner();
        if (_port is not { IsOpen: true })
            throw new IOException("Serial port is closed.");

        Logger?.LogTrace(
            "LoRa serial TX → {To:X8}, payload {Len} bytes: {Hex}",
            to, payload.Length, Convert.ToHexString(payload.Span));
        var framed = MeshtasticProto.Frame(
            MeshtasticProto.BuildToRadioPacket(to, NextPacketId(), payload.ToArray(), hopLimit: 3));
        WriteRaw(framed);
    }

    /// <summary>
    ///     Set Config.LoRaConfig.tx_power on the local board (REQ-RF-8).
    ///     Always GET the current LoRaConfig first and merge tx_power into that full blob —
    ///     a set_config with only tx_power replaces the whole LoRa section and wipes region/channel.
    /// </summary>
    internal void BeginApplyTxPower(int txPowerDbm, TaskCompletionSource<bool> done)
    {
        EnsureOwner();
        if (_port is not { IsOpen: true } || !_hasLocalNode)
        {
            done.TrySetResult(false);
            return;
        }

        _txPowerWait?.TrySetResult(false);
        _txPowerWait = done;
        _pendingTxPowerDbm = txPowerDbm;

        try
        {
            var admin = MeshtasticProto.BuildAdminGetLoraConfig();
            var framed = MeshtasticProto.Frame(
                MeshtasticProto.BuildToRadioPacket(
                    _localNodeNumber, NextPacketId(), admin, hopLimit: 0, MeshtasticProto.PortNumAdminApp));
            WriteRaw(framed);
            Logger?.LogDebug(
                "Requested full LoRa config from board before applying TX power {Dbm} dBm",
                txPowerDbm);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Failed to request LoRa config for TX power apply");
            done.TrySetResult(false);
            _txPowerWait = null;
            _pendingTxPowerDbm = null;
        }
    }

    /// <summary>
    ///     Set Config.LoRaConfig.use_preset and modem_preset on the local board.
    ///     GET the current LoRaConfig and merge those fields into that full blob —
    ///     a set_config that contains only the preset replaces the whole LoRa section
    ///     and wipes region, channel and tx_power. If a TX-power apply is already
    ///     waiting on the same GET, the response writes both in one merged blob.
    /// </summary>
    internal void BeginApplyModemPreset(int modemPreset, TaskCompletionSource<bool> done)
    {
        EnsureOwner();
        if (!global::ShortP2P.LoRa.LoRaModemPresets.IsAllowedProtoValue(modemPreset)
            || _port is not { IsOpen: true }
            || !_hasLocalNode)
        {
            done.TrySetResult(false);
            return;
        }

        _modemPresetWait?.TrySetResult(false);
        _modemPresetWait = done;
        _pendingModemPreset = modemPreset;

        try
        {
            var admin = MeshtasticProto.BuildAdminGetLoraConfig();
            var framed = MeshtasticProto.Frame(
                MeshtasticProto.BuildToRadioPacket(
                    _localNodeNumber, NextPacketId(), admin, hopLimit: 0, MeshtasticProto.PortNumAdminApp));
            WriteRaw(framed);
            Logger?.LogDebug(
                "Requested full LoRa config from board before applying modem preset {Preset}",
                global::ShortP2P.LoRa.LoRaModemPresets.NameForProtoValue(modemPreset));
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Failed to request LoRa config for modem preset apply");
            done.TrySetResult(false);
            _modemPresetWait = null;
            _pendingModemPreset = null;
        }
    }

    /// <summary>GET the board LoRaConfig and complete with the parsed snapshot (no set_config).</summary>
    internal void BeginReadLoRaConfig(TaskCompletionSource<LoRaBoardConfigInfo> done)
    {
        EnsureOwner();
        if (_port is not { IsOpen: true } || !_hasLocalNode)
        {
            done.TrySetResult(MeshtasticProto.ParseLoRaConfig([]));
            return;
        }

        _readConfigWait?.TrySetResult(MeshtasticProto.ParseLoRaConfig([]));
        _readConfigWait = done;
        _pendingReadOnly = true;

        try
        {
            var admin = MeshtasticProto.BuildAdminGetLoraConfig();
            var framed = MeshtasticProto.Frame(
                MeshtasticProto.BuildToRadioPacket(
                    _localNodeNumber, NextPacketId(), admin, hopLimit: 0, MeshtasticProto.PortNumAdminApp));
            WriteRaw(framed);
            Logger?.LogDebug("Requested full LoRa config from board (read-only)");
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Failed to request LoRa config for read-only snapshot");
            done.TrySetResult(MeshtasticProto.ParseLoRaConfig([]));
            _readConfigWait = null;
            _pendingReadOnly = false;
        }
    }

    /// <summary>
    ///     Write a previously saved full LoRaConfig blob. Still GETs first when a passkey is needed
    ///     on modern firmware; the set_config body is the saved blob, not a rebuilt merge.
    /// </summary>
    internal void BeginApplyRawLoRaConfig(byte[] loraConfig, TaskCompletionSource<bool> done)
    {
        EnsureOwner();
        if (loraConfig.Length == 0 || _port is not { IsOpen: true } || !_hasLocalNode)
        {
            done.TrySetResult(false);
            return;
        }

        _rawConfigWait?.TrySetResult(false);
        _rawConfigWait = done;
        _pendingRawLoraConfig = loraConfig;

        try
        {
            // Prefer a fresh passkey: GET then replace the response body with our saved blob.
            var admin = MeshtasticProto.BuildAdminGetLoraConfig();
            var framed = MeshtasticProto.Frame(
                MeshtasticProto.BuildToRadioPacket(
                    _localNodeNumber, NextPacketId(), admin, hopLimit: 0, MeshtasticProto.PortNumAdminApp));
            WriteRaw(framed);
            Logger?.LogDebug(
                "Requested LoRa config before restoring saved blob ({Len} bytes)", loraConfig.Length);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Failed to request LoRa config before raw restore");
            done.TrySetResult(false);
            _rawConfigWait = null;
            _pendingRawLoraConfig = null;
        }
    }

    /// <summary>
    ///     Ask the board to resend its node database; the wait completes on config_complete
    /// (raised by <see cref="Pump" /> when the board answers with the same config id).
    /// </summary>
    internal void BeginRefresh(TaskCompletionSource<bool> refresh)
    {
        EnsureOwner();
        if (_port is not { IsOpen: true })
        {
            refresh.TrySetResult(false);
            return;
        }

        _nodes.Clear();
        var id = (uint)Random.Shared.Next(2, int.MaxValue);
        _refreshWaitId = id;
        _refreshWait = refresh;
        _nextRefreshRetryUtc = DateTime.UtcNow.AddSeconds(RefreshRetrySeconds);
        WriteRaw(MeshtasticProto.Frame(MeshtasticProto.BuildToRadioWantConfig(id)));
    }

    /// <summary>
    ///     Drain buffered board bytes, parse frames, update the node database and raise events.
    ///     Returns true when the device state changed (caller should republish the snapshot).
    /// </summary>
    internal bool Pump()
    {
        EnsureOwner();
        if (_port is not { IsOpen: true })
            return false;

        RetryDueWants();

        int available;
        try
        {
            available = _port.BytesToRead;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return false;
        }

        if (available <= 0)
            return false;

        int read;
        try
        {
            read = _port.Read(_readBuffer, 0, Math.Min(available, _readBuffer.Length));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            return false;
        }

        if (read <= 0)
            return false;

        for (var i = 0; i < read; i++)
            _pending.Add(_readBuffer[i]);
        return DrainFrames();
    }

    internal IReadOnlyList<MeshtasticRadioNode> SnapshotNodes()
    {
        EnsureOwner();
        return _nodes.Values.ToArray();
    }

    // ---- radio-thread internals ----

    /// <summary>
    ///     Re-send pending want_config frames until the board answers: boards that reset on the DTR
    ///     edge miss the first frame sent milliseconds after open while the firmware is still booting.
    /// </summary>
    private void RetryDueWants()
    {
        var now = DateTime.UtcNow;
        if (!_hasLocalNode && now >= _nextAnnounceRetryUtc)
        {
            try
            {
                WriteRaw(MeshtasticProto.Frame(MeshtasticProto.BuildToRadioWantConfig(1)));
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Retrying the announce want_config failed");
                return;
            }

            Logger?.LogTrace("Re-sent want_config(1), still waiting for the board node number");
            _nextAnnounceRetryUtc = now.AddSeconds(AnnounceRetrySeconds);
        }

        if (_refreshWait != null && now >= _nextRefreshRetryUtc)
        {
            try
            {
                WriteRaw(MeshtasticProto.Frame(MeshtasticProto.BuildToRadioWantConfig(_refreshWaitId)));
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Retrying the node database want_config failed");
                return;
            }

            Logger?.LogTrace("Re-sent want_config({Id}) for the node database", _refreshWaitId);
            _nextRefreshRetryUtc = now.AddSeconds(RefreshRetrySeconds);
        }
    }

    private void WriteRaw(byte[] framed)
    {
        var port = _port;
        if (port is not { IsOpen: true })
            throw new IOException("Serial port is closed.");
        port.Write(framed, 0, framed.Length);
        OnAirtimeWritten?.Invoke(framed.Length - SerialHeaderBytes);
    }

    private uint NextPacketId()
    {
        return (uint)(unchecked(++_packetId) & int.MaxValue);
    }

    private bool DrainFrames()
    {
        var changed = false;
        while (true)
        {
            var start = -1;
            for (var i = 0; i + 1 < _pending.Count; i++)
                if (_pending[i] == MeshtasticProto.Start1 && _pending[i + 1] == MeshtasticProto.Start2)
                {
                    start = i;
                    break;
                }

            if (start < 0)
            {
                _pending.Clear();
                return changed;
            }

            if (start > 0)
                _pending.RemoveRange(0, start);

            if (_pending.Count < 4)
                return changed;

            var length = (_pending[2] << 8) | _pending[3];
            if (length > MeshtasticProto.MaxPacketBytes)
            {
                _pending.RemoveRange(0, 2); // resync
                continue;
            }

            if (_pending.Count < 4 + length)
                return changed;

            var payload = _pending.GetRange(4, length).ToArray();
            _pending.RemoveRange(0, 4 + length);
            changed |= DispatchFromRadio(payload);
        }
    }

    private bool DispatchFromRadio(byte[] payload)
    {
        try
        {
            var changed = false;
            MeshtasticProto.ReadFromRadio(
                payload,
                onPacket: frame =>
                {
                    // Information: empty "LoRa serial RX" in normal host logs previously looked like
                    // zero RF because PrivateApp RX was only LogTrace.
                    Logger?.LogInformation(
                        "LoRa serial RX ← {From:X8} to {To:X8}, payload {Len} bytes (first=0x{First:X2}): {Hex}",
                        frame.From, frame.To, frame.Payload.Length,
                        frame.Payload.Length > 0 ? frame.Payload[0] : 0,
                        Convert.ToHexString(frame.Payload));
                    try
                    {
                        Received?.Invoke(frame);
                    }
                    catch (Exception ex)
                    {
                        // A subscriber must never break the radio thread.
                        Logger?.LogWarning(ex, "Meshtastic frame subscriber failed");
                    }
                },
                onMyNodeNum: node =>
                {
                    _localNodeNumber = node;
                    if (!_hasLocalNode)
                    {
                        _hasLocalNode = true;
                        Logger?.LogInformation("Board reported local node number {Node:X8}", node);
                        NodeNumberAnnounced?.Invoke();
                    }

                    changed = true;
                },
                onNode: node =>
                {
                    _nodes[node.NodeNumber] = node;
                    Logger?.LogInformation("Meshtastic node {Id} {Name}", node.NodeId, node.DisplayName);
                    changed = true;
                },
                onConfigComplete: id =>
                {
                    if (id == _refreshWaitId)
                        _refreshWait?.TrySetResult(true);
                },
                onAdminPayload: admin =>
                {
                    Logger?.LogDebug("Admin payload {Bytes} bytes from board", admin.Length);
                    if (MeshtasticProto.TryReadAdminSessionPasskey(admin, out var key))
                    {
                        _adminPasskey = key;
                        changed = true;
                        Logger?.LogDebug("Admin session_passkey received ({Len} bytes)", key.Length);
                    }

                    // Only write after we have the board's current LoRaConfig (get_config_response).
                    // TX power and modem preset share that blob so the second set_config cannot
                    // put back a stale copy of the field the other apply just changed.
                    if (_pendingReadOnly)
                    {
                        if (MeshtasticProto.TryReadAdminGetConfigResponseLora(admin, out var readLora))
                        {
                            var info = MeshtasticProto.ParseLoRaConfig(readLora);
                            _pendingReadOnly = false;
                            _readConfigWait?.TrySetResult(info);
                            _readConfigWait = null;
                            Logger?.LogInformation(
                                "Read board LoRaConfig: preset={Preset} region={Region} tx={Dbm}dBm overrideDuty={Override} duty%={Duty}",
                                info.ModemPresetName, info.RegionCode, info.TxPowerDbm,
                                info.OverrideDutyCycle, info.DutyCycleMaxPercent);
                        }
                        else
                        {
                            Logger?.LogDebug(
                                "Admin payload while LoRa read is pending did not contain get_config_response.lora ({Bytes} bytes)",
                                admin.Length);
                        }
                    }
                    else if (_pendingRawLoraConfig != null)
                    {
                        // Passkey may arrive on this admin payload; then write the saved blob as-is.
                        if (MeshtasticProto.TryReadAdminGetConfigResponseLora(admin, out _)
                            || _adminPasskey.Length > 0)
                        {
                            WriteRawLoraConfig(_pendingRawLoraConfig);
                        }
                        else
                        {
                            Logger?.LogDebug(
                                "Admin payload while raw LoRa restore is pending — waiting for passkey/config ({Bytes} bytes)",
                                admin.Length);
                        }
                    }
                    else if (_pendingTxPowerDbm != null || _pendingModemPreset != null)
                    {
                        if (MeshtasticProto.TryReadAdminGetConfigResponseLora(admin, out var lora))
                        {
                            var dbm = _pendingTxPowerDbm;
                            var preset = _pendingModemPreset;
                            var patched = lora;
                            if (dbm is int power)
                                patched = MeshtasticProto.PatchLoRaConfigTxPower(patched, power);
                            if (preset is int mode)
                                patched = MeshtasticProto.PatchLoRaConfigModemPreset(patched, mode);
                            WriteMergedLoraConfig(patched, dbm, preset);
                        }
                        else
                        {
                            Logger?.LogDebug(
                                "Admin payload while LoRa config apply is pending did not contain get_config_response.lora ({Bytes} bytes)",
                                admin.Length);
                        }
                    }
                },
                onSkippedPacket: (from, to, port, reason) =>
                {
                    Logger?.LogInformation(
                        "LoRa serial MeshPacket skipped ← {From:X8} to {To:X8}, port={Port}: {Reason}",
                        from, to, port?.ToString() ?? "-", reason);
                });
            return changed;
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Failed to parse a FromRadio payload ({Len} bytes)", payload.Length);
            return false;
        }
    }

    private void WriteMergedLoraConfig(byte[] loraConfig, int? txPowerDbm, int? modemPreset)
    {
        try
        {
            var admin = MeshtasticProto.BuildAdminSetLoraConfig(loraConfig, _adminPasskey);
            var framed = MeshtasticProto.Frame(
                MeshtasticProto.BuildToRadioPacket(
                    _localNodeNumber, NextPacketId(), admin, hopLimit: 0, MeshtasticProto.PortNumAdminApp));
            WriteRaw(framed);
            if (txPowerDbm is int dbm)
                Logger?.LogInformation(
                    "Meshtastic TX power set to {Dbm} dBm via full LoRaConfig merge (passkey={HasKey}, loraBytes={Len})",
                    dbm, _adminPasskey.Length > 0, loraConfig.Length);
            if (modemPreset is int preset)
                Logger?.LogInformation(
                    "Meshtastic modem preset {Preset} applied via full LoRaConfig merge (use_preset=true, passkey={HasKey}, loraBytes={Len})",
                    global::ShortP2P.LoRa.LoRaModemPresets.NameForProtoValue(preset) ?? preset.ToString(),
                    _adminPasskey.Length > 0, loraConfig.Length);
            _pendingTxPowerDbm = null;
            _pendingModemPreset = null;
            _txPowerWait?.TrySetResult(true);
            _txPowerWait = null;
            _modemPresetWait?.TrySetResult(true);
            _modemPresetWait = null;
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Failed to write merged Meshtastic LoRaConfig (txPower={Dbm}, preset={Preset})",
                txPowerDbm, modemPreset);
            _pendingTxPowerDbm = null;
            _pendingModemPreset = null;
            _txPowerWait?.TrySetResult(false);
            _txPowerWait = null;
            _modemPresetWait?.TrySetResult(false);
            _modemPresetWait = null;
        }
    }

    private void WriteRawLoraConfig(byte[] loraConfig)
    {
        try
        {
            var admin = MeshtasticProto.BuildAdminSetLoraConfig(loraConfig, _adminPasskey);
            var framed = MeshtasticProto.Frame(
                MeshtasticProto.BuildToRadioPacket(
                    _localNodeNumber, NextPacketId(), admin, hopLimit: 0, MeshtasticProto.PortNumAdminApp));
            WriteRaw(framed);
            var info = MeshtasticProto.ParseLoRaConfig(loraConfig);
            Logger?.LogInformation(
                "Restored full Meshtastic LoRaConfig blob ({Len} bytes): preset={Preset} region={Region} tx={Dbm}dBm duty%={Duty} overrideDuty={Override}",
                loraConfig.Length, info.ModemPresetName, info.RegionCode, info.TxPowerDbm,
                info.DutyCycleMaxPercent, info.OverrideDutyCycle);
            _pendingRawLoraConfig = null;
            _rawConfigWait?.TrySetResult(true);
            _rawConfigWait = null;
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Failed to restore raw Meshtastic LoRaConfig ({Len} bytes)",
                loraConfig.Length);
            _pendingRawLoraConfig = null;
            _rawConfigWait?.TrySetResult(false);
            _rawConfigWait = null;
        }
    }

    private void EnsureOwner()
    {
        if (Thread.CurrentThread != _owner)
            throw new InvalidOperationException(
                "Meshtastic serial session access is forbidden outside the MeshtasticGate radio thread.");
    }
}
