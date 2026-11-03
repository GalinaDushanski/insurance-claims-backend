using Claims.Auditing;
using Claims.Repositories;

namespace Claims.Tests.Unit;

internal sealed class FakeClaimRepository(List<string> calls) : IClaimRepository
{
    public List<Claim> Items { get; } = [];
    public TaskCompletionSource? AddCompletion { get; set; }
    public Exception? AddFailure { get; set; }

    public Task<IEnumerable<Claim>> GetAllAsync() => Task.FromResult<IEnumerable<Claim>>(Items);

    public Task<Claim?> GetByIdAsync(string id) =>
        Task.FromResult(Items.SingleOrDefault(item => item.Id == id));

    public async Task AddAsync(Claim claim)
    {
        calls.Add("save-start");
        if (AddFailure is not null) throw AddFailure;
        if (AddCompletion is not null) await AddCompletion.Task;
        Items.Add(claim);
        calls.Add("save-complete");
    }

    public Task DeleteAsync(string id)
    {
        calls.Add($"delete:{id}");
        Items.RemoveAll(item => item.Id == id);
        return Task.CompletedTask;
    }
}

internal sealed class FakeCoverRepository(List<string> calls) : ICoverRepository
{
    public List<Cover> Items { get; } = [];
    public TaskCompletionSource? AddCompletion { get; set; }
    public Exception? AddFailure { get; set; }

    public Task<IEnumerable<Cover>> GetAllAsync() => Task.FromResult<IEnumerable<Cover>>(Items);

    public Task<Cover?> GetByIdAsync(string id) =>
        Task.FromResult(Items.SingleOrDefault(item => item.Id == id));

    public async Task AddAsync(Cover cover)
    {
        calls.Add("save-start");
        if (AddFailure is not null) throw AddFailure;
        if (AddCompletion is not null) await AddCompletion.Task;
        Items.Add(cover);
        calls.Add("save-complete");
    }

    public Task DeleteAsync(string id)
    {
        calls.Add($"delete:{id}");
        Items.RemoveAll(item => item.Id == id);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingAuditer(List<string> calls) : IAuditer
{
    public Exception? Failure { get; set; }

    public void AuditClaim(string id, string httpRequestType)
    {
        calls.Add($"audit-claim:{httpRequestType}:{id}");
        if (Failure is not null) throw Failure;
    }

    public void AuditCover(string id, string httpRequestType)
    {
        calls.Add($"audit-cover:{httpRequestType}:{id}");
        if (Failure is not null) throw Failure;
    }
}
