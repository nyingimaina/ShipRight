using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Database;

namespace ShipRight.Tests.Database;

[TestClass]
public class DatabaseTransactionMiddlewareTests
{
    private class RecordingDatabaseHelper : IDatabaseHelper<Guid>
    {
        private bool _inTransaction;

        public int BeginCount { get; private set; }
        public int CommitCount { get; private set; }
        public int RollbackCount { get; private set; }

        public void BeginTransaction()
        {
            if (_inTransaction)
                throw new DatabaseIntegratorException("A database transaction is already in progress. Cannot nest transactions");
            _inTransaction = true;
            BeginCount++;
        }

        public void CommitTransaction()
        {
            _inTransaction = false;
            CommitCount++;
        }

        public void RollBackTransaction()
        {
            _inTransaction = false;
            RollbackCount++;
        }

        public Task<int> ExecuteAsync(string sql, object param = null) => Task.FromResult(0);

        public Task<T> ExecuteScalarAsync<T>(string sql, object param = null) => Task.FromResult(default(T))!;

        public Task<ImmutableList<TModel>> GetManyAsync<TModel>(string query, Dictionary<string, object> parameters = null)
            => Task.FromResult(ImmutableList<TModel>.Empty);

        public Task<TModel> GetSingleAsync<TModel>(string query, Dictionary<string, object> parameters = null)
            where TModel : ModelBase<Guid> => Task.FromResult<TModel>(null)!;

        public Task SaveAsync<TModel>(TModel model, bool isUpdate) where TModel : ModelBase<Guid> => Task.CompletedTask;

        public void Dispose() { }
    }

    private static DatabaseTransactionMiddleware Middleware(Action nextBody)
        => new(_ =>
        {
            nextBody();
            return Task.CompletedTask;
        });

    private static DefaultHttpContext Context(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return context;
    }

    [TestMethod]
    public async Task InvokeAsync_GetRequestToStaticSpaPath_DoesNotBeginTransaction()
    {
        var helper = new RecordingDatabaseHelper();
        var invoked = false;
        var middleware = Middleware(() => invoked = true);

        await middleware.InvokeAsync(Context(HttpMethods.Get, "/login/"), helper);

        Assert.IsTrue(invoked);
        Assert.AreEqual(0, helper.BeginCount);
        Assert.AreEqual(0, helper.CommitCount);
        Assert.AreEqual(0, helper.RollbackCount);
    }

    [TestMethod]
    public async Task InvokeAsync_GetApiRequest_DoesNotBeginTransaction()
    {
        var helper = new RecordingDatabaseHelper();
        var middleware = Middleware(() => { });

        await middleware.InvokeAsync(Context(HttpMethods.Get, "/api/projects"), helper);

        Assert.AreEqual(0, helper.BeginCount);
        Assert.AreEqual(0, helper.CommitCount);
    }

    [TestMethod]
    public async Task InvokeAsync_PostApiRequest_BeginsAndCommitsTransaction()
    {
        var helper = new RecordingDatabaseHelper();
        var middleware = Middleware(() => { });

        await middleware.InvokeAsync(Context(HttpMethods.Post, "/api/auth/login"), helper);

        Assert.AreEqual(1, helper.BeginCount);
        Assert.AreEqual(1, helper.CommitCount);
        Assert.AreEqual(0, helper.RollbackCount);
    }

    [TestMethod]
    public async Task InvokeAsync_PostToExcludedPrefix_DoesNotBeginTransaction()
    {
        var helper = new RecordingDatabaseHelper();
        var middleware = Middleware(() => { });

        await middleware.InvokeAsync(Context(HttpMethods.Post, "/api/health"), helper);

        Assert.AreEqual(0, helper.BeginCount);
    }

    [TestMethod]
    public async Task InvokeAsync_PostApiRequest_WhenHandlerThrows_RollsBackAndRethrows()
    {
        var helper = new RecordingDatabaseHelper();
        var middleware = Middleware(() => throw new InvalidOperationException("boom"));

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(Context(HttpMethods.Post, "/api/auth/login"), helper));

        Assert.AreEqual("boom", ex.Message);
        Assert.AreEqual(1, helper.BeginCount);
        Assert.AreEqual(0, helper.CommitCount);
        Assert.AreEqual(1, helper.RollbackCount);
    }

    [TestMethod]
    public async Task InvokeAsync_PostApiRequest_SkipsTransactionForConcurrentReads_SharedHelperNeverNests()
    {
        var helper = new RecordingDatabaseHelper();
        var middleware = Middleware(() => { });

        // Simulates a browser firing many parallel requests through one shared singleton helper.
        var tasks = Enumerable.Range(0, 32).Select(i =>
            middleware.InvokeAsync(Context(i % 2 == 0 ? HttpMethods.Get : HttpMethods.Post, "/api/projects"), helper));
        await Task.WhenAll(tasks);

        Assert.AreEqual(0, helper.RollbackCount);
    }
}
