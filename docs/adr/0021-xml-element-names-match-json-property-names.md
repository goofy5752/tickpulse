# 0021. XML element names match JSON property names

- **Status:** Accepted
- **Date:** 2026-11-04

## Context

The same DTOs in `TickPulse.Contracts` are serialised as JSON (System.Text.Json, camelCase) and as
XML (`DataContractSerializer`). By default the XML serializer uses the C# member names, so the same
response had `averagePrice` in JSON and `<AveragePrice>` in XML. Clients that switch formats, the
dashboard's API playground and the CLI's raw output then show two vocabularies for one contract.
`DataContractSerializer` also orders members alphabetically unless told otherwise.

## Decision

- Every contract has an explicit `[DataContract(Name = "…", Namespace = "https://tickpulse.dev/schema/v1")]`
  and every member an explicit `[DataMember(Name = "camelCase", Order = n)]`. XML element names and
  order are then the JSON property names and order.
- Root elements are explicit too: `averagePrice`, `sma` (with `dataPoint` items), `symbols`/`symbol`,
  `candles`/`candle`, `adminSymbols`/`adminSymbol`, `systemStats`.
- Null members are written as `i:nil="true"` in XML and `null` in JSON.
- XML validation problems carry their `errors` as `<errors><n>message</n></errors>` in the
  `urn:ietf:rfc:7807` namespace, mirroring the JSON `errors` object.
- DTO timestamps are `DateTime` with `Kind = Utc`, never `DateTimeOffset`, which
  `DataContractSerializer` writes as a complex type. Both formats carry ISO-8601 with `Z`.

## Alternatives

- **Default member names.** Rejected: two vocabularies for one API.
- **`XmlSerializer` instead of `DataContractSerializer`.** Rejected: it also defaults to the C# names,
  so explicit names would be needed either way, and the problem writer and its tests were built on
  `DataContractSerializer`.

## Consequences

- A field rename is one attribute change that applies to both formats.
- `Sma_Xml_HasSameFields` and the list XML tests assert that the XML element names equal the JSON
  property names.
- The namespace `https://tickpulse.dev/schema/v1` is part of the XML contract; a breaking change
  would bump it to `v2`.
