namespace ShortP2P.LoRa.Link;

/// <summary>Optional board radio settings beyond opaque datagram TX (REQ-RF-8).</summary>
public interface IMeshRadioConfig
{
    /// <summary>Write Config.LoRaConfig.tx_power (dBm) to the local Meshtastic node.</summary>
    ValueTask ApplyTxPowerDbmAsync(int txPowerDbm, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Merge <c>use_preset=true</c> and <c>modem_preset</c> into the board's current LoRaConfig.
    ///     <paramref name="modemPreset"/> is the protobuf enum value; only universal presets are written.
    /// </summary>
    ValueTask ApplyModemPresetAsync(int modemPreset, CancellationToken cancellationToken = default);
}
