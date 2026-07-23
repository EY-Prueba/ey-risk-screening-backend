namespace EyRiskScreening.Application.Screening;

// Source adapters must emit only source-approved fields; arbitrary provider
// payloads, transport metadata, credentials, and raw documents are not fields.
public sealed record ScreeningSourceField(string Name, string Value);
