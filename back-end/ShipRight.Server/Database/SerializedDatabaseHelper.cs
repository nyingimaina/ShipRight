using System.Collections.Immutable;
using Rocket.Libraries.DatabaseIntegrator;

namespace ShipRight.Database;

public class SerializedDatabaseHelper<TIdentifier> : IDatabaseHelper<TIdentifier>
{
    private readonly IDatabaseHelper<TIdentifier> _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<bool> _ownsTransaction = new();

    public SerializedDatabaseHelper(IDatabaseHelper<TIdentifier> inner)
    {
        _inner = inner;
    }

    public void BeginTransaction()
    {
        _gate.Wait();
        try
        {
            _inner.BeginTransaction();
            _ownsTransaction.Value = true;
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    public void CommitTransaction()
    {
        try
        {
            _inner.CommitTransaction();
        }
        finally
        {
            _ownsTransaction.Value = false;
            _gate.Release();
        }
    }

    public void RollBackTransaction()
    {
        try
        {
            _inner.RollBackTransaction();
        }
        finally
        {
            _ownsTransaction.Value = false;
            _gate.Release();
        }
    }

    public async Task<int> ExecuteAsync(string sql, object param = null)
    {
        if (_ownsTransaction.Value)
            return await _inner.ExecuteAsync(sql, param);
        await _gate.WaitAsync();
        try { return await _inner.ExecuteAsync(sql, param); }
        finally { _gate.Release(); }
    }

    public async Task<T> ExecuteScalarAsync<T>(string sql, object param = null)
    {
        if (_ownsTransaction.Value)
            return await _inner.ExecuteScalarAsync<T>(sql, param);
        await _gate.WaitAsync();
        try { return await _inner.ExecuteScalarAsync<T>(sql, param); }
        finally { _gate.Release(); }
    }

    public async Task<ImmutableList<TModel>> GetManyAsync<TModel>(string query, Dictionary<string, object> parameters = null)
    {
        if (_ownsTransaction.Value)
            return await _inner.GetManyAsync<TModel>(query, parameters);
        await _gate.WaitAsync();
        try { return await _inner.GetManyAsync<TModel>(query, parameters); }
        finally { _gate.Release(); }
    }

    public async Task<TModel> GetSingleAsync<TModel>(string query, Dictionary<string, object> parameters = null)
        where TModel : ModelBase<TIdentifier>
    {
        if (_ownsTransaction.Value)
            return await _inner.GetSingleAsync<TModel>(query, parameters);
        await _gate.WaitAsync();
        try { return await _inner.GetSingleAsync<TModel>(query, parameters); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync<TModel>(TModel model, bool isUpdate) where TModel : ModelBase<TIdentifier>
    {
        if (_ownsTransaction.Value)
        {
            await _inner.SaveAsync(model, isUpdate);
            return;
        }
        await _gate.WaitAsync();
        try { await _inner.SaveAsync(model, isUpdate); }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _inner.Dispose();
        _gate.Dispose();
    }
}
