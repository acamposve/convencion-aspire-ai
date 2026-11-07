using Convenciones.Worker;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests;

public sealed class WorkerDeadlineTests
{
    private static WorkerOptions Enabled(DateTimeOffset? deadline, bool invalid = false) => new()
    {
        Enabled = true,
        DeadlineRaw = deadline?.ToString("O") ?? (invalid ? "not-a-date" : null),
        DeadlineUtc = deadline,
        DeadlineInvalid = invalid,
    };

    [Fact]
    public void Disabled_worker_does_not_need_a_deadline()
    {
        WorkerDeadlinePolicy.Validate(new WorkerOptions(), DateTimeOffset.UtcNow).ShouldBeNull();
    }

    [Fact]
    public void Enabled_worker_without_deadline_fails_closed()
    {
        WorkerDeadlinePolicy.Validate(Enabled(null), DateTimeOffset.UtcNow).ShouldNotBeNull();
    }

    [Fact]
    public void Enabled_worker_with_invalid_deadline_fails_closed()
    {
        WorkerDeadlinePolicy.Validate(Enabled(null, invalid: true), DateTimeOffset.UtcNow).ShouldNotBeNull();
    }

    [Fact]
    public void Expired_deadline_fails_closed()
    {
        var now = DateTimeOffset.UtcNow;
        WorkerDeadlinePolicy.Validate(Enabled(now.AddMinutes(-1)), now).ShouldNotBeNull();
    }

    [Fact]
    public void Future_deadline_is_accepted()
    {
        var now = DateTimeOffset.UtcNow;
        WorkerDeadlinePolicy.Validate(Enabled(now.AddMinutes(10)), now).ShouldBeNull();
    }

    [Fact]
    public void Factory_parses_the_deadline_and_marks_invalid_values()
    {
        var valid = WorkerOptionsFactory.From(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:Enabled"] = "true",
                ["Worker:DeadlineUtc"] = "2030-01-01T00:00:00Z",
            })
            .Build());
        valid.DeadlineUtc.ShouldNotBeNull();
        valid.DeadlineInvalid.ShouldBeFalse();

        var invalid = WorkerOptionsFactory.From(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:Enabled"] = "true",
                ["Worker:DeadlineUtc"] = "not-a-date",
            })
            .Build());
        invalid.DeadlineInvalid.ShouldBeTrue();
    }
}
