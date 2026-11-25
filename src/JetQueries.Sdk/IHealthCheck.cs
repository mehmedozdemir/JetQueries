namespace JetQueries.Sdk;

/// <summary>
/// Optional for an <see cref="IDatabaseConnection"/>: checks the server against well-known rules (configuration,
/// databases, backups, security) for the health check screen. It only reads; a finding may carry a script that would
/// fix it, which the host puts into a query tab and never runs by itself.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0 (ADR 0013).</remarks>
public interface IHealthCheck
{
    /// <summary>Checks every rule the provider has; a rule that passes is reported as <see cref="HealthSeverity.Ok"/>.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<HealthFinding>> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>How much a finding matters.</summary>
public enum HealthSeverity
{
    /// <summary>The rule passes.</summary>
    Ok,

    /// <summary>Worth knowing; usually no action needed.</summary>
    Info,

    /// <summary>Should be fixed when convenient.</summary>
    Warning,

    /// <summary>Data or availability is at risk: fix soon.</summary>
    Critical,
}

/// <summary>What a rule is about.</summary>
public enum HealthCategory
{
    /// <summary>Server settings.</summary>
    Configuration,

    /// <summary>Database options and files.</summary>
    Databases,

    /// <summary>Backups and integrity checks.</summary>
    Backups,

    /// <summary>Logins and permissions.</summary>
    Security,

    /// <summary>Settings that affect speed.</summary>
    Performance,
}

/// <summary>The result of one rule.</summary>
/// <param name="RuleId">
/// A stable identifier (<c>MaxServerMemory</c>, <c>AutoShrink</c>…); the host shows its translated title and
/// explanation for known ids, else <see cref="Title"/>.
/// </param>
/// <param name="Category">What it is about.</param>
/// <param name="Severity">How much it matters.</param>
/// <param name="Title">The rule's name in English, used when the host has no translation.</param>
public sealed record HealthFinding(string RuleId, HealthCategory Category, HealthSeverity Severity, string Title)
{
    /// <summary>The value found (<c>2147483647</c>, <c>0</c>, <c>TORN_PAGE_DETECTION</c>…); null when not one value.</summary>
    public string? Current { get; init; }

    /// <summary>The value recommended; null when not one value.</summary>
    public string? Recommended { get; init; }

    /// <summary>What the rule found it on (databases, files, logins), each with its own detail.</summary>
    public IReadOnlyList<string> Objects { get; init; } = [];

    /// <summary>A statement that would fix it, quoted for the server; never run by the host.</summary>
    public string? FixScript { get; init; }
}
