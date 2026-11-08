# ShortP2P.LoRa — RF / regulatory requirements

Parent: [SMT-1](https://botticelli.youtrack.cloud/projects/SMT/issues/SMT-1) (ShortP2P LoRa + Meshtastic).

Sources:

- [SMT-1](https://botticelli.youtrack.cloud/projects/SMT/issues/SMT-1)
- [Meshtastic — LoRa Region by Country](https://meshtastic.org/docs/configuration/region-by-country/)
- [Meshtastic — LoRa Configuration (Region table)](https://meshtastic.org/docs/configuration/radio/lora/)

This document defines product requirements for frequency, TX power, and duty cycle. It does not invent a parallel chat protocol: ShortP2P wire frames and the 80-byte radio payload target from SMT-1 still apply.

---

## REQ-RF-1 — Adjustable radio parameters

The LoRa / Meshtastic transport MUST expose configurable:

| Parameter | Unit | Notes |
| --- | --- | --- |
| Frequency range | MHz | Within the selected region band |
| TX power | dBm | Within region and hardware ceilings unless override is confirmed |
| Duty cycle | % TX airtime | Remainder is RX / listen |

Changing these MUST NOT change ShortP2P framing, crypto, or transport contracts from SMT-1.

---

## REQ-RF-2 — Default product profile

Until the user or a country/region profile overrides:

| Parameter | Default |
| --- | --- |
| TX duty cycle | **1%** |
| RX / listen | **99%** (complement of TX duty cycle) |
| Region | `UNSET` → TX allowed at the **1% default duty cycle** (no hard ban; the region cap applies when a region is chosen) |
| TX power | Region max, or hardware-safe default when the stack treats `0` as max legal continuous power |
| Frequency | Center / frequency slot of the selected region band (Meshtastic slot rules are acceptable) |

Rationale: listen-mostly mesh. Product default TX 1% is at or below most regulatory caps (for example EU 10%).

---

## REQ-RF-3 — Enforcement and exceeding limits

1. The **regulatory ceiling** for the selected region (duty cycle %, TX power dBm, frequency band) is always known to the system and shown in UI / config.
2. On an attempt to set a value **above** the regional maximum:
   - Do **not** apply silently (no silent clamp).
   - Show an explicit notice naming what is exceeded (frequency / power / duty cycle), the current regional max, and the requested value.
   - Require explicit confirmation. Canonical warning text (EN):

     > **Note the legal and technical risks in your jurisdiction.**

     Preferred RU locale copy:

     > **Учтите юридические и технические риски Вашей юрисдикции!**

3. Without that confirmation, the previous valid value is kept.
4. After confirmation:
   - The setting is applied.
   - Config / status keeps a visible **override / at-own-risk** flag while the value stays above the norm or until override is cleared.
   - Legal compliance of TX remains the operator’s responsibility (documented disclaimer).
5. Product **default**: TX **1%** / RX **99%**, power ≤ region max, frequency inside the band, override **off**.
6. There is no hidden `min(configured, regionMax)` apply path: either the value is within the norm, or a confirmed override is active.

---

## REQ-RF-4 — Region catalog

Ship Meshtastic-aligned region codes with caps from the LoRa Region table. Values are the firmware ceiling Meshtastic documents, not legal advice — the operator must still check national rules.

| Region | Frequency (MHz) | Duty cycle max % | Power max (dBm) |
| --- | --- | --- | --- |
| `UNSET` | N/A | N/A | N/A |
| `US` | 902.0–928.0 | 100 | 30 |
| `EU_433` | 433.0–434.0 | 10 | 10 |
| `EU_868` | 869.4–869.65 | 10 | 27 |
| `EU_866` | 865.6–867.6 | 2.5 / 10\* | 27 |
| `EU_N_868` | 869.4–869.65 | 10 | 27 |
| `CN` | 470.0–510.0 | 100 | 19 |
| `JP` | 920.5–923.5 | 100 | 13 |
| `ANZ` | 915.0–928.0 | 100 | 30 |
| `ANZ_433` | 433.05–434.79 | 100 | 14 |
| `KR` | 920.0–923.0 | 100 | 23 |
| `TW` | 920.0–925.0 | 100 | 27 |
| `RU` | 868.7–869.2 | 100 | 20 |
| `IN` | 865.0–867.0 | 100 | 30 |
| `NZ_865` | 864.0–868.0 | 100 | 36 |
| `TH` | 920.0–925.0 | 10 | 27 |
| `UA_433` | 433.0–434.7 | 10 | 10 |
| `MY_433` | 433.0–435.0 | 100 | 20 |
| `MY_919` | 919.0–924.0 | 100 | 27 |
| `SG_923` | 917.0–925.0 | 100 | 20 |
| `KZ_433` | 433.075–434.775 | 100 | 10 |
| `KZ_863` | 863.0–868.0 | 100 | 30 |
| `BR_902` | 902.0–907.5 | 100 | 30 |
| `PH_433` | 433.0–434.7 | 100 | 10 |
| `PH_868` | 868.0–869.4 | 100 | 14 |
| `PH_915` | 915.0–918.0 | 100 | 24 |
| `NP_865` | 865.0–868.0 | 100 | 30 |
| `LORA_24` | 2400.0–2483.5 | 100 | 10 |
| ITU ham (`ITU*_2M`, `*_70CM`, `ITU2_125CM`) | per Meshtastic | 100 | 30 |

\* `EU_866`: 2.5% by default; 10% only for `ROUTER` / `ROUTER_LATE`-equivalent roles if those roles are supported.

Notes aligned with Meshtastic:

- `EU_433` and `EU_868`: 10% duty cycle on a rolling one-hour window (recalculated every minute). After the limit, stop TX, keep RX, resume when allowed.
- Amateur ITU regions: licensed mode only; encryption restrictions may apply under ham rules. Treat as restricted / non-goal unless explicitly productized.
- Catalog MUST be versioned and cite the Meshtastic docs revision used (e.g. docs v2.8); refresh when upstream changes.

---

## REQ-RF-5 — Country → region presets

Country selection applies one or more allowed Meshtastic regions from [region-by-country](https://meshtastic.org/docs/configuration/region-by-country/).

After a country is chosen:

1. Primary region = preferred band for that country (configurable preference, e.g. prefer 868 over 433 for EU where both exist).
2. **TX duty cycle remains 1%** unless the region max is lower than 1% (then clamp to region max without override).
3. TX power defaults to the region max (user may lower it).
4. Frequency stays within that region band.

Representative mapping (full table = Meshtastic region-by-country):

| Country | Allowed regions |
| --- | --- |
| Russia | `RU` |
| United States / Canada | `US` |
| EU countries (e.g. DE, FR, PL, …) | `EU_868`, `EU_433` (+ `EU_866` / `EU_N_868` if exposed) |
| United Kingdom | `EU_868`, `EU_433` |
| Ukraine | `EU_868`, `UA_433` |
| Australia | `ANZ` |
| New Zealand | `ANZ`, `NZ_865` |
| Brazil | `ANZ`, `BR_902` |
| China | `CN` |
| Japan | `JP` |
| Kazakhstan | `KZ_433`, `KZ_863` |
| Malaysia | `MY_919`, `MY_433` |
| Philippines | `PH_915`, `PH_868` (and `PH_433` if exposed) |
| Singapore / Indonesia | `SG_923` |
| India | `IN` |
| Thailand | `TH` |
| Taiwan | `TW` |
| Korea | `KR` |
| Nepal | `NP_865` |
| South Africa | `EU_868`, `EU_433` |

---

## REQ-RF-6 — Acceptance (RF)

1. Fresh install / `UNSET` region: TX limited to the **1% default duty cycle** (no hard ban).
2. After defaults apply: measured TX airtime ≤ **1%** over the enforcement window; otherwise RX / listen.
3. Country profile: frequency, power, and duty cycle never exceed region caps **without** a confirmed override.
4. Attempt to set `dutyCycle > regionMax`, `power > regionMax`, or frequency outside the band:
   - blocked until confirm;
   - warning uses the REQ-RF-3 text (legal and technical risks in the user’s jurisdiction);
   - after confirm, value applies and the override flag remains visible;
   - clearing override returns the parameter to within the regional max, with a notice that normative limits are restored.
5. Documented mapping is available: country → region(s) → defaults (frequency band, power dBm, duty cycle %).

---

## REQ-RF-7 — Logging

The LoRa / Meshtastic transport MUST emit structured logs at the levels below. Timestamps use UTC with millisecond precision in the form `yyyy-MM-dd HH:mm:ss.fff` (example: `2025-12-02 19:12:32.123`).

### Debug

Log every radio TX and RX with peer addressing and UTC time:

```text
TX From: <networkId> To: <networkId> DateTimeUTC: <DateTimeUTC>
RX From: <networkId> To: <networkId> DateTimeUTC: <DateTimeUTC>
```

Example:

```text
TX From: 0xA1B2C3D4 To: 0x11223344 DateTimeUTC: 2025-12-02 19:12:32.123
RX From: 0x11223344 To: 0xA1B2C3D4 DateTimeUTC: 2025-12-02 19:12:32.456
```

`networkId` is the ShortP2P network identifier used on the transport (mapped from Meshtastic node id / channel as defined in SMT-1). Directional labels `From` / `To` are mandatory on both TX and RX lines.

### Info

Log lifecycle and configuration events:

- connect to ESP (device / serial / transport link up), including device identity when known;
- disconnect from ESP (link down, intentional stop, or unexpected loss);
- session setup (start, success, failure reason at Info without dumping secrets);
- change of **region** and radio parameters: **power**, **freq**, **channel**, **duty_cycle** (log old → new values, and whether a regulatory override / at-own-risk confirmation was used).

### Error

Log failures that affect correctness or device health:

- **protocol errors** — unexpected / invalid ShortP2P or Meshtastic framing, decode failures, sequence / handshake violations, oversized payloads rejected by transport rules;
- **device errors** — ESP / radio I/O failures, timeouts, firmware NAK, unexpected reset, region/`UNSET` TX attempts blocked by stack.

Error entries MUST include `DateTimeUTC` and enough context to diagnose (operation, peer `networkId` when applicable, error code / message). Do not log cryptographic key material or plaintext chat content at any level.

### Acceptance (logging)

1. With Debug enabled, each successful TX/RX produces a line matching the `From` / `To` / `DateTimeUTC` pattern above.
2. Connect, disconnect, session setup, and each change of region / power / freq / channel / duty_cycle each produce at least one Info event.
3. Injected protocol violation and simulated device fault each produce an Error event with UTC timestamp and context.
4. Secrets and message plaintext are absent from Debug / Info / Error output.

---

## Out of scope (here)

- Replacing MessengerServer with Meshtastic MQTT as the primary cloud API (see SMT-1).
- Standalone LoRa messenger UI unrelated to ShortP2P crypto / `ChatWireCodec`.
- Legal certification or country-by-country counsel; region tables are operational caps aligned with Meshtastic docs.
