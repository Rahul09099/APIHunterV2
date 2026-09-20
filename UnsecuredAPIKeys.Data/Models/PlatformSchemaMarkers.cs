using System.ComponentModel.DataAnnotations;

namespace UnsecuredAPIKeys.Data.Models;

/// <summary>
/// Durable version of the additive control-plane schema understood by this runtime.
/// </summary>
public sealed class PlatformSchemaVersion
{
    [Key]
    public int Id { get; set; }

    public int Version { get; set; }

    public DateTime AppliedUtc { get; set; }
}

/// <summary>
/// Durable proof that the schema/backfill slice represented by <see cref="SchemaVersion"/>
/// completed successfully.
/// </summary>
public sealed class ReadinessMarker
{
    [Key]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SchemaVersion { get; set; }

    public bool IsReady { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// Durable migration-stage marker. Task 2.2 creates the pre-cutover record only;
/// later cutover tasks are responsible for changing its completion state.
/// </summary>
public sealed class CutoverMarker
{
    [Key]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Version { get; set; }

    public bool IsComplete { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
