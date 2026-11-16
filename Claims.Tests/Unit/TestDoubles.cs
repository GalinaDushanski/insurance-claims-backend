using Claims.Auditing;
using Claims.Repositories;

namespace Claims.Tests.Unit;

internal sealed class FakeClaimRepository(List<string> calls) : IClaimRepository
{
    public List<CancellationToken> Tokens { get; } = [];
    public List<Claim> Items { get; } = [];
    public Action? AfterPersistence { get; set; }
    public TaskCompletionSource? AddCompletion { get; set; }
    public TaskCompletionSource<CancellationToken>? LookupStarted { get; set; }
    public TaskCompletionSource? LookupCompletion { get; set; }
    public Exception? LookupFailure { get; set; }
    public Exception? AddFailure { get; set; }
    public TaskCompletionSource? DeleteCompletion { get; set; }
    public Exception? DeleteFailure { get; set; }

    public Task<IEnumerable<Claim>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        Tokens.Add(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IEnumerable<Claim>>(Items);
    }

    public async Task<Claim?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        Tokens.Add(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        LookupStarted?.TrySetResult(cancellationToken);
        if (LookupCompletion is not null) await LookupCompletion.Task.WaitAsync(cancellationToken);
        if (LookupFailure is not null) throw LookupFailure;
        return Items.SingleOrDefault(item => item.Id == id);
    }

    public async Task AddAsync(Claim claim, CancellationToken cancellationToken = default)
    {
        Tokens.Add(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add("save-start");
        if (AddFailure is not null) throw AddFailure;
        if (AddCompletion is not null) await AddCompletion.Task.WaitAsync(cancellationToken);
        Items.Add(claim);
        calls.Add("save-complete");
        AfterPersistence?.Invoke();
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        Tokens.Add(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add($"delete-start:{id}");
        if (DeleteFailure is not null) throw DeleteFailure;
        if (DeleteCompletion is not null) await DeleteCompletion.Task.WaitAsync(cancellationToken);
        Items.RemoveAll(item => item.Id == id);
        calls.Add($"delete-complete:{id}");
        AfterPersistence?.Invoke();
    }
}

internal sealed class FakeCoverRepository(List<string> calls) : ICoverRepository
{
    public List<CancellationToken> Tokens { get; } = [];
    public List<Cover> Items { get; } = [];
    public Action? AfterPersistence { get; set; }
    public TaskCompletionSource? AddCompletion { get; set; }
    public TaskCompletionSource<CancellationToken>? LookupStarted { get; set; }
    public TaskCompletionSource? LookupCompletion { get; set; }
    public Exception? LookupFailure { get; set; }
    public Exception? AddFailure { get; set; }
    public TaskCompletionSource? DeleteCompletion { get; set; }
    public Exception? DeleteFailure { get; set; }

    public Task<IEnumerable<Cover>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        Tokens.Add(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IEnumerable<Cover>>(Items);
    }

    public async Task<Cover?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        Tokens.Add(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        LookupStarted?.TrySetResult(cancellationToken);
        if (LookupCompletion is not null) await LookupCompletion.Task.WaitAsync(cancellationToken);
        if (LookupFailure is not null) throw LookupFailure;
        return Items.SingleOrDefault(item => item.Id == id);
    }

    public async Task AddAsync(Cover cover, CancellationToken cancellationToken = default)
    {
        Tokens.Add(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add("save-start");
        if (AddFailure is not null) throw AddFailure;
        if (AddCompletion is not null) await AddCompletion.Task.WaitAsync(cancellationToken);
        Items.Add(cover);
        calls.Add("save-complete");
        AfterPersistence?.Invoke();
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        Tokens.Add(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add($"delete-start:{id}");
        if (DeleteFailure is not null) throw DeleteFailure;
        if (DeleteCompletion is not null) await DeleteCompletion.Task.WaitAsync(cancellationToken);
        Items.RemoveAll(item => item.Id == id);
        calls.Add($"delete-complete:{id}");
        AfterPersistence?.Invoke();
    }
}

internal sealed class RecordingAuditer(List<string> calls) : IAuditer
{
    public List<CancellationToken> Tokens { get; } = [];
    public Exception? Failure { get; set; }
    public TaskCompletionSource? PublicationCompletion { get; set; }

    public Task AuditClaimAsync(string id, AuditAction action, CancellationToken cancellationToken = default) =>
        RecordAsync($"audit-claim:{action}:{id}", cancellationToken);

    public Task AuditCoverAsync(string id, AuditAction action, CancellationToken cancellationToken = default) =>
        RecordAsync($"audit-cover:{action}:{id}", cancellationToken);

    private async Task RecordAsync(string call, CancellationToken cancellationToken)
    {
        Tokens.Add(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add(call);
        if (Failure is not null) throw Failure;
        if (PublicationCompletion is not null) await PublicationCompletion.Task.WaitAsync(cancellationToken);
    }
}
