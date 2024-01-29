Fork of https://github.com/simplesqlunittesting/simplesqlunittesting with some tweaks and additional features for seeding test data, among other things.

### Background
Microsoft provides a `Microsoft.Data.Tools.Schema.Sql.UnitTesting` library with integration for SSDT projects.  
These SQL Unit Test projects are easy to spin up using a wizard in Visual Studio, but defining tests (and especially the corresponding assertions) is pretty awful.
Some "highlights" of the default testing experience:
- Defining and modifying tests requires the use of a design view/UI (which is fairly buggy/unstable)
- Seeing your test setup, test action, and test cleanup SQL requires switching between three different views
- Test runs are *not* isolated from one another or cleaned up automatically via transactions or any other mechanism
- The underlying C# test class is generated code based on work done in the design view
- Actual SQL to be executed as part of the test is stored in a .RESX file that is referenced by the generated C# test class
- Resultset assertions are mostly limited to scalar values (i.e. "this exact cell has this value") resultset checksums, and rowcount checks
  - the checksum can only be produced by manually getting your local database into the exact state you want at the end of the test, and telling VS to calculate the checksum and save it.
    - the actual resultset values are not persisted anywhere, so you have no idea what the actual resultset was supposed to be if the test fails.
    - all of this stuff is also stored in the .RESX
    - even just getting to this editor for the checksum and rest of the .RESX is flaky/buggy, half the time it just doesn't want to open or shows a blank "Properties" tab
- Really no way to do any meaningful test setup, mock data creation, etc. outside of handwritten SQL scripts. 

# Getting started:
- In Solution Explorer, right click on any Stored Procedure or Function in your SSDT project and select "Create Unit Tests..."
  - The function/sproc you choose for this doesn't matter, as long as it's in the SSDT project you want to create a unit test project for
- In the wizard window that pops up, under "Output project" select "Create a new Visual C# test project..." and specify the new project name (e.g. SD.MyDatabase.Tests), then click OK
- The new test project should be added to the current solution automatically.  Find it in solution explorer and delete the generated test class (probably `SqlServerUnitTest1.cs`) and any generated .RESX files.
  - Do not delete the generated `app.config` or `SqlDatabaseSetup.cs`
- Create a new test class.  
  - For this example we'll create a `HelloWorldTest.cs` class to smoke test the SSDT build/publish and test runner.
```
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SimpleSqlUnitTesting;

namespace SD.MyDatabase.Tests 
{
	[TestClass]
	public class HelloWorldTest : LocalTransactionSqlTest
	{
		[TestMethod]
        public void HelloWorld()
        {
            RunTest(Actions.CreateBlock($@"
                SELECT 'hello', 'world'
            ").ResultsetShouldBe(1, new object[] { "hello", "world" })
            );
        }
	}
}
```
- Run the test you created via the Visual Studio Test Explorer.
  - The example "HelloWorldTest" above should run succesfully if everything is configured correctly.

# What's included
### Define tests without any funky .RESX, designer views, or resultset checksums
- Run each test in its own transaction scope, which is rolled back at the end of the test, by inheriting from `LocalTransactionSqlTest`
  - There is also a `DistributedTransactionSqlTest` class that can be inherited from, though I haven't tested it so you're on your own if you wanna use it
  - Both of these implementations inherit from the abstract `SqlTest` class defined in this project.
- Define your common test initialization actions for the current suite by setting the inherited `TestInitializeAction` property to some `SqlDatabaseTestAction`.
  - You can generate a `SqlDatabaseTestAction` via a call to the static `Actions.CreateSingle(string sql)`
    - This method also has an overload that lets you compose multiple actions into a single action - `CreateSingle(params SqlDatabaseTestAction[] actionsToConcat)`
- Define your pretest/test/posttest actions and assertions using a `SqlDatabaseTestActions` instance 
  - `SqlDatabaseTestActions` instance is generated via a call to the static `Actions.CreateBlock(string sql)`
  - Two types of assertions are supported - `ResultsetShouldBe` and `ResultsetShouldBeEmpty`
    - The expected resultset can be defined in a few different ways:
      - As an `object[]`, `object[,]`, or `params object[][]`:
        - `.ResultsetShouldBe(1, new object[] {"hello", "world"})`
        - `.ResultsetShouldBe(1, new object[] {"hello", "world"}, new object[] {"second", "row"})`
        - `.ResultsetShouldBe(1, new object[][] {new object[] {"hello", "world"}, new object[] {"second", "row"}})`
        - `.ResultsetShouldBe(1, new object[,] {{"hello", "world"}, {"second", "row"}, {Guid.Empty, 12345}})`
      - As a `string[]` or `string[,]`: 
        - `.ResultsetShouldBe(1, new string[] {"hello", "world"})`
        - `.ResultsetShouldBe(1, new string[,] {{"hello", "world"},{"second", "row"}})`
      - As a comma-delimited `string`: 
        - `.ResultsetShouldBe(1, "hello,world")`
        - ```
           .ResultsetShouldBe(1, $@"
           hello,world
           second,row
           ")
- Run your tests by calling your test classes' inherited `RunTest(SqlDatabaseTestActions testActions)`

### Extra utilities - `SimpleSqlUnitTesting.SqlTestUtils`
- Documentation for these utilities is provided via XML comments in `SqlTestUtils.cs` - some of what's included:
  - `SqlDatabaseTestAction AddEntities<T>(T rootEntity)`
    - A method that generates SQL for inserting a provided entity POCO and its children into the databse under test.
  - `T SetAllMatchingUnsetChildPropsRecursive<T>(T node)`
    - A method that descends upon an entity POCO's properties and copies values from parent to child, where the child's property name matches but is unset/default value.
    - This is called by default in `AddEntities` as well, but can be skipped via an optional argument when invoking `AddEntities`.
  - `string getSqlLiteral(object obj)`
    - A method that generates a SQL literal for a given value.
    - Useful for interpolating literal values into SQL statement strings.
  - `Guid makeGuid(int | string)`
    - A method that produces a stable Guid/UNIQUEIDENTIFIER based on an integer or string's hashcode
  - `interface ColumnOrdinalSortable<T>`
    - An interface that, when implemented, makes extension methods available that can produce sorted, explicit-column-ordered results/resultsets (as `object[]`/`object[][]` respectively) 
  - `string ToTableVariableDeclaration<T>(this IEnumerable<T> instList, string varName, string udttName = null) where T : class`
    - A method that takes a collection of entity POCOs and generates a table variable declaration along with insert statements for each row.
      - Useful for testing functions or sprocs that take table-valued parameters



## *Original readme below*:

# Simple SQL Unit Testing Framework
Framework that greatly simplifies the development of SQL Unit Tests in Visual Studio.

## Benefits
Everyone that has used the SQL Unit Tests feature of Visual Studio will know that is far from perfect. The main problems being the weird bugs that prevent properly saving the files, and the cumbersome way of specifying the assertions. Other benefits are:
- No RESX!
- Work directly with MSTest test classes.
- Assertions for a whole table (no more Scalar Value Conditions for each of your cells!) 
- Fluent and terse assertions.
- You can paste results from SSMS directly into your assertion.
- Outputs joined SQL.
- Base classes for wrapping your test in a rollbacked transaction, either local or distributed.

## Getting started

    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using SimpleSqlUnitTesting

    namespace MyTests
    {
        [TestClass]
        public class MyTestClass : LocalTransactionSqlTest
        {
            public MyTestClass()
            {
                TestInitializeAction = Actions.CreateSingle(
                    @"sql");
            }

            [TestMethod]
            public void MyTestMethod()
            {
                RunTest(Actions.CreateBlock(@"sql")
                .ResultsetShouldBe(1, @"
    11	1	0.143	7
    22	2	0.286	7
    55	3	0.429	7
    66	4	0.571	7
    77	5	0.714	7
    88	6	0.857	7
    133	7	1.000	7")
                .ResultsetShouldBe(2, "6"));
            }
        }
    }

## Feedback
Please provide feedback and ask questions [here](https://github.com/simplesqlunittesting/simplesqlunittesting/issues/new).