using System.Collections.Immutable;
using System.Net.Sockets;
using MySql.Data.MySqlClient;
using Rocket.Libraries.DatabaseIntegrator;
using Serilog;

namespace ShipRight.Database;

/// <summary>
/// Serializes access to the single database connection the integrator keeps, and recovers when that connection
/// is dropped (idle timeout, database restart, network reset). Without the recovery, one dropped connection
/// poisons every later request with "The connection is not open".
/// </summary>
public class SerializedDatabaseHelper<TIdentifier> : IDatabaseHelper<TIdentifier>
{
    private readonly Func<IDatabaseHelper<TIdentifier>> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<bool> _ownsTransaction = new();
    private IDatabaseHelper<TIdentifier> _inner;
    private bool _disposed;

    public SerializedDatabaseHelper(IDatabaseHelper<TIdentifier> inner) : this(() => inner) { }

    public SerializedDatabaseHelper(Func<IDatabaseHelper<TIdentifier>> factory)
    {
        _factory = factory;
        _inner = factory();
    }

    public void BeginTransaction()
    {
        _gate.Wait();
        try
        {
            try
            {
                _inner.BeginTransaction();
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                ReplaceInner(ex);
                _inner.BeginTransaction();
            }

            _ownsTransaction.Value = true;
        }
        catch
        {
            _ownsTransaction.Value = false;
            _gate.Release();
            throw;
        }
    }

    public void CommitTransaction()
    {
        if (!_ownsTransaction.Value) return;

        try
        {
            _inner.CommitTransaction();
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            // The transaction died with the connection; a fresh connection cannot commit it.
            ReplaceInner(ex);
            throw;
        }
        finally
        {
            _ownsTransaction.Value = false;
            _gate.Release();
        }
    }

    public void RollBackTransaction()
    {
        if (!_ownsTransaction.Value) return;

        try
        {
            _inner.RollBackTransaction();
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            ReplaceInner(ex);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Database rollback failed");
        }
        finally
        {
            _ownsTransaction.Value = false;
            _gate.Release();
        }
    }

    public Task<int> ExecuteAsync(string sql, object param = null) =>
        RunAsync(inner => inner.ExecuteAsync(sql, param));

    public Task<T> ExecuteScalarAsync<T>(string sql, object param = null) =>
        RunAsync(inner => inner.ExecuteScalarAsync<T>(sql, param));

    public Task<ImmutableList<TModel>> GetManyAsync<TModel>(string query, Dictionary<string, object> parameters = null) =>
        RunAsync(inner => inner.GetManyAsync<TModel>(query, parameters));

    public Task<TModel> GetSingleAsync<TModel>(string query, Dictionary<string, object> parameters = null)
        where TModel : ModelBase<TIdentifier> =>
        RunAsync(inner => inner.GetSingleAsync<TModel>(query, parameters));

    public Task SaveAsync<TModel>(TModel model, bool isUpdate) where TModel : ModelBase<TIdentifier> =>
        RunAsync(async inner =>
        {
            await inner.SaveAsync(model, isUpdate);
            return true;
        });

    /// <summary>
    /// A transaction is already holding the gate, so the retry happens in place; otherwise the work is serialized
    /// and retried once on a fresh connection after a connection-level failure.
    /// </summary>
    private async Task<T> RunAsync<T>(Func<IDatabaseHelper<TIdentifier>, Task<T>> work)
    {
        if (_ownsTransaction.Value)
            return await work(_inner);

        await _gate.WaitAsync();
        try
        {
            try
            {
                return await work(_inner);
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                ReplaceInner(ex);
                return await work(_inner);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReplaceInner(Exception reason)
    {
        Log.Warning("Database connection lost; opening a new one ({Reason})", reason.Message);

        var old = _inner;
        try { old.Dispose(); } catch { /* best-effort */ }

        _inner = _factory();
    }

    /// <summary>True only for failures that mean "this connection is gone", never for a bad query or a duplicate key.</summary>
    internal static bool IsConnectionFailure(Exception ex)
    {
        if (ex is MySqlException mysql)
        {
            // 2002/2003 cannot connect, 2006/2013/2026/2055 connection lost, 1040 too many connections,
            // 1152-1161 aborted/killed, 1927/4031 idle timeout.
            return mysql.Number is 2002 or 2003 or 2006 or 2013 or 2026 or 2055
                or 1040 or 1041 or 1042
                or 1152 or 1153 or 1154 or 1155 or 1156 or 1157 or 1158 or 1159 or 1160 or 1161
                or 1927 or 4031;
        }

        if (ex is IOException or SocketException) return true;

        if (ex is InvalidOperationException invalid)
        {
            return invalid.Message.Contains("connection is not open", StringComparison.OrdinalIgnoreCase)
                || invalid.Message.Contains("connection must be valid", StringComparison.OrdinalIgnoreCase)
                || invalid.Message.Contains("connection was not open", StringComparison.OrdinalIgnoreCase)
                || invalid.Message.Contains("connection is closed", StringComparison.OrdinalIgnoreCase);
        }

        return ex.InnerException is not null && IsConnectionFailure(ex.InnerException);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _inner.Dispose(); } catch { /* best-effort */ }
        _gate.Dispose();
    }
}
