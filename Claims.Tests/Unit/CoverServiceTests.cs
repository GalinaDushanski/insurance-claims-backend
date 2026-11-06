using Claims.Services;
using Claims.Exceptions;
using Xunit;

namespace Claims.Tests.Unit;

public class CoverServiceTests
{
    private readonly List<string> _calls = [];
    private readonly FakeCoverRepository _repository;
    private readonly RecordingAuditer _auditer;
    private readonly CoverService _service;

    public CoverServiceTests()
    {
        _repository = new FakeCoverRepository(_calls);
        _auditer = new RecordingAuditer(_calls);
        _service = new CoverService(_repository, _auditer, new PremiumCalculator(),
            new FixedTimeProvider(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task Create_replaces_client_ids_with_distinct_guids_and_returns_saved_objects()
    {
        var first = NewCover();
        var second = NewCover();

        var result = await _service.CreateAsync(first);
        await _service.CreateAsync(second);

        Assert.Same(first, result);
        Assert.Collection(_repository.Items, item => Assert.Same(first, item), item => Assert.Same(second, item));
        Assert.True(Guid.TryParse(first.Id, out var firstId));
        Assert.True(Guid.TryParse(second.Id, out var secondId));
        Assert.NotEqual(Guid.Empty, firstId);
        Assert.NotEqual(Guid.Empty, secondId);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(1375m, result.Premium);
        Assert.Equal(new DateTime(2030, 1, 1), result.StartDate);
        Assert.Equal(new DateTime(2030, 1, 2), result.EndDate);
        Assert.Equal(CoverType.Yacht, result.Type);
        Assert.Equal(new[]
        {
            "save-start", "save-complete", $"audit-cover:POST:{first.Id}",
            "save-start", "save-complete", $"audit-cover:POST:{second.Id}"
        }, _calls);
    }

    [Fact]
    public async Task Create_waits_for_persistence_before_publishing()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _repository.AddCompletion = completion;
        var item = NewCover();

        var creation = _service.CreateAsync(item);
        Assert.False(creation.IsCompleted);
        Assert.Equal(new[] { "save-start" }, _calls);
        completion.SetResult();
        await creation;

        Assert.Equal(new[] { "save-start", "save-complete", $"audit-cover:POST:{item.Id}" }, _calls);
    }

    [Fact]
    public async Task Create_does_not_publish_when_persistence_fails()
    {
        var failure = new InvalidOperationException("Save failed");
        _repository.AddFailure = failure;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(NewCover()));

        Assert.Same(failure, error);
        Assert.Empty(_repository.Items);
        Assert.Equal(new[] { "save-start" }, _calls);
    }

    [Fact]
    public async Task Create_leaves_saved_record_when_audit_publication_fails()
    {
        var failure = new InvalidOperationException("Enqueue failed");
        _auditer.Failure = failure;
        var item = NewCover();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(item));

        Assert.Same(failure, error);
        Assert.Same(item, Assert.Single(_repository.Items));
        Assert.Equal(new[] { "save-start", "save-complete", $"audit-cover:POST:{item.Id}" }, _calls);
    }

    [Fact]
    public async Task Delete_existing_record_publishes_after_deletion()
    {
        var item = NewCover();
        _repository.Items.Add(item);

        await _service.DeleteAsync(item.Id);

        Assert.Empty(_repository.Items);
        Assert.Equal(new[] { $"delete-start:{item.Id}", $"delete-complete:{item.Id}", $"audit-cover:DELETE:{item.Id}" }, _calls);
    }

    [Fact]
    public async Task Delete_missing_record_throws_without_auditing_or_deleting()
    {
        var other = NewCover();
        _repository.Items.Add(other);

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => _service.DeleteAsync("missing"));

        Assert.Equal("Cover 'missing' was not found.", error.Message);
        Assert.Same(other, Assert.Single(_repository.Items));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Delete_propagates_lookup_failure_without_deleting_or_publishing()
    {
        var failure = new InvalidOperationException("Lookup failed");
        _repository.LookupFailure = failure;
        var item = NewCover();
        _repository.Items.Add(item);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteAsync(item.Id));

        Assert.Same(failure, error);
        Assert.Same(item, Assert.Single(_repository.Items));
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_waits_for_existence_check_before_any_side_effects(bool exists)
    {
        var item = NewCover();
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
            Assert.Equal(new[] { $"delete-start:{item.Id}", $"delete-complete:{item.Id}", $"audit-cover:DELETE:{item.Id}" }, _calls);
        }
        else
        {
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => deletion);
            Assert.Empty(_calls);
        }
        Assert.Empty(_repository.Items);
    }

    [Fact]
    public async Task Delete_remains_deleted_when_audit_publication_fails()
    {
        var failure = new InvalidOperationException("Enqueue failed");
        _auditer.Failure = failure;
        var item = NewCover();
        _repository.Items.Add(item);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteAsync(item.Id));

        Assert.Same(failure, error);
        Assert.Empty(_repository.Items);
        Assert.Equal(new[] { $"delete-start:{item.Id}", $"delete-complete:{item.Id}", $"audit-cover:DELETE:{item.Id}" }, _calls);
    }

    [Fact]
    public async Task Reads_return_repository_results_without_auditing()
    {
        var item = NewCover();
        _repository.Items.Add(item);

        Assert.Same(item, Assert.Single(await _service.GetAllAsync()));
        Assert.Same(item, await _service.GetByIdAsync(item.Id));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Get_missing_record_throws_without_auditing_or_deleting()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => _service.GetByIdAsync("missing"));

        Assert.Equal("Cover 'missing' was not found.", error.Message);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Delete_waits_for_persistence_before_publishing()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _repository.DeleteCompletion = completion;
        var item = NewCover();
        _repository.Items.Add(item);

        var deletion = _service.DeleteAsync(item.Id);

        Assert.False(deletion.IsCompleted);
        Assert.Equal(new[] { $"delete-start:{item.Id}" }, _calls);
        Assert.Same(item, Assert.Single(_repository.Items));
        completion.SetResult();
        await deletion;
        Assert.Empty(_repository.Items);
        Assert.Equal(new[] { $"delete-start:{item.Id}", $"delete-complete:{item.Id}", $"audit-cover:DELETE:{item.Id}" }, _calls);
    }

    [Fact]
    public async Task Delete_does_not_publish_when_persistence_fails()
    {
        var failure = new InvalidOperationException("Delete failed");
        _repository.DeleteFailure = failure;
        var item = NewCover();
        _repository.Items.Add(item);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteAsync(item.Id));

        Assert.Same(failure, error);
        Assert.Same(item, Assert.Single(_repository.Items));
        Assert.Equal(new[] { $"delete-start:{item.Id}" }, _calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operations_wait_for_publication_after_database_changes_complete(bool deleting)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _auditer.PublicationCompletion = completion;
        var item = NewCover();
        if (deleting) _repository.Items.Add(item);

        Task operation = deleting ? _service.DeleteAsync(item.Id) : _service.CreateAsync(item);

        Assert.False(operation.IsCompleted);
        if (deleting) Assert.Empty(_repository.Items);
        else Assert.Same(item, Assert.Single(_repository.Items));
        Assert.Equal($"audit-cover:{(deleting ? "DELETE" : "POST")}:{item.Id}", _calls.Last());
        completion.SetResult();
        await operation;
    }

    private static Cover NewCover() =>
        new Cover { Id = "client-id", StartDate = new DateTime(2030, 1, 1), EndDate = new DateTime(2030, 1, 2), Type = CoverType.Yacht, Premium = -1m };
}
