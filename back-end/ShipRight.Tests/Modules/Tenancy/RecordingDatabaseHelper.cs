using System.Collections.Immutable;
using Rocket.Libraries.DatabaseIntegrator;

namespace ShipRight.Tests.Modules.Tenancy;

// A database stand-in that remembers every statement and its parameters, so tests can prove that the company
// filter is part of the SQL itself. Reads return the rows queued with Queue<T>, nothing, or (when asked)
// one blank row of whatever type the store reads, which lets "it already exists" paths run.
public sealed class RecordingDatabaseHelper : IDatabaseHelper<Guid>
{
    private readonly Dictionary<Type, object> _rows = new();

    public List<(string Sql, object? Parameters)> Statements { get; } = [];

    public string LastSql => Statements[^1].Sql;

    // Every read returns one row built with its parameterless constructor ("Data" becomes "{}").
    public bool ReturnOneBlankRow { get; set; }

    // Lets a test shape the blank row (for example set Role = "Admin").
    public Action<object>? CustomizeBlankRow { get; set; }

    // Rows reported as changed by every write; 0 simulates "the guarded UPDATE matched nothing".
    public int ExecuteResult { get; set; } = 1;

    // The answer to "which company owns this project?" (used by background writes).
    public Guid? CompanyOfProject { get; set; }

    public void Queue<T>(params T[] rows) => _rows[typeof(T)] = rows.ToList();

    public static IReadOnlyDictionary<string, object?> ParametersOf(object? parameters) => parameters switch
    {
        null => new Dictionary<string, object?>(),
        IDictionary<string, object> dictionary => dictionary.ToDictionary(p => p.Key, p => (object?)p.Value),
        _ => parameters.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(parameters)),
    };

    public void BeginTransaction() { }
    public void CommitTransaction() { }
    public void RollBackTransaction() { }

    public Task<int> ExecuteAsync(string sql, object param = null!)
    {
        Statements.Add((sql, param));
        return Task.FromResult(ExecuteResult);
    }

    public Task<T> ExecuteScalarAsync<T>(string sql, object param = null!)
    {
        Statements.Add((sql, param));
        return Task.FromResult(default(T)!);
    }

    public Task<ImmutableList<TModel>> GetManyAsync<TModel>(string query, Dictionary<string, object> parameters = null!)
    {
        Statements.Add((query, parameters));

        if (CompanyOfProject is { } company && query.Contains("FROM Project WHERE", StringComparison.Ordinal))
        {
            var owner = Activator.CreateInstance<TModel>();
            typeof(TModel).GetProperty("CompanyId")!.SetValue(owner, company);
            return Task.FromResult(ImmutableList.Create(owner));
        }

        if (_rows.TryGetValue(typeof(TModel), out var rows))
            return Task.FromResult(((List<TModel>)rows).ToImmutableList());

        if (ReturnOneBlankRow)
        {
            var row = Activator.CreateInstance<TModel>();
            typeof(TModel).GetProperty("Data")?.SetValue(row, "{}");
            CustomizeBlankRow?.Invoke(row!);
            return Task.FromResult(ImmutableList.Create(row));
        }

        return Task.FromResult(ImmutableList<TModel>.Empty);
    }

    public Task<TModel> GetSingleAsync<TModel>(string query, Dictionary<string, object> parameters = null!)
        where TModel : ModelBase<Guid>
    {
        Statements.Add((query, parameters));
        return Task.FromResult<TModel>(null!);
    }

    public Task SaveAsync<TModel>(TModel model, bool isUpdate) where TModel : ModelBase<Guid> => Task.CompletedTask;

    public void Dispose() { }
}
