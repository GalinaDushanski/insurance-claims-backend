using Claims.Domain.Services;
using Claims.Api.Contracts;
using Claims.Controllers;
using Claims.Services;
using Xunit;

namespace Claims.Tests.Unit;

public class CancellationPropagationTests
{
    private readonly List<string> _calls = [];
    private readonly FakeClaimRepository _claims;
    private readonly FakeCoverRepository _covers;
    private readonly RecordingAuditer _auditer;
    private readonly ClaimsController _claimController;
    private readonly CoversController _coverController;

    public CancellationPropagationTests()
    {
        _claims = new FakeClaimRepository(_calls);
        _covers = new FakeCoverRepository(_calls);
        _auditer = new RecordingAuditer(_calls);
        _covers.Items.Add(NewCover());
        _claims.Items.Add(NewClaim());
        _claimController = new ClaimsController(new ClaimService(_claims, _auditer, _covers));
        _coverController = new CoversController(new CoverService(_covers, _auditer, new PremiumCalculator(),
            new FixedTimeProvider(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero))));
    }

    [Theory]
    [InlineData(false, "list", 1, 0, 0)]
    [InlineData(false, "get", 1, 0, 0)]
    [InlineData(false, "create", 1, 1, 1)]
    [InlineData(false, "delete", 2, 0, 1)]
    [InlineData(true, "list", 0, 1, 0)]
    [InlineData(true, "get", 0, 1, 0)]
    [InlineData(true, "create", 0, 1, 1)]
    [InlineData(true, "delete", 0, 2, 1)]
    public async Task Controller_passes_request_token_to_persistence_and_independent_token_to_auditing(
        bool cover, string operation, int claimCalls, int coverCalls, int auditCalls)
    {
        using var timeout = CreateTimeout();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await InvokeAsync(cover, operation, cancellation.Token).WaitAsync(timeout.Token);

        Assert.Equal(claimCalls, _claims.Tokens.Count);
        Assert.Equal(coverCalls, _covers.Tokens.Count);
        Assert.Equal(auditCalls, _auditer.Tokens.Count);
        Assert.All(_claims.Tokens.Concat(_covers.Tokens), token => Assert.Equal(cancellation.Token, token));
        Assert.All(_auditer.Tokens, token =>
        {
            Assert.NotEqual(cancellation.Token, token);
            Assert.True(token.CanBeCanceled);
        });
    }

    [Theory]
    [InlineData(false, "list")]
    [InlineData(false, "get")]
    [InlineData(false, "create")]
    [InlineData(false, "delete")]
    [InlineData(true, "list")]
    [InlineData(true, "get")]
    [InlineData(true, "create")]
    [InlineData(true, "delete")]
    [InlineData(true, "compute")]
    public async Task Already_cancelled_request_propagates_cancellation_without_mutations(bool cover, string operation)
    {
        using var timeout = CreateTimeout();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(cover, operation, cancellation.Token).WaitAsync(timeout.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Single(_claims.Items);
        Assert.Single(_covers.Items);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData(false, "validation")]
    [InlineData(false, "save")]
    [InlineData(false, "lookup")]
    [InlineData(false, "delete")]
    [InlineData(true, "save")]
    [InlineData(true, "lookup")]
    [InlineData(true, "delete")]
    public async Task Inflight_cancellation_stops_waiting_and_prevents_subsequent_work(bool cover, string stage)
    {
        using var timeout = CreateTimeout();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleting = stage is "lookup" or "delete";
        if (stage == "validation") _covers.LookupCompletion = gate;
        else if (cover)
        {
            if (stage == "save") _covers.AddCompletion = gate;
            else if (stage == "lookup") _covers.LookupCompletion = gate;
            else _covers.DeleteCompletion = gate;
        }
        else
        {
            if (stage == "save") _claims.AddCompletion = gate;
            else if (stage == "lookup") _claims.LookupCompletion = gate;
            else _claims.DeleteCompletion = gate;
        }

        var operation = InvokeAsync(cover, deleting ? "delete" : "create", cancellation.Token);
        Assert.False(operation.IsCompleted);
        await cancellation.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(timeout.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(gate.Task.IsCompleted);
        Assert.Single(_claims.Items);
        Assert.Single(_covers.Items);
        Assert.Empty(_auditer.Tokens);
    }

    private static CancellationTokenSource CreateTimeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return timeout;
    }

    private Task InvokeAsync(bool cover, string operation, CancellationToken token) => (cover, operation) switch
    {
        (false, "list") => _claimController.GetAsync(token),
        (false, "get") => _claimController.GetAsync("claim-id", token),
        (false, "create") => _claimController.CreateAsync(NewClaim().ToDto(), token),
        (false, "delete") => _claimController.DeleteAsync("claim-id", token),
        (true, "list") => _coverController.GetAsync(token),
        (true, "get") => _coverController.GetAsync("cover-id", token),
        (true, "create") => _coverController.CreateAsync(NewCover().ToDto(), token),
        (true, "delete") => _coverController.DeleteAsync("cover-id", token),
        (true, "compute") => _coverController.ComputePremiumAsync(
            new DateTime(2030, 1, 1), new DateTime(2030, 1, 2), CoverType.Yacht, token),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static Cover NewCover() => new()
    {
        Id = "cover-id", StartDate = new DateTime(2030, 1, 1),
        EndDate = new DateTime(2030, 1, 2), Type = CoverType.Yacht
    };

    private static Claim NewClaim() => new()
    {
        Id = "claim-id", CoverId = "cover-id", Created = new DateTime(2030, 1, 1),
        Name = "Collision", Type = ClaimType.Collision, DamageCost = 42m
    };
}
