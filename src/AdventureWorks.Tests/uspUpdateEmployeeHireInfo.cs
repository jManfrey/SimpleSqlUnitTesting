using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SimpleSqlUnitTesting;

namespace AdventureWorks.Tests
{
    [TestClass]
    public class uspUpdateEmployeeHireInfo : LocalTransactionSqlTest
    {

        public uspUpdateEmployeeHireInfo()
        {
            TestInitializeAction = Actions.CreateSingle(@"
INSERT INTO [Production].[Location]
VALUES ('TEST',100,100,'20160101');
");

        }

        [TestMethod]
        public void ReturnsData()
        {
            RunTest(Actions.CreateBlock(@"
				SELECT  [Name]
                       ,[CostRate]
                       ,[Availability]
                       ,[ModifiedDate] 
                FROM [Production].[Location]")
                .ResultsetShouldBe(1, new object[] { "TEST", 100, 100, new DateTime(2016, 1, 1) }));
        }

        [TestMethod]
        public void NonReturnsData()
        {
            RunTest(Actions.CreateBlock(@"
DELETE [Production].[Location]
WHERE Name='TEST' 

SELECT [Name]
                       ,[CostRate]
                       ,[Availability]
                       ,[ModifiedDate] 
                FROM [Production].[Location]").ResultsetShouldBeEmpty(1));

        }

    }
}
