using Microsoft.Data.Tools.Schema.Sql.UnitTesting;
using System.Linq;

namespace SimpleSqlUnitTesting
{
    public static class Actions
    {
        public static SqlDatabaseTestActions CreateBlock(string sql)
        {
            return new SqlDatabaseTestActions
            {
                TestAction = CreateSingle(sql)
            };
        }

        public static SqlDatabaseTestAction CreateSingle(string sql)
        {
            return new SqlDatabaseTestAction
            {
                SqlScript = sql
            };
        }

        public static SqlDatabaseTestAction CreateSingle(params SqlDatabaseTestAction[] actionsToConcat)
        {
            return new SqlDatabaseTestAction
            {
                SqlScript = actionsToConcat.Select(a => a.SqlScript).Aggregate((p, n) => p + "\r\n" + n)
            };
        }
    }
}