namespace ShortP2P.LoRa.Link;

/// <summary>Optional board radio settings beyond opaque datagram TX (REQ-RF-8).</summary>
public interface IMeshRadioConfig
{
    /// <summary>Write Config.LoRaConfig.tx_power (dBm) to the local Meshtastic node.</summary>
    ValueTask ApplyTxPowerDbmAsync(int txPowerDbm, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Write Config.LoRaConfig.region. Implementations GET the full config, patch region, and SET
    ///     the full blob. Zero means UNSET and must not be written.
    /// </summary>
    ValueTask ApplyRegionCodeAsync(uint regionCode, CancellationToken cancellationToken = default);
}
