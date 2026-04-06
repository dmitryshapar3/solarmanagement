namespace DeyeSolar.Domain.Options;

public class PollingOptions
{
    public const string Section = "Polling";

    public int IntervalSeconds { get; set; } = 10;
}
