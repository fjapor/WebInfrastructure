﻿namespace Skeleton.Queues.Abstractions.QueuesFactory
{
    using System;
    using System.Runtime.ExceptionServices;
    using System.Threading;
    using System.Threading.Tasks;
    using ExceptionsHandling;
    using ExceptionsHandling.Handlers;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;

    public abstract class QueueBase : IDisposable
    {
        private readonly int _retriesCount;
        private readonly TimeSpan _retryInitialTimeout;

        protected readonly ILogger Logger;
        protected bool Disposed;

        protected async Task RetryAsync(Func<Task> action, CancellationToken cancellationToken)
        {
            var succeeded = false;
            ExceptionDispatchInfo lastExceptionDispatchInfo = null;

            for (var i = 0; i < _retriesCount + 1 && cancellationToken.IsCancellationRequested == false; i++)
            {
                try
                {
                    await action();

                    succeeded = true;
                    break;
                }
                catch (Exception e)
                {
                    lastExceptionDispatchInfo = ExceptionDispatchInfo.Capture(e);
                }

                if (i != _retriesCount - 1)
                    await Task.Delay(_retryInitialTimeout, cancellationToken);
            }

            if (succeeded == false)
                lastExceptionDispatchInfo?.Throw();
        }

        internal QueueBase(
            int retriesCount,
            TimeSpan retryInitialTimeout,
            ILogger logger)
        {
            if (logger == null)
                throw new ArgumentNullException(nameof(logger));

            _retriesCount = retriesCount;
            _retryInitialTimeout = retryInitialTimeout;

            Logger = logger;
        }

        protected abstract void DisposeInternal(bool disposing);

        public abstract Task SendMessageAsync<TMessage>(
            TMessage message,
            CancellationToken cancellationToken = default(CancellationToken));

        public abstract Task SubscribeAsync<TMessage>(
            IMessageHandler<TMessage> handler,
            CancellationToken cancellationToken = default(CancellationToken));

        public void Dispose()
        {
            DisposeInternal(true);
            GC.SuppressFinalize(this);
        }
    }

    public abstract class QueueBase<TMessageDescription> : QueueBase
        where TMessageDescription : QueueMessageDescriptionBase, new()
    {
        protected readonly ExceptionHandlerBase<TMessageDescription> ExceptionHandler;
        public ITypedQueue<ExceptionDescription> ErrorsQueue { get; }

        protected QueueBase(
            int retriesCount,
            TimeSpan retryInitialTimeout,
            ITypedQueue<ExceptionDescription> errorsQueue,
            ExceptionHandlerBase<TMessageDescription> exceptionHandler,
            ILogger logger) : base(retriesCount, retryInitialTimeout, logger)
        {
            if (exceptionHandler == null)
                throw new ArgumentNullException(nameof(exceptionHandler));

            ExceptionHandler = exceptionHandler;
            ErrorsQueue = errorsQueue;
        }

        protected abstract Task SendMessageInternalAsync(TMessageDescription messageDescription, CancellationToken cancellationToken);

        protected abstract Task SubscribeInternalAsync(Func<TMessageDescription, Task> messageHandler, CancellationToken cancellationToken);

        public async Task SendMessageAsync(
            TMessageDescription messageDescription,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            await RetryAsync(() => SendMessageInternalAsync(messageDescription, cancellationToken), cancellationToken);
        }

        public override async Task SendMessageAsync<TMessage>(
            TMessage message, 
            CancellationToken cancellationToken = default(CancellationToken))
        {
            await SendMessageAsync(
                new TMessageDescription
                {
                    Id = Guid.NewGuid().ToString(),
                    Content = JsonConvert.SerializeObject(message)
                },
                cancellationToken
            );
        }

        public override async Task SubscribeAsync<TMessage>(
            IMessageHandler<TMessage> handler, 
            CancellationToken cancellationToken = default(CancellationToken))
        {
            await SubscribeInternalAsync(
                async messageDescription =>
                {
                    try
                    {
                        var message = JsonConvert.DeserializeObject<TMessage>(messageDescription.Content);
                        await RetryAsync(() => handler.Handle(message, cancellationToken), cancellationToken);
                        await AcknowledgeMessageAsync(messageDescription, cancellationToken);
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e, $"Error has been occurred during processing the message, Id {messageDescription.Id}");
                        await ExceptionHandler.HandleAsync(this, messageDescription, e, cancellationToken);
                    }
                },
                cancellationToken
            );
        }

        public abstract Task AcknowledgeMessageAsync(
            TMessageDescription messageDescription,
            CancellationToken cancellationToken = default(CancellationToken));

        public abstract Task RejectMessageAsync(
            TMessageDescription messageDescription,
            CancellationToken cancellationToken = default(CancellationToken));
    }
}