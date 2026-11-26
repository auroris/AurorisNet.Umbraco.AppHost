namespace UmbracoAppHost.Testing;

/// <summary>Controllable <see cref="TimeProvider"/> for deterministic tests; falls back to <see cref="DateTimeOffset.UtcNow"/> when <see cref="Now"/> is null.</summary>
public sealed class TestClock : TimeProvider
{
    public DateTimeOffset? Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now ?? DateTimeOffset.UtcNow;
}
