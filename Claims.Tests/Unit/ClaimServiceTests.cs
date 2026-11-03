using Claims.Services;
using Claims.Exceptions;
using Xunit;

namespace Claims.Tests.Unit;

public class ClaimServiceTests
{
    private readonly List<string> _calls = [];
    private readonly FakeClaimRepository _repository;
    private readonly RecordingAuditer _auditer;
    private readonly ClaimService _service;

    public ClaimServiceTests()
    {
        _repository = new FakeClaimRepository(_calls);
        _auditer = new RecordingAuditer(_calls);
        var covers = new FakeCoverRepository([]);
        covers.Items.Add(new Cover
        {
            Id = "cover-id", StartDate = new DateTime(2030, 1, 1), EndDate = new DateTime(2030, 1, 2)
        });
        _service = new ClaimService(_repository, _auditer, covers);
    }

    [Fact]
    public async Task Create_replaces_client_ids_with_distinct_guids_and_returns_saved_objects()
    {
        var first = NewClaim();
        var second = NewClaim();

        var result = await _service.CreateAsync(first);
        await _service.CreateAsync(second);

        Assert.Same(first, result);
        Assert.Collection(_repository.Items, item => Assert.Same(first, item), item => Assert.Same(second, item));
        Assert.True(Guid.TryParse(first.Id, out var firstId));
        Assert.True(Guid.TryParse(second.Id, out var secondId));
        Assert.NotEqual(Guid.Empty, firstId);
        Assert.NotEqual(Guid.Empty, secondId);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal("cover-id", result.CoverId);
        Assert.Equal("Collision", result.Name);
        Assert.Equal(new DateTime(2030, 1, 1), result.Created);
        Assert.Equal(ClaimType.Collision, result.Type);
        Assert.Equal(42m, result.DamageCost);
        Assert.Equal(new[]
        {
            "save-start", "save-complete", $"audit-claim:POST:{first.Id}",
            "save-start", "save-complete", $"audit-claim:POST:{second.Id}"
        }, _calls);
    }

    [Fact]
    public async Task Create_waits_for_persistence_before_auditing()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _repository.AddCompletion = completion;
        var item = NewClaim();

        var creation = _service.CreateAsync(item);
        Assert.False(creation.IsCompleted);
        Assert.Equal(new[] { "save-start" }, _calls);
        completion.SetResult();
        await creation;

        Assert.Equal(new[] { "save-start", "save-complete", $"audit-claim:POST:{item.Id}" }, _calls);
    }

    [Fact]
    public async Task Create_does_not_audit_when_persistence_fails()
    {
        var failure = new InvalidOperationException("Save failed");
        _repository.AddFailure = failure;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(NewClaim()));

        Assert.Same(failure, error);
        Assert.Empty(_repository.Items);
        Assert.Equal(new[] { "save-start" }, _calls);
    }

    [Fact]
    public async Task Create_leaves_saved_record_when_auditing_fails()
    {
        var failure = new InvalidOperationException("Audit failed");
        _auditer.Failure = failure;
        var item = NewClaim();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(item));

        Assert.Same(failure, error);
        Assert.Same(item, Assert.Single(_repository.Items));
        Assert.Equal(new[] { "save-start", "save-complete", $"audit-claim:POST:{item.Id}" }, _calls);
    }

    [Fact]
    public async Task Delete_existing_record_audits_before_deletion()
    {
        var item = NewClaim();
        _repository.Items.Add(item);

        await _service.DeleteAsync(item.Id);

        Assert.Empty(_repository.Items);
        Assert.Equal(new[] { $"audit-claim:DELETE:{item.Id}", $"delete:{item.Id}" }, _calls);
    }

    [Fact]
    public async Task Delete_missing_record_throws_without_auditing_or_deleting()
    {
        var other = NewClaim();
        _repository.Items.Add(other);

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => _service.DeleteAsync("missing"));

        Assert.Equal("Claim 'missing' was not found.", error.Message);
        Assert.Same(other, Assert.Single(_repository.Items));
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_waits_for_existence_check_before_any_side_effects(bool exists)
    {
        var item = NewClaim();
        if (exists) _repository.Items.Add(item);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _repository.LookupCompletion = completion;

        var deletion = _service.DeleteAsync(item.Id);
        Assert.False(deletion.IsCompleted);
        Assert.Empty(_calls);
        completion.SetResult();

        if (exists)
        {
            await deletion;
            Assert.Equal(new[] { $"audit-claim:DELETE:{item.Id}", $"delete:{item.Id}" }, _calls);
        }
        else
        {
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => deletion);
            Assert.Empty(_calls);
        }
        Assert.Empty(_repository.Items);
    }

    [Fact]
    public async Task Delete_does_not_reach_repository_when_auditing_fails()
    {
        var failure = new InvalidOperationException("Audit failed");
        _auditer.Failure = failure;
        var item = NewClaim();
        _repository.Items.Add(item);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteAsync(item.Id));

        Assert.Same(failure, error);
        Assert.Same(item, Assert.Single(_repository.Items));
        Assert.Equal(new[] { $"audit-claim:DELETE:{item.Id}" }, _calls);
    }

    [Fact]
    public async Task Reads_return_repository_results_without_auditing()
    {
        var item = NewClaim();
        _repository.Items.Add(item);

        Assert.Same(item, Assert.Single(await _service.GetAllAsync()));
        Assert.Same(item, await _service.GetByIdAsync(item.Id));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Get_missing_record_throws_without_auditing_or_deleting()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => _service.GetByIdAsync("missing"));

        Assert.Equal("Claim 'missing' was not found.", error.Message);
        Assert.Empty(_calls);
    }

    private static Claim NewClaim() =>
        new Claim { Id = "client-id", CoverId = "cover-id", Name = "Collision", Created = new DateTime(2030, 1, 1), Type = ClaimType.Collision, DamageCost = 42m };
}
