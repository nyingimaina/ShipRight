using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Database;

namespace ShipRight.Tests.Database;

[TestClass]
public class SerializedDatabaseHelperTests
{
    private class FakeInnerHelper : IDatabaseHelper<Guid>
    {
        private int _activeOps;
        private bool _inTransaction;
        private int _maxConcurrentOps;

        public int MaxConcurrentOps => _maxConcurrentOps;
        public int NestingViolations { get; private set; }

        public void BeginTransaction()
        {
            if (_inTransaction)
                NestingViolations++;
            _inTransaction = true;
        }

        public void CommitTransaction() => _inTransaction = false;

        public void RollBackTransaction() => _inTransaction = false;

        public async Task<int> ExecuteAsync(string sql, object param = null)
        {
            var now = Interlocked.Increment(ref _activeOps);
            var max = Volatile.Read(ref _maxConcurrentOps);
            while (now > max)
            {
                var prev = Interlocked.CompareExchange(ref _maxConcurrentOps, now, max);
                if (prev == max) break;
                max = Volatile.Read(ref _maxConcurrentOps);
            }
            await Task.Delay(10);
            Interlocked.Decrement(ref _activeOps);
            return 1;
        }

        public Task<T> ExecuteScalarAsync<T>(string sql, object param = null) => Task.FromResult(default(T))!;

        public Task<ImmutableList<TModel>> GetManyAsync<TModel>(string query, Dictionary<string, object> parameters = null)
            => Task.FromResult(ImmutableList<TModel>.Empty);

        public Task<TModel> GetSingleAsync<TModel>(string query, Dictionary<string, object> parameters = null)
            where TModel : ModelBase<Guid> => Task.FromResult<TModel>(null)!;

        public Task SaveAsync<TModel>(TModel model, bool isUpdate) where TModel : ModelBase<Guid> => Task.CompletedTask;

        public void Dispose() { }
    }

    [TestMethod]
    public async Task ConcurrentMixedRequests_AreSerialized_NeverOverlapOnSharedConnection()
    {
        var inner = new FakeInnerHelper();
        var helper = new SerializedDatabaseHelper<Guid>(inner);

        // Simulates a browser firing concurrent GETs (reads) and POSTs (transactional writes)
        // through one shared singleton helper. MySql.Data cannot serve two commands on one
        // connection at once, so operations must be serialized.
        var tasks = Enumerable.Range(0, 32).Select(async i =>
        {
            if (i % 3 == 0)
            {
                helper.BeginTransaction();
                await helper.ExecuteAsync("insert into t values (1)");
                await helper.ExecuteAsync("insert into t values (2)");
                helper.CommitTransaction();
            }
            else
            {
                await helper.ExecuteAsync("select * from t");
            }
        });

        await Task.WhenAll(tasks);

        Assert.AreEqual(1, inner.MaxConcurrentOps, "Two commands executed concurrently on the shared connection.");
        Assert.AreEqual(0, inner.NestingViolations, "Two transactions were active at the same time.");
    }

    private sealed class ScriptedHelper : IDatabaseHelper<Guid>
    {
        public Func<Task<int>> Execute { get; init; } = () => Task.FromResult(1);
        public Action? Begin { get; init; }
        public Action? Commit { get; init; }
        public bool Disposed { get; private set; }

        public void BeginTransaction() => Begin?.Invoke();
        public void CommitTransaction() => Commit?.Invoke();
        public void RollBackTransaction() { }

        public Task<int> ExecuteAsync(string sql, object param = null) => Execute();
        public Task<T> ExecuteScalarAsync<T>(string sql, object param = null) => Task.FromResult(default(T))!;
        public Task<ImmutableList<TModel>> GetManyAsync<TModel>(string query, Dictionary<string, object> parameters = null)
            => Task.FromResult(ImmutableList<TModel>.Empty);
        public Task<TModel> GetSingleAsync<TModel>(string query, Dictionary<string, object> parameters = null)
            where TModel : ModelBase<Guid> => Task.FromResult<TModel>(null)!;
        public Task SaveAsync<TModel>(TModel model, bool isUpdate) where TModel : ModelBase<Guid> => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }

    private static InvalidOperationException ConnectionLost() => new("The connection is not open.");

    [TestMethod]
    public async Task ConnectionFailure_OnQuery_ReconnectsAndRetriesOnce()
    {
        var created = 0;
        var first = new ScriptedHelper { Execute = () => throw ConnectionLost() };
        var second = new ScriptedHelper();
        var helper = new SerializedDatabaseHelper<Guid>(() =>
            ++created == 1 ? first : second);

        var result = await helper.ExecuteAsync("select 1");

        Assert.AreEqual(1, result);
        Assert.AreEqual(2, created, "a fresh integrator is created after the connection is lost");
        Assert.IsTrue(first.Disposed);
    }

    [TestMethod]
    public void ConnectionFailure_OnBegin_ReconnectsAndStartsTheTransaction()
    {
        var created = 0;
        var first = new ScriptedHelper { Begin = () => throw ConnectionLost() };
        var second = new ScriptedHelper();
        var helper = new SerializedDatabaseHelper<Guid>(() =>
            ++created == 1 ? first : second);

        helper.BeginTransaction();
        helper.CommitTransaction();

        Assert.AreEqual(2, created);
    }

    [TestMethod]
    public async Task OtherFailures_AreNotRetried()
    {
        var created = 0;
        var first = new ScriptedHelper { Execute = () => throw new InvalidOperationException("duplicate key") };
        var helper = new SerializedDatabaseHelper<Guid>(() =>
            ++created == 1 ? first : new ScriptedHelper());

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => helper.ExecuteAsync("insert ..."));

        Assert.AreEqual(1, created, "a bad query must not be retried on a new connection");
    }

    [TestMethod]
    public async Task CommitConnectionFailure_DoesNotOverReleaseTheGate()
    {
        var created = 0;
        var first = new ScriptedHelper { Commit = () => throw ConnectionLost() };
        var second = new ScriptedHelper();
        var helper = new SerializedDatabaseHelper<Guid>(() =>
            ++created == 1 ? first : second);

        helper.BeginTransaction();
        Assert.ThrowsException<InvalidOperationException>(() => helper.CommitTransaction());

        // The middleware calls rollback after a failed commit; that must be a no-op, not a SemaphoreFullException.
        helper.RollBackTransaction();

        Assert.AreEqual(1, await helper.ExecuteAsync("select 1"));
        Assert.AreEqual(2, created);
    }
}
