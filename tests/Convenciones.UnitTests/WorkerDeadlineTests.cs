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
    public void Deadline_bound_timeout_before_the_wall_clock_reaches_the_deadline_is_a_deadline_failure()
    {
        var now = DateTimeOffset.UtcNow;
        // The cancellation timer is monotonic; it can fire a few milliseconds before the wall clock
        // reaches DeadlineUtc while the clock is slewed. That is still the deadline's cancellation,
        // and treating it as a retryable attempt timeout would requeue a run the worker never retries.
        WorkerDeadlinePolicy.IsDeadlineFailure(deadlineBoundTimeout: true, now.AddMilliseconds(5), now)
            .ShouldBeTrue();
    }

    [Fact]
    public void Unbounded_timeout_before_the_deadline_is_a_retryable_attempt_timeout()
    {
        var now = DateTimeOffset.UtcNow;
        WorkerDeadlinePolicy.IsDeadlineFailure(deadlineBoundTimeout: false, now.AddSeconds(30), now)
            .ShouldBeFalse();
    }

    [Fact]
    public void Unbounded_timeout_at_or_after_the_deadline_is_a_deadline_failure()
    {
        var now = DateTimeOffset.UtcNow;
        WorkerDeadlinePolicy.IsDeadlineFailure(deadlineBoundTimeout: false, now, now).ShouldBeTrue();
    }

    [Fact]
    public void Without_a_deadline_the_timeout_is_never_a_deadline_failure()
    {
        WorkerDeadlinePolicy.IsDeadlineFailure(deadlineBoundTimeout: false, deadlineUtc: null, DateTimeOffset.UtcNow)
            .ShouldBeFalse();
    }

    [Fact]
    public void Factory_parses_optional_claim_scope()
    {
        var documentId = Guid.NewGuid();
        var options = WorkerOptionsFactory.From(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:AllowedDocumentId"] = documentId.ToString(),
            })
            .Build());

        options.AllowedDocumentId.ShouldBe(documentId);
        options.AllowedRunId.ShouldBeNull();
        options.ScopeInvalid.ShouldBeFalse();
    }

    [Fact]
    public void Invalid_claim_scope_fails_closed()
    {
        var options = WorkerOptionsFactory.From(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:AllowedRunId"] = "not-a-guid",
            })
            .Build());

        options.ScopeInvalid.ShouldBeTrue();
    }

    [Fact]
    public void Require_scope_is_parsed_separately_from_the_scope_value()
    {
        var options = WorkerOptionsFactory.From(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RequireScope"] = "true",
            })
            .Build());

        options.RequireScope.ShouldBeTrue();
        options.AllowedDocumentId.ShouldBeNull();
        options.AllowedRunId.ShouldBeNull();
    }

    [Fact]
    public void Scoped_staging_requires_a_target()
    {
        var options = new WorkerOptions
        {
            Enabled = true,
            RequireScope = true,
        };

        WorkerScopePolicy.Validate(options).ShouldNotBeNull();
    }

    [Fact]
    public void Scoped_staging_accepts_one_document_target()
    {
        var options = new WorkerOptions
        {
            Enabled = true,
            RequireScope = true,
            AllowedCompanyId = Guid.NewGuid(),
            AuthorizedCompanyId = Guid.NewGuid(),
            AllowedDocumentId = Guid.NewGuid(),
        };

        WorkerScopePolicy.Validate(options).ShouldBeNull();
    }

    [Fact]
    public void Unscoped_production_behavior_remains_allowed()
    {
        var options = new WorkerOptions
        {
            Enabled = true,
            RequireScope = false,
        };

        WorkerScopePolicy.Validate(options).ShouldBeNull();
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
