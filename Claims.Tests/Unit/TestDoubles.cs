using Claims.Auditing;
using Claims.Repositories;

namespace Claims.Tests.Unit;

internal sealed class FakeClaimRepository(List<string> calls) : IClaimRepository
{
    public List<Claim> Items { get; } = [];
    public TaskCompletionSource? AddCompletion { get; set; }
    public TaskCompletionSource? LookupCompletion { get; set; }
    public Exception? LookupFailure { get; set; }
    public Exception? AddFailure { get; set; }
    public TaskCompletionSource? DeleteCompletion { get; set; }
    public Exception? DeleteFailure { get; set; }

    public Task<IEnumerable<Claim>> GetAllAsync() => Task.FromResult<IEnumerable<Claim>>(Items);

    public async Task<Claim?> GetByIdAsync(string id)
    {
        if (LookupCompletion is not null) await LookupCompletion.Task;
        if (LookupFailure is not null) throw LookupFailure;
        return Items.SingleOrDefault(item => item.Id == id);
    }

    public async Task AddAsync(Claim claim)
    {
        calls.Add("save-start");
        if (AddFailure is not null) throw AddFailure;
        if (AddCompletion is not null) await AddCompletion.Task;
        Items.Add(claim);
        calls.Add("save-complete");
    }

    public async Task DeleteAsync(string id)
    {
        calls.Add($"delete-start:{id}");
        if (DeleteFailure is not null) throw DeleteFailure;
        if (DeleteCompletion is not null) await DeleteCompletion.Task;
        Items.RemoveAll(item => item.Id == id);
        calls.Add($"delete-complete:{id}");
    }
}

internal sealed class FakeCoverRepository(List<string> calls) : ICoverRepository
{
    public List<Cover> Items { get; } = [];
    public TaskCompletionSource? AddCompletion { get; set; }
    public TaskCompletionSource? LookupCompletion { get; set; }
    public Exception? LookupFailure { get; set; }
    public Exception? AddFailure { get; set; }
    public TaskCompletionSource? DeleteCompletion { get; set; }
    public Exception? DeleteFailure { get; set; }

    public Task<IEnumerable<Cover>> GetAllAsync() => Task.FromResult<IEnumerable<Cover>>(Items);

    public async Task<Cover?> GetByIdAsync(string id)
    {
        if (LookupCompletion is not null) await LookupCompletion.Task;
        if (LookupFailure is not null) throw LookupFailure;
        return Items.SingleOrDefault(item => item.Id == id);
    }

    public async Task AddAsync(Cover cover)
    {
        calls.Add("save-start");
        if (AddFailure is not null) throw AddFailure;
        if (AddCompletion is not null) await AddCompletion.Task;
        Items.Add(cover);
        calls.Add("save-complete");
    }

    public async Task DeleteAsync(string id)
    {
        calls.Add($"delete-start:{id}");
        if (DeleteFailure is not null) throw DeleteFailure;
        if (DeleteCompletion is not null) await DeleteCompletion.Task;
        Items.RemoveAll(item => item.Id == id);
        calls.Add($"delete-complete:{id}");
    }
}

internal sealed class RecordingAuditer(List<string> calls) : IAuditer
{
    public Exception? Failure { get; set; }
    public TaskCompletionSource? PublicationCompletion { get; set; }

    public Task AuditClaimAsync(string id, string httpRequestType, CancellationToken cancellationToken = default) =>
        RecordAsync($"audit-claim:{httpRequestType}:{id}", cancellationToken);

    public Task AuditCoverAsync(string id, string httpRequestType, CancellationToken cancellationToken = default) =>
        RecordAsync($"audit-cover:{httpRequestType}:{id}", cancellationToken);

    private async Task RecordAsync(string call, CancellationToken cancellationToken)
    {
        calls.Add(call);
        if (Failure is not null) throw Failure;
        if (PublicationCompletion is not null) await PublicationCompletion.Task.WaitAsync(cancellationToken);
    }
}
