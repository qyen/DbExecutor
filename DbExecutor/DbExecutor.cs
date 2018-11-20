﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.Contracts;
using System.Dynamic;
using System.Linq;
using Codeplex.Data.Internal;

namespace Codeplex.Data
{
    /// <summary>Simple and Lightweight Database Executor.</summary>
    public partial class DbExecutor : IDisposable
    {
        readonly IDbConnection connection;
        readonly char parameterSymbol;
        // Transaction
        readonly bool isUseTransaction;
        readonly IsolationLevel isolationLevel;
        IDbTransaction transaction;
        bool isTransactionCompleted = false;

        /// <summary>Create standard executor.</summary>
        /// <param name="connection">Database connection.</param>
        /// <param name="parameterSymbol">Command parameter symbol. SqlServer = '@', MySql = '?', Oracle = ':'</param>
        public DbExecutor(IDbConnection connection, char parameterSymbol = '@')
        {
            Contract.Requires<ArgumentNullException>(connection != null);

            this.connection = connection;
            this.parameterSymbol = parameterSymbol;
            this.isUseTransaction = false;
        }

        /// <summary>Use transaction.</summary>
        /// <param name="connection">Database connection.</param>
        /// <param name="isolationLevel">Transaction IsolationLevel.</param>
        /// <param name="parameterSymbol">Command parameter symbol. SqlServer = '@', MySql = '?', Oracle = ':'</param>
        public DbExecutor(IDbConnection connection, IsolationLevel isolationLevel, char parameterSymbol = '@')
        {
            Contract.Requires<ArgumentNullException>(connection != null);

            this.connection = connection;
            this.parameterSymbol = parameterSymbol;
            this.isUseTransaction = true;
            this.isolationLevel = isolationLevel;
        }

        /// <summary>If connection is not open then open and create command.</summary>
        /// <param name="query">SQL code.</param>
        /// <param name="commandType">Command Type.</param>
        /// <param name="parameter">PropertyName parameterized to PropertyName. if null then no use parameter.</param>
        /// <param name="extraParameter">CommandName set to __extra__PropertyName.</param>
        /// <returns>Setuped IDbCommand.</returns>
        protected IDbCommand PrepareExecute(string query, CommandType commandType, object parameter, object extraParameter = null)
        {
            Contract.Requires<ArgumentException>(!String.IsNullOrEmpty(query));
            Contract.Ensures(Contract.Result<IDbCommand>() != null);

            if (connection.State != ConnectionState.Open) connection.Open();
            if (transaction == null && isUseTransaction) transaction = connection.BeginTransaction(isolationLevel);

            var command = connection.CreateCommand();
            command.CommandText = query;
            command.CommandType = commandType;

            if (parameter != null)
            {
                foreach (var p in AccessorCache.Lookup(parameter.GetType()))
                {
                    if (!p.IsReadable) continue;

                    Contract.Assume(parameter != null);
                    var param = command.CreateParameter();
                    param.ParameterName = p.Name;
                    param.Value = p.GetValueDirect(parameter) ?? DBNull.Value;
                    command.Parameters.Add(param);
                }
            }
            if (extraParameter != null)
            {
                foreach (var p in AccessorCache.Lookup(extraParameter.GetType()))
                {
                    if (!p.IsReadable) continue;

                    Contract.Assume(extraParameter != null);
                    var param = command.CreateParameter();
                    param.ParameterName = "__extra__" + p.Name;
                    param.Value = p.GetValueDirect(extraParameter) ?? DBNull.Value;
                    command.Parameters.Add(param);
                }
            }

            if (transaction != null) command.Transaction = transaction;

            return command;
        }

        IEnumerable<IDataRecord> YieldReaderHelper(string query, object parameter, CommandType commandType, CommandBehavior commandBehavior)
        {
            using (var command = PrepareExecute(query, commandType, parameter))
            using (var reader = command.ExecuteReader(commandBehavior))
            {
                while (reader.Read()) yield return reader;
            }
        }

        /// <summary>Executes and returns the data records.</summary>
        /// <param name="query">SQL code.</param>
        /// <param name="parameter">PropertyName parameterized to PropertyName. if null then no use parameter.</param>
        /// <param name="commandType">Command Type.</param>
        /// <param name="commandBehavior">Command Behavior.</param>
        /// <returns>Query results.</returns>
        public IEnumerable<IDataRecord> ExecuteReader(string query, object parameter = null, CommandType commandType = CommandType.Text, CommandBehavior commandBehavior = CommandBehavior.Default)
        {
            Contract.Requires<ArgumentException>(!String.IsNullOrEmpty(query));
            Contract.Ensures(Contract.Result<IEnumerable<IDataRecord>>() != null);

            return YieldReaderHelper(query, parameter, commandType, commandBehavior);
        }

        IEnumerable<dynamic> YieldReaderDynamicHelper(string query, object parameter, CommandType commandType, CommandBehavior commandBehavior)
        {
            using (var command = PrepareExecute(query, commandType, parameter))
            using (var reader = command.ExecuteReader(commandBehavior))
            {
                var record = new DynamicDataRecord(reader); // reference same reader
                while (reader.Read()) yield return record;
            }
        }

        /// <summary>Executes and returns the data records enclosing DynamicDataRecord.</summary>
        /// <param name="query">SQL code.</param>
        /// <param name="parameter">PropertyName parameterized to PropertyName. if null then no use parameter.</param>
        /// <param name="commandType">Command Type.</param>
        /// <param name="commandBehavior">Command Behavior.</param>
        /// <returns>Query results. Result type is DynamicDataRecord.</returns>
        public IEnumerable<dynamic> ExecuteReaderDynamic(string query, object parameter = null, CommandType commandType = CommandType.Text, CommandBehavior commandBehavior = CommandBehavior.Default)
        {
            Contract.Requires<ArgumentException>(!String.IsNullOrEmpty(query));
            Contract.Ensures(Contract.Result<IEnumerable<dynamic>>() != null);

            return YieldReaderDynamicHelper(query, parameter, commandType, commandBehavior);
        }

        /// <summary>Executes and returns the number of rows affected.</summary>
        /// <param name="query">SQL code.</param>
        /// <param name="parameter">PropertyName parameterized to PropertyName. if null then no use parameter.</param>
        /// <param name="commandType">Command Type.</param>
        /// <returns>Rows affected.</returns>
        public int ExecuteNonQuery(string query, object parameter = null, CommandType commandType = CommandType.Text)
        {
            Contract.Requires<ArgumentException>(!String.IsNullOrEmpty(query));

            using (var command = PrepareExecute(query, commandType, parameter))
            {
                return command.ExecuteNonQuery();
            }
        }

        /// <summary>Executes and returns the first column, first row.</summary>
        /// <typeparam name="T">Result type.</typeparam>
        /// <param name="query">SQL code.</param>
        /// <param name="parameter">PropertyName parameterized to PropertyName. if null then no use parameter.</param>
        /// <param name="commandType">Command Type.</param>
        /// <returns>Query results of first column, first row.</returns>
        public T ExecuteScalar<T>(string query, object parameter = null, CommandType commandType = CommandType.Text)
        {
            Contract.Requires<ArgumentException>(!String.IsNullOrEmpty(query));

            using (var command = PrepareExecute(query, commandType, parameter))
            {
                return (T)command.ExecuteScalar();
            }
        }

        /// <summary>Executes and mapping objects by ColumnName - PropertyName.</summary>
        /// <typeparam name="T">Mapping target Class.</typeparam>
        /// <param name="query">SQL code.</param>
        /// <param name="parameter">PropertyName parameterized to PropertyName. if null then no use parameter.</param>
        /// <param name="commandType">Command Type.</param>
        /// <returns>Mapped instances.</returns>
        public IEnumerable<T> Select<T>(string query, object parameter = null, CommandType commandType = CommandType.Text) where T : new()
        {
            Contract.Requires<ArgumentException>(!String.IsNullOrEmpty(query));
            Contract.Ensures(Contract.Result<IEnumerable<T>>() != null);

            var accessors = AccessorCache.Lookup(typeof(T));
            return ExecuteReader(query, parameter, commandType, CommandBehavior.SequentialAccess)
                .Select(dr =>
                {
                    // if T is ValueType then can't set SetValue
                    // must be boxed
                    object result = new T();
                    for (int i = 0; i < dr.FieldCount; i++)
                    {
                        if (dr.IsDBNull(i)) continue;

                        var accessor = accessors[dr.GetName(i)];
                        if (accessor != null && accessor.IsWritable) accessor.SetValueDirect(result, dr[i]);
                    }
                    return (T)result;
                });
        }

        /// <summary>Executes and mapping objects to ExpandoObject. Object is dynamic accessable by ColumnName.</summary>
        /// <param name="query">SQL code.</param>
        /// <param name="parameter">PropertyName parameterized to PropertyName. if null then no use parameter.</param>
        /// <param name="commandType">Command Type.</param>
        /// <returns>Mapped results(dynamic type is ExpandoObject).</returns>
        public IEnumerable<dynamic> SelectDynamic(string query, object parameter = null, CommandType commandType = CommandType.Text)
        {
            Contract.Requires<ArgumentException>(!String.IsNullOrEmpty(query));
            Contract.Ensures(Contract.Result<IEnumerable<dynamic>>() != null);

            return ExecuteReader(query, parameter, commandType, CommandBehavior.SequentialAccess)
                .Select(dr =>
                {
                    IDictionary<string, object> expando = new ExpandoObject();
                    for (int i = 0; i < dr.FieldCount; i++)
                    {
                        var value = dr.IsDBNull(i) ? null : dr.GetValue(i);
                        expando.Add(dr.GetName(i), value);
                    }
                    return expando;
                });
        }

        /// <summary>Insert by object's PropertyName.</summary>
        /// <param name="tableName">Target database's table.</param>
        /// <param name="insertItem">Table's column name extracted from PropertyName.</param>
        /// <returns>Rows affected.</returns>
        public int Insert(string tableName, object insertItem)
        {
            Contract.Requires<ArgumentException>(!String.IsNullOrEmpty(tableName));
            Contract.Requires<ArgumentNullException>(insertItem != null);

            var propNames = AccessorCache.Lookup(insertItem.GetType())
                .Where(p => p.IsReadable)
                .ToArray();
            var column = string.Join(", ", propNames.Select(p => p.Name));
            var data = string.Join(", ", propNames.Select(p => parameterSymbol + p.Name));

            var query = string.Format("insert into {0} ({1}) values ({2})", tableName, column, data);

            Contract.Assume(query.Length > 0);
            return ExecuteNonQuery(query, insertItem);
        }

        /// <summary>Update by object's PropertyName.</summary>
        /// <param name="tableName">Target database's table.</param>
        /// <param name="updateItem">Table's column name extracted from PropertyName.</param>
        /// <param name="whereCondition">Where condition extracted from PropertyName.</param>
        /// <returns>Rows affected.</returns>
        public int Update(string tableName, object updateItem, object whereCondition)
        {
            Contract.Requires<ArgumentException>(!String.IsNullOrEmpty(tableName));
            Contract.Requires<ArgumentNullException>(whereCondition != null);
            Contract.Requires<ArgumentNullException>(updateItem != null);

            var update = string.Join(", ", AccessorCache.Lookup(updateItem.GetType())
                .Where(p => p.IsReadable)
                .Select(p => p.Name + " = " + parameterSymbol + p.Name));

            var where = string.Join(" and ", AccessorCache.Lookup(whereCondition.GetType())
                .Select(p => p.Name + " = " + parameterSymbol + "__extra__" + p.Name));

            var query = string.Format("update {0} set {1} where {2}", tableName, update, where);

            Contract.Assume(query.Length > 0);
            using (var command = PrepareExecute(query, CommandType.Text, updateItem, whereCondition))
            {
                return command.ExecuteNonQuery();
            }
        }

        /// <summary>Delete by object's PropertyName.</summary>
        /// <param name="tableName">Target database's table.</param>
        /// <param name="whereCondition">Where condition extracted from PropertyName.</param>
        /// <returns>Rows affected.</returns>
        public int Delete(string tableName, object whereCondition)
        {
            Contract.Requires<ArgumentException>(!String.IsNullOrEmpty(tableName));
            Contract.Requires<ArgumentNullException>(whereCondition != null);

            var where = string.Join(" and ", AccessorCache.Lookup(whereCondition.GetType())
                .Select(p => p.Name + " = " + parameterSymbol + p.Name));

            var query = string.Format("delete from {0} where {1}", tableName, where);

            Contract.Assume(query.Length > 0);
            return ExecuteNonQuery(query, whereCondition);
        }

        /// <summary>Commit transaction.</summary>
        public void TransactionComplete()
        {
            if (transaction != null)
            {
                transaction.Commit();
                isTransactionCompleted = true;
            }
        }

        /// <summary>Dispose inner connection.</summary>
        public void Dispose()
        {
            try
            {
                if (transaction != null && !isTransactionCompleted)
                {
                    transaction.Rollback();
                    isTransactionCompleted = true;
                }
            }
            finally
            {
                connection.Dispose();
            }
        }
    }
}