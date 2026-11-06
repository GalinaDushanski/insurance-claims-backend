using System.Threading.Channels;

namespace Claims.Auditing;

public sealed class AuditQueue
{
    private readonly Channel<AuditEvent> _channel = Channel.CreateBounded<AuditEvent>(
        new BoundedChannelOptions(1000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    public ValueTask EnqueueAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(auditEvent, cancellationToken);

    public IAsyncEnumerable<AuditEvent> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public void Complete() => _channel.Writer.TryComplete();
}
