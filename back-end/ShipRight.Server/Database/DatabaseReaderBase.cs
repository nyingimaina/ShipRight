using System.Collections.Immutable;
using System.Linq.Expressions;
using Jattac.Libraries.QBuilder;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Database.Models;

namespace ShipRight.Database;

public interface IDatabaseReaderBase<TModel> : IReaderBase<TModel, Guid>
    where TModel : Model
{
    Task<ImmutableList<TModel>> GetPageableAsync<TPageField>(
        int? page = null,
        ushort? pageSize = null,
        Expression<Func<TModel, TPageField>>? pagingField = null,
        bool orderAscending = true,
        Action<QBuilder>? onBeforeQuery = null,
        DeletedFilter deletedFilter = DeletedFilter.ExcludeDeleted);

    Task<ImmutableList<TModel>> GetManyByIdsAsync(
        IEnumerable<Guid> ids,
        Action<QBuilder>? onBeforeQuery = null);
}

public abstract class DatabaseReaderBase<TModel> : ReaderBase<TModel, Guid>
    where TModel : Model
{
    protected DatabaseReaderBase(IDatabaseHelper<Guid> databaseHelper)
        : base(databaseHelper)
    {
    }

    public override async Task<TModel> GetByIdAsync(Guid id, bool? showDeleted)
    {
        var models = await GetManyByIdsAsync(new List<Guid> { id }, onBeforeQuery: qBuilder =>
        {
            if (!showDeleted.HasValue || (showDeleted.HasValue && showDeleted.Value == false))
            {
                qBuilder
                    .UseTableBoundFilter<TModel>()
                    .WhereEqualTo(tModel => tModel.Deleted, 0);
            }
        });

        return models.FirstOrDefault()
            ?? throw new Exception($"No {typeof(TModel).Name} with id {id} found");
    }

    public virtual async Task<ImmutableList<TModel>> GetManyByIdsAsync(
        IEnumerable<Guid> ids,
        Action<QBuilder>? onBeforeQuery = null)
    {
        if (ids == null) return ImmutableList<TModel>.Empty;

        var listOfIds = ids.ToList();
        if (listOfIds.Count == 0) return ImmutableList<TModel>.Empty;

        using var qBuilder = AppQBuilderExtensions.NewQBuilder();
        qBuilder
            .UseSelector()
            .Select<TModel>("*")
            .Then()
            .UseTableBoundFilter<TModel>()
            .WhereIn(tModel => tModel.Id, listOfIds);

        onBeforeQuery?.Invoke(qBuilder);

        var builtQuery = qBuilder.BuildWithParameters();
        return await DatabaseHelper.GetManyAsync<TModel>(
            query: builtQuery.ParameterizedSql,
            parameters: builtQuery.Parameters);
    }

    public virtual async Task<ImmutableList<TModel>> GetPageableAsync<TPageField>(
        int? page = null,
        ushort? pageSize = null,
        Expression<Func<TModel, TPageField>>? pagingField = null,
        bool orderAscending = true,
        Action<QBuilder>? onBeforeQuery = null,
        DeletedFilter deletedFilter = DeletedFilter.ExcludeDeleted)
    {
        using var qBuilder = AppQBuilderExtensions.NewQBuilder();

        qBuilder
            .UseSelector()
            .Select<TModel>("*")
            .Then();

        var pageVal = (uint)(page ?? 1);
        var pageSizeVal = pageSize ?? ushort.MaxValue;

        if (pagingField == null)
        {
            qBuilder
                .UseMySqlServerPagingBuilder<TModel>()
                .PageBy(
                    tModel => tModel.Created,
                    page: pageVal,
                    pageSize: pageSizeVal,
                    orderAscending: orderAscending);
        }
        else
        {
            qBuilder
                .UseMySqlServerPagingBuilder<TModel>()
                .PageBy(
                    pagingField,
                    page: pageVal,
                    pageSize: pageSizeVal,
                    orderAscending: orderAscending);
        }

        if (deletedFilter != DeletedFilter.IncludeDeleted)
        {
            var deletedValue = deletedFilter == DeletedFilter.OnlyDeleted ? 1 : 0;
            qBuilder
                .UseTableBoundFilter<TModel>()
                .WhereEqualTo(m => m.Deleted, deletedValue);
        }

        onBeforeQuery?.Invoke(qBuilder);

        var builtQuery = qBuilder.BuildWithParameters();
        return await DatabaseHelper.GetManyAsync<TModel>(
            query: builtQuery.ParameterizedSql,
            parameters: builtQuery.Parameters);
    }

    protected void ApplyTenantFilter(QBuilder qBuilder, Guid companyId)
    {
        if (typeof(CompanyScopedModel).IsAssignableFrom(typeof(TModel)))
        {
            qBuilder
                .UseTableBoundFilter<TModel>()
                .WhereEqualTo(m => ((CompanyScopedModel)(object)m).CompanyId, companyId);
        }
    }
}
