using System.Collections.Immutable;
using System.Reflection;
using Dapper.Contrib.Extensions;
using Jattac.Libraries.QBuilder;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Database.Models;

namespace ShipRight.Database;

public static class AppQBuilderExtensions
{
    public static QBuilder HandleDeletedRecords<TModel>(this QBuilder qBuilder, bool showDeleted)
        where TModel : Model
    {
        if (!showDeleted)
        {
            qBuilder
                .UseTableBoundFilter<TModel>()
                .WhereEqualTo(table => table.Deleted, 0);
        }
        return qBuilder;
    }

    public static QBuilder HandleCompanyFilter<TModel>(this QBuilder qBuilder, Guid companyId)
        where TModel : CompanyScopedModel
    {
        qBuilder
            .UseTableBoundFilter<TModel>()
            .WhereEqualTo((TModel m) => m.CompanyId, companyId);
        return qBuilder;
    }

    public static async Task<TModel?> GetSingleAsync<TModel>(
        this QBuilder qBuilder,
        IDatabaseHelper<Guid> databaseHelper)
        where TModel : Model
    {
        var results = await GetManyAsync<TModel>(qBuilder, databaseHelper);
        if (results == null || results.Count == 0) return null;
        if (results.Count > 1) throw new Exception($"Too many results ({results.Count}), expected only 1 record");
        return results.SingleOrDefault();
    }

    public static async Task<ImmutableList<TModel>> GetManyAsync<TModel>(
        this QBuilder qBuilder,
        IDatabaseHelper<Guid> databaseHelper)
        where TModel : Model
    {
        var builtQuery = qBuilder.BuildWithParameters();
        return await databaseHelper.GetManyAsync<TModel>(
            query: builtQuery.ParameterizedSql,
            parameters: builtQuery.Parameters);
    }

    public static string ResolveTableName(Type type)
    {
        var attr = type.GetCustomAttribute<TableAttribute>();
        if (attr?.Name != null) return attr.Name;
        var name = type.Name;
        if (name.EndsWith("Record")) return name[..^6];
        if (name.EndsWith("Dto")) return name[..^3];
        return name;
    }

    public static QBuilder NewQBuilder()
    {
        return new QBuilder(ResolveTableName, "t", parameterize: true);
    }

    public static QBuilder SelectCountAs<TModel>(this QBuilder qBuilder, string fieldAlias = "Count")
    {
        qBuilder
            .UseSelector()
            .SelectExplicit(
                ResolveTableName(typeof(TModel)),
                $"COUNT(*) AS {fieldAlias}",
                string.Empty,
                string.Empty,
                preventTableNameAliasing: true,
                qualifyFieldWithTableName: false)
            .Then();
        return qBuilder;
    }

    public static QBuilder GetQBuilder()
    {
        return NewQBuilder();
    }
}
