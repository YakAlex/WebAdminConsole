using MediatR;

namespace AdminConsole.Tests.Controllers;

/// <summary>
/// Hand-rolled IMediator test double — records every published notification.
/// Send/CreateStream throw: no controller under test in this project calls
/// them, and a real MediatR pipeline is unnecessary ceremony for what's
/// really just "did Publish get called with X".
/// </summary>
public sealed class RecordingMediator : IMediator
{
    public List<INotification> Published { get; } = [];

    public Task Publish(object notification, CancellationToken cancellationToken = default)
    {
        Published.Add((INotification)notification);
        return Task.CompletedTask;
    }

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        Published.Add(notification);
        return Task.CompletedTask;
    }

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("RecordingMediator only supports Publish.");

    public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("RecordingMediator only supports Publish.");

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
        throw new NotSupportedException("RecordingMediator only supports Publish.");

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("RecordingMediator only supports Publish.");

    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("RecordingMediator only supports Publish.");
}
