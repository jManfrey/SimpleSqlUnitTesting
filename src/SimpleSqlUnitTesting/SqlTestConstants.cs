using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SimpleSqlUnitTesting
{
    public static class SqlTestConstants
    {
        /// <summary>
        /// Can be used in a `ResultSetShouldBe` call wherever an ScalarValueCondition isn't necessary for particular cell<br/>
        /// example: new object[] {"col1", SqlTestConstants.AnyResult, "col3"} -> matches ["col1", "col2", "col3"] <br/>
        /// example: $"col1,{sqlTestConstants.AnyResult},col3" -> matches ["col1", "col2", "col3"]
        /// </summary>
        public static string AnyResult = "______ANY___RESULT______";
    }
}
