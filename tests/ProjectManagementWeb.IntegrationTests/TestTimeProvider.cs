namespace ProjectManagementWeb.IntegrationTests;

internal sealed class TestTimeProvider : TimeProvider
{
    private long _utcTicks;

    public TestTimeProvider(DateTimeOffset initialUtcNow)
    {
        _utcTicks = initialUtcNow.UtcTicks;
    }

    public override DateTimeOffset GetUtcNow() =>
        new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    public void Advance(TimeSpan duration) => Interlocked.Add(ref _utcTicks, duration.Ticks);

    public void SetUtcNow(DateTimeOffset utcNow) => Interlocked.Exchange(ref _utcTicks, utcNow.UtcTicks);
}
