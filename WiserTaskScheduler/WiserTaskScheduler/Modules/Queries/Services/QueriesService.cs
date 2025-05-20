using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Drawing.Diagrams;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using GeeksCoreLibrary.Core.Helpers;
using GeeksCoreLibrary.Modules.Databases.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using WiserTaskScheduler.Core.Enums;
using WiserTaskScheduler.Core.Helpers;
using WiserTaskScheduler.Core.Interfaces;
using WiserTaskScheduler.Core.Models;
using WiserTaskScheduler.Modules.Queries.Interfaces;
using WiserTaskScheduler.Modules.Queries.Models;

namespace WiserTaskScheduler.Modules.Queries.Services
{
    /// <summary>
    /// A service for a query action.
    /// </summary>
    public class QueriesService : IQueriesService, IActionsService, IScopedService
    {
        private readonly ILogService logService;
        private readonly ILogger<QueriesService> logger;
        private readonly IServiceProvider serviceProvider;

        private string connectionString;

        /// <summary>
        /// Creates a new instance of <see cref="QueriesService"/>.
        /// </summary>
        public QueriesService(ILogService logService, ILogger<QueriesService> logger, IServiceProvider serviceProvider)
        {
            this.logService = logService;
            this.logger = logger;
            this.serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public Task InitializeAsync(ConfigurationModel configuration, HashSet<string> tablesToOptimize)
        {
            connectionString = configuration.ConnectionString;

            if (String.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException($"Configuration '{configuration.ServiceName}' has no connection string defined but contains active `Query` actions. Please provide a connection string.");
            }
            
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task<JObject> Execute(ActionModel action, JObject resultSets, string configurationServiceName)
        {
            var query = (QueryModel)action;
            await logService.LogInformation(logger, LogScopes.RunStartAndStop, query.LogSettings, $"Executing query in time id: {query.TimeId}, order: {query.Order}", configurationServiceName, query.TimeId, query.Order);
            
            using var scope = serviceProvider.CreateScope();
            var databaseConnection = scope.ServiceProvider.GetRequiredService<IDatabaseConnection>();

            await databaseConnection.ChangeConnectionStringsAsync(connectionString, connectionString);
            databaseConnection.ClearParameters();
            await databaseConnection.EnsureOpenConnectionForWritingAsync();
            await databaseConnection.EnsureOpenConnectionForReadingAsync();
            databaseConnection.SetCommandTimeout(query.Timeout);

            // Enforce the set character set and collation that is used during the execution of this action.
            databaseConnection.AddParameter("characterSet", query.CharacterEncoding.CharacterSet);
            databaseConnection.AddParameter("collation", query.CharacterEncoding.Collation);
            await databaseConnection.GetAsync("SET NAMES ?characterSet COLLATE ?collation", cleanUp: false);
            databaseConnection.ClearParameters();
            
            if (query.UseTransaction) await databaseConnection.BeginTransactionAsync();

            try
            {
                var result = await Execute(query, databaseConnection, resultSets, configurationServiceName);
                
                if (!query.UseTransaction) return result;
                
                await databaseConnection.CommitTransactionAsync();
                await logService.LogInformation(logger, LogScopes.RunStartAndStop, query.LogSettings, $"Transaction committed in time id: {query.TimeId}, order: {query.Order}", configurationServiceName, query.TimeId, query.Order);
                return result;
            }
            catch
            {
                if (!query.UseTransaction) throw;
                
                await databaseConnection.RollbackTransactionAsync();
                await logService.LogInformation(logger, LogScopes.RunStartAndStop, query.LogSettings, $"Action failed, rolled back transaction in time id: {query.TimeId}, order: {query.Order}", configurationServiceName, query.TimeId, query.Order);
                throw;
            }
        }
        
        private async Task<JObject> Execute(QueryModel query, IDatabaseConnection databaseConnection, JObject resultSets, string configurationServiceName)
        {
            // If not using a result set execute the query as given.
            if (String.IsNullOrWhiteSpace(query.UseResultSet))
            {
                await logService.LogInformation(logger, LogScopes.RunBody, query.LogSettings, $"Query (without resultset): {query.Query}", configurationServiceName, query.TimeId, query.Order);
                var dataTable = await databaseConnection.GetAsync(query.Query);
                await logService.LogInformation(logger, LogScopes.RunBody, query.LogSettings, $"Query results rows: {dataTable.Rows.Count}", configurationServiceName, query.TimeId, query.Order);
                return GetResultSetFromDataTable(dataTable);
            }

            var keyParts = query.UseResultSet.Split('.');
            var remainingKey = keyParts.Length > 1 ? query.UseResultSet.Substring(keyParts[0].Length + 1) : "";
            var tuple = ReplacementHelper.PrepareText(query.Query, (JObject)resultSets[keyParts[0]], remainingKey, query.HashSettings, insertValues: false);
            var queryString = tuple.Item1;
            var parameterKeys = tuple.Item2;
            var insertedParameters = tuple.Item3;

            await logService.LogInformation(logger, LogScopes.RunBody, query.LogSettings, $"Query: {queryString}", configurationServiceName, query.TimeId, query.Order);

            // Perform the query when there are no parameters. Either no values are used from the using result set or all values have been combined and a single query is sufficient.
            if (parameterKeys.Count == 0)
            {
                foreach (var parameter in insertedParameters)
                {
                    databaseConnection.AddParameter(parameter.Key, parameter.Value);
                }

                var dataTable = await databaseConnection.GetAsync(queryString);
                return GetResultSetFromDataTable(dataTable);
            }

            var jArray = new JArray();

            // Perform the query for each row in the result set that is being used.
            var usingResultSet = ResultSetHelper.GetCorrectObject<JArray>(query.UseResultSet, ReplacementHelper.EmptyRows, resultSets);
            
            var rows = new List<int> {0, 0};
            var keyWithSecondLayer = parameterKeys.FirstOrDefault(parameterKey => parameterKey.Key.Contains("[j]"))?.Key;
            for (var i = 0; i < usingResultSet.Count; i++)
            {
                // If the resulset has a succes of 'false' then don't proceed
                if (query.OnlyWithSuccessfullResultSet && Convert.ToBoolean(usingResultSet[i]["Success"] ?? true)==false)
                {
                    await logService.LogInformation(logger, LogScopes.RunBody, query.LogSettings, $"Execution of Query skipped because Resulset has a Success=false", configurationServiceName, query.TimeId, query.Order);
                    continue;
                }
                
                rows[0] = i;
                var lastIQuery = i == usingResultSet.Count - 1;

                if (keyWithSecondLayer != null)
                {
                    var secondLayerKey = keyWithSecondLayer.Substring(0, keyWithSecondLayer.IndexOf("[j]"));
                    var secondLayerArray = ResultSetHelper.GetCorrectObject<JArray>($"{query.UseResultSet}[i].{secondLayerKey}", rows, resultSets);

                    if (secondLayerArray == null)
                    {
                        await logService.LogWarning(logger, LogScopes.RunBody, query.LogSettings, $"Could not find second layer array with key '{secondLayerKey}' in result set '{query.UseResultSet}' at index '{i}', referring to object:\n{ResultSetHelper.GetCorrectObject<JObject>($"{query.UseResultSet}[i]", rows, resultSets)}", configurationServiceName, query.TimeId, query.Order);
                        continue;
                    }
                    
                    for (var j = 0; j < secondLayerArray.Count; j++)
                    {
                        rows[1] = j;
                        var lastJQuery = j == secondLayerArray.Count - 1;
                        jArray.Add(await ExecuteQueryWithParameters(query, databaseConnection, queryString, rows, usingResultSet, parameterKeys, insertedParameters, lastIQuery && lastJQuery, query.UseTransaction));
                    }
                }
                else
                {
                    jArray.Add(await ExecuteQueryWithParameters(query, databaseConnection, queryString, rows, usingResultSet, parameterKeys, insertedParameters, lastIQuery, query.UseTransaction));
                }
            }

            return new JObject
            {
                {"Results", jArray}
            };
        }

        /// <summary>
        /// Execute the query with parameters.
        /// </summary>
        /// <param name="query">The query action that is executed.</param>
        /// <param name="databaseConnection">The database connection to use.</param>
        /// <param name="queryString">The query to execute.</param>
        /// <param name="rows">The indexes for the rows to use.</param>
        /// <param name="usingResultSet">The result set that is used.</param>
        /// <param name="parameterKeys">The keys of the parameters to add.</param>
        /// <param name="insertedParameters">The keys and values of parameters already inserted.</param>
        /// <param name="lastQuery">If the current query is the last one to be performed for this action.</param>
        /// <param name="usingTransaction">If the action is using a transaction.</param>
        /// <returns></returns>
        private async Task<JObject> ExecuteQueryWithParameters(QueryModel query, IDatabaseConnection databaseConnection, string queryString, List<int> rows, JArray usingResultSet, List<ParameterKeyModel> parameterKeys, List<KeyValuePair<string, string>> insertedParameters, bool lastQuery, bool usingTransaction)
        {
            var parameters = new List<KeyValuePair<string, string>>(insertedParameters);

            foreach (var parameterKey in parameterKeys)
            {
                var value = ReplacementHelper.GetValue(parameterKey.Key, rows, (JObject)usingResultSet[rows[0]], false);

                if (parameterKey.Hash)
                {
                    value = StringHelpers.HashValue(value, query.HashSettings);
                }
                
                parameters.Add(new KeyValuePair<string, string>(parameterKey.ReplacementKey, value));
            }

            databaseConnection.ClearParameters();
            foreach (var parameter in parameters)
            {
                if (parameter.Value == null || parameter.Value.Equals("DBNull", StringComparison.InvariantCultureIgnoreCase))
                {
                    databaseConnection.AddParameter(parameter.Key, DBNull.Value);
                }
                else
                {
                    databaseConnection.AddParameter(parameter.Key, parameter.Value);
                }
            }

            var dataTable = await databaseConnection.GetAsync(queryString, cleanUp: lastQuery, useWritingConnectionIfAvailable: usingTransaction);
            return GetResultSetFromDataTable(dataTable);
        }

        private JObject GetResultSetFromDataTable(DataTable dataTable)
        {
            var resultSet = new JObject();
            var jArray = new JArray();

            if (dataTable != null && dataTable.Rows.Count > 0)
            {

                foreach (DataRow row in dataTable.Rows)
                {
                    var jObject = new JObject();

                    for (var i = 0; i < dataTable.Columns.Count; i++)
                    {
                        var columnName = dataTable.Columns[i].ColumnName;
                        jObject.Add(columnName, row[i].ToString());
                    }

                    jArray.Add(jObject);
                }
            }

            resultSet.Add("Results", jArray);
            return resultSet;
        }
    }
}
