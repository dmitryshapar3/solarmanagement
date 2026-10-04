namespace DeyeSolar.Web.Integrations;

public sealed record IntegrationCommandReceipt(Guid CommandId, Guid DeviceId, bool IsOn, string Status,
    string? Rejection, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);
public sealed record IntegrationSocketCommandRequest(Guid CommandId, bool IsOn);
