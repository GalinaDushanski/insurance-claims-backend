using Claims.Data;
using Claims.Repositories;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using Xunit;

namespace Claims.Tests.Unit;

public class RepositoryCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Add_passes_cancellation_to_the_inflight_database_save(bool cover)
    {
        using var timeout = CreateTimeout();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var context = new CapturingClaimsContext();

        var saving = AddAsync(context, cover, cancellation.Token);
        Assert.False(saving.IsCompleted);
        Assert.Equal(cancellation.Token, context.SavedToken);
        Assert.Equal(EntityState.Added, Assert.Single(context.ChangeTracker.Entries()).State);
        await cancellation.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving.WaitAsync(timeout.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(context.SaveCompletion.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Already_cancelled_add_does_not_track_or_save_an_entity(bool cover)
    {
        using var timeout = CreateTimeout();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await using var context = new CapturingClaimsContext();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AddAsync(context, cover, cancellation.Token).WaitAsync(timeout.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);

        Assert.Empty(context.ChangeTracker.Entries());
        Assert.False(context.SavedToken.CanBeCanceled);
    }

    private static CancellationTokenSource CreateTimeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return timeout;
    }

    private static Task AddAsync(ClaimsContext context, bool cover, CancellationToken token) => cover
        ? new CoverRepository(context).AddAsync(new Cover
        {
            Id = "cover-id", StartDate = new DateTime(2030, 1, 1), EndDate = new DateTime(2030, 1, 2)
        }, token)
        : new ClaimRepository(context).AddAsync(new Claim
        {
            Id = "claim-id", CoverId = "cover-id", Name = "Collision", Created = new DateTime(2030, 1, 1)
        }, token);

    // Uses the real MongoDB model; intercept saving so these tests need no database connection.
    private sealed class CapturingClaimsContext() : ClaimsContext(Options)
    {
        private static readonly DbContextOptions<ClaimsContext> Options = new DbContextOptionsBuilder<ClaimsContext>()
            .UseMongoDB(new MongoClient("mongodb://localhost:27017"), "cancellation-tests").Options;

        public CancellationToken SavedToken { get; private set; }
        public TaskCompletionSource SaveCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SavedToken = cancellationToken;
            await SaveCompletion.Task.WaitAsync(cancellationToken);
            return 1;
        }
    }
}
