using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StackExchange.Redis;
using RedLockNet;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;

namespace ecommerce.Infrastructure
{
    public class RedisDistributedLockProvider : IDistributedLockProvider
    {
        private readonly RedLockFactory _lockFactory;

        public RedisDistributedLockProvider(IConnectionMultiplexer connection)
        {
            var multiplexers = new List<RedLockMultiplexer> { new RedLockMultiplexer(connection) };
            _lockFactory = RedLockFactory.Create(multiplexers);
        }

        public async Task<IDistributedLock> AcquireLockAsync(string resource, TimeSpan expiry)
        {
            var redLock = await _lockFactory.CreateLockAsync(
                resource,
                expiry,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(100));

            return new DistributedLockWrapper(redLock);
        }

        private class DistributedLockWrapper : IDistributedLock
        {
            private readonly IRedLock _redLock;

            public bool IsAcquired => _redLock.IsAcquired;

            public DistributedLockWrapper(IRedLock redLock) => _redLock = redLock;

            public ValueTask DisposeAsync()
            {
                if (_redLock.IsAcquired)
                {
                    _redLock.Dispose();
                }
                GC.SuppressFinalize(this);
                return ValueTask.CompletedTask;
            }
        }
    }

    public interface IDistributedLock : IAsyncDisposable
    {
        bool IsAcquired { get; }
    }

    public interface IDistributedLockProvider
    {
        Task<IDistributedLock> AcquireLockAsync(string resource, TimeSpan expiry);
    }
}