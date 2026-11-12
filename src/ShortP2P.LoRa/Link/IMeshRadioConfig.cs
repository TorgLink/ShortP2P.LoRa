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

    /// <summary>
    ///     GET the board's current full LoRaConfig without writing. Empty when the board did not answer.
    /// </summary>
    ValueTask<LoRaBoardConfigInfo> ReadLoRaConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Write a previously saved full LoRaConfig blob via set_config (no field rebuild).
    ///     Empty blobs are rejected so the section cannot be wiped.
    /// </summary>
    ValueTask ApplyRawLoRaConfigAsync(ReadOnlyMemory<byte> loraConfig, CancellationToken cancellationToken = default);
}
