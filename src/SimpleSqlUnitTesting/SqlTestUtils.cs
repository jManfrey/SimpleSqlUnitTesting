using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Tools.Schema.Sql.UnitTesting;

namespace SimpleSqlUnitTesting
{
    public static class SqlTestUtils
    {

        /// <summary>
        /// When using AddEntities or SetAllMatchingUnsetChildPropsRecursive, this list will be consulted when mapping parent prop names to child prop names.<br/>
        /// Mappings can either be exact <br/>
        /// e.g. ("SomeClass.SomeProperty", "ChildMemberClass.ChildMemberProperty")<br/>
        /// or loose (wildcarded class names)<br/>
        /// e.g. ("*.SomeProperty", "*.ChildMemberProperty")<br/>
        /// </summary>
        /// <remarks>TODO: support wildcard in just parent or child portion of mapping (all or nothing for now)</remarks>
        public static (string ParentClassMember, string ChildClassMember)[] AdditionalParentChildMatchingPropNames = new (string ParentClassMember, string ChildClassMember)[0];

        /// <summary>
        /// When using AddEntities or SetAllMatchingUnsetChildPropsRecursive, this list excludes any matching props/columns.<br/>
        /// This means AddEntities will exclude it from its generated SQL INSERTs, and SetAllMatchingUnsetChildPropsRecursive will ignore it even if a parent match is found.
        /// </summary>
        public static string[] KnownComputedColumnNames = new string[0];

        //TODO: Class -> Table name override mapping
        //TODO: Opt in/out of IDENTITY_INSERT detection (on a per-table basis potentially)


        /// <summary>
        /// Reflection magic<br/>
        /// - generate SQL INSERT statements for the provided rootEntity and its children
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="rootEntity">the entity object to generate SQL INSERTs for</param>
        /// <param name="doSetAllMatchingUnsetChildProps">(default true) if false, skips calling SetAllMatchingUnsetChildPropsRecursive on the provided rootEntity</param>
        /// <returns></returns>
        public static SqlDatabaseTestAction AddEntities<T>(T rootEntity, bool doSetAllMatchingUnsetChildProps = true)
        {
            if ((rootEntity as System.Collections.IEnumerable) != null)
            {
                var compositeAction = Actions.CreateSingle("");
                var rootCollection = (System.Collections.IEnumerable)rootEntity;
                foreach (var entity in rootCollection)
                {
                    compositeAction = compositeAction.Then(AddEntities(entity, doSetAllMatchingUnsetChildProps).SqlScript);
                }
                return compositeAction;
            }
            var fullyQualifiedEntities = doSetAllMatchingUnsetChildProps ? SetAllMatchingUnsetChildPropsRecursive(rootEntity) : rootEntity;
            var actualColumnProps = rootEntity.GetType().GetProperties().Where(p => isPropertyStorableInAColumn(p)).ToArray();

            var rootAction = Actions.CreateSingle($@"
            INSERT INTO [dbo].[{rootEntity.GetType().Name}]
            ({actualColumnProps.Select(p => "[" + p.Name + "]").Aggregate((p, c) => p + "," + c)})
            VALUES
            ({actualColumnProps.Select(p => getSqlLiteral(p, rootEntity)).Aggregate((p, c) => p + "," + c)})
            ");

            //hack - detect IDENTITY column
            if (actualColumnProps.Any(p => p.Name == $"{rootEntity.GetType().Name}Id" && p.PropertyType == typeof(int)))
            {
                rootAction = Actions.CreateSingle($@"
                SET IDENTITY_INSERT [dbo].[{rootEntity.GetType().Name}] ON
                    {rootAction.SqlScript}
                SET IDENTITY_INSERT [dbo].[{rootEntity.GetType().Name}] OFF
                ");
            }

            var childActionsToDoAfter = rootEntity
                .GetType()
                .GetProperties()
                .Except(actualColumnProps)
                .Where(p => !isKnownComputedColumn(p)) //also filter out computed columns since they won't be in actualColumnProps
                .Select(p =>
                {
                    var actionToReturn = Actions.CreateSingle("");
                    var propValue = p.GetValue(rootEntity);
                    if (propValue != null)
                    {
                        if (isCollectionProp(p))
                        {
                            var someCollection = (System.Collections.IEnumerable)propValue;
                            foreach (var item in someCollection)
                            {
                                actionToReturn = actionToReturn.Then(AddEntities(item, false).SqlScript);
                            }
                        }
                        else
                        {
                            actionToReturn = AddEntities(propValue, false);
                        }
                    }
                    return actionToReturn;
                }
                );

            foreach (var action in childActionsToDoAfter)
            {
                rootAction = rootAction.Then(action.SqlScript);
            }

            return rootAction;
        }

        /// <summary>
        /// reflection magic <br/>
        /// - traverse an object's (parent node's) properties <br/>
        /// - if one of the parent node's child nodes (i.e. properties) is a reference type (or ICollection of a ref type), traverse that child node's children (properties)<br/>
        /// - if one of these grandchild nodes (properties) has a name matching some other parent node child (property), and the grandchild node value is default/null<br/>
        /// -- copy the value from the matching child node to the matching grandchild node.<br/>
        /// e.g. <br/>
        /// class Foo { int FooId, Bar Child}<br/>
        /// class Bar { int FooId, int BarId}<br/>
        /// SetAllMatchingUnsetChildPropsRecursive(new Foo { FooId = 1, Child = new Bar { BarId = 2 } })<br/>
        /// sets properties so object is equivalent to<br/>
        /// new Foo { FooId = 1, Child = new Bar { FooId = 1, BarId = 2 } }
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="node">note, this input will be modified</param>
        public static T SetAllMatchingUnsetChildPropsRecursive<T>(T node)
        {
            //if entry point to this method is via passing a collection/enumerable, handle appropriately
            //note that this won't get hit via AddEntities since it does its own enumerable iteration
            //but will get hit if you define some mock data inside a getter that calls this fn, for example

            if ((node as System.Collections.IEnumerable) != null)
            {
                var rootCollection = (System.Collections.IEnumerable)node;
                foreach (var entity in rootCollection)
                {
                    SetAllMatchingUnsetChildPropsRecursive(entity);
                }
                return node;
            }


            var props = node.GetType().GetProperties();
            foreach (var prop in props)
            {
                if (isCollectionProp(prop))
                { //ICollection child
                    var childVals = (System.Collections.IEnumerable)prop.GetValue(node);
                    if (childVals == null) continue;
                    foreach (var childVal in childVals)
                    {
                        var childValProps = childVal.GetType().GetProperties();
                        foreach (var p in childValProps)
                        {
                            var isMatchableType = isPropertyStorableInAColumn(p);
                            var parentMatchingProp = FindMatchingParentProperty(node, p);
                            var defaultValueForPropType = GetDefault(p.PropertyType);
                            var childValueForPropType = p.GetValue(childVal);
                            var valueMatchesDefault = childValueForPropType?.Equals(defaultValueForPropType) ?? true;

                            if (isMatchableType && parentMatchingProp != null && valueMatchesDefault)
                            {
                                var matchingParentPropValue = parentMatchingProp.GetValue(node);
                                p.SetValue(childVal, matchingParentPropValue);
                            }
                        }

                        SetAllMatchingUnsetChildPropsRecursive(childVal);
                    }
                }
                else if (!prop.PropertyType.IsValueType && prop.PropertyType != typeof(string))
                {
                    var childVal = prop.GetValue(node);
                    if (childVal == null) continue;
                    var childValProps = childVal.GetType().GetProperties();
                    foreach (var p in childValProps)
                    {
                        var isMatchableType = isPropertyStorableInAColumn(p);
                        var parentMatchingProp = FindMatchingParentProperty(node, p);
                        var defaultValueForPropType = GetDefault(p.PropertyType);
                        var childValueForPropType = p.GetValue(childVal);
                        var valueMatchesDefault = childValueForPropType?.Equals(defaultValueForPropType) ?? true;

                        if (isMatchableType && parentMatchingProp != null && valueMatchesDefault)
                        {
                            var matchingParentPropValue = parentMatchingProp.GetValue(node);
                            p.SetValue(childVal, matchingParentPropValue);
                        }
                    }

                    SetAllMatchingUnsetChildPropsRecursive(childVal);

                }
            }
            return node;
        }


        private static System.Reflection.PropertyInfo FindMatchingParentProperty(object parent, System.Reflection.PropertyInfo childProperty)
        {
            var parentType = parent.GetType();
            var parentProps = parentType.GetProperties();
            var parentPropNames = parentProps.Select(p => p.Name).ToArray();
            var parentTypeName = parentType.Name;
            var childTypeName = childProperty.DeclaringType.Name;

            //try fully qualified exact match override

            var matchingExactOverride = AdditionalParentChildMatchingPropNames
                .FirstOrDefault(p => p.ChildClassMember == $"{childTypeName}.{childProperty.Name}" && p.ParentClassMember.StartsWith($"{parentTypeName}."));

            if (matchingExactOverride.ParentClassMember != null)
            {
                return parentProps.First(p => p.Name == matchingExactOverride.ParentClassMember.Substring($"{parentTypeName}.".Length));
            }

            //try splat override

            var matchingSplatOverride = AdditionalParentChildMatchingPropNames
                    .FirstOrDefault(p =>
                    p.ChildClassMember == $"*.{childProperty.Name}" &&
                    p.ParentClassMember.StartsWith("*.") &&
                    parentPropNames.Contains(p.ParentClassMember.Substring("*.".Length)));

            if (matchingSplatOverride.ParentClassMember != null) //stupid value tuple non-default check
            {
                return parentProps.First(p => p.Name == matchingSplatOverride.ParentClassMember.Substring("*.".Length));
            }

            //try normal name match

            var matchingExactName = parentProps.FirstOrDefault(p => p.Name == childProperty.Name);
            if (matchingExactName != null)
                return matchingExactName;

            return null;
        }

        private static object GetDefault(Type type)
        {
            if (type.IsValueType)
            {
                return Activator.CreateInstance(type);
            }
            return null;
        }

        private static bool isKnownComputedColumn(System.Reflection.PropertyInfo prop)
        {
            return KnownComputedColumnNames.Any(n => n == $"*.{prop.Name}" || n == $"{prop.DeclaringType.Name}.{prop.Name}");
        }

        private static bool isPropertyStorableInAColumn(System.Reflection.PropertyInfo prop)
        {
            var propertyType = prop.PropertyType;

            var computedColumnDetected = isKnownComputedColumn(prop);

            return !computedColumnDetected &&
                (
                    propertyType.IsValueType ||
                    propertyType == typeof(string) ||
                    //cheesy nullable value type check (since we know our scaffolded entities only contain one of two generic types - ICollection and Nullable)
                    //and the ICollections are always of reference types
                    (propertyType.IsGenericType && propertyType.GetGenericArguments()[0].IsValueType)
                );
        }

        private static bool isCollectionProp(System.Reflection.PropertyInfo prop)
        {
            return prop.PropertyType.IsGenericType && prop.PropertyType.Name.Contains("ICollection");
        }

        private static Type[] noQuoteColumnTypes = new Type[] { typeof(int), typeof(long), typeof(byte), typeof(bool) };

        private static string getSqlLiteral(System.Reflection.PropertyInfo propInfo, object obj)
        {
            var columnVal = propInfo.GetValue(obj);
            if (columnVal == null) return "NULL";

            if (noQuoteColumnTypes.Contains(propInfo.PropertyType))
            {
                return Convert.ToInt64(columnVal).ToString();
            }

            return $@"'{columnVal.ToString()}'";
        }

        /// <summary>
        /// Gets a SQL literal that can be interpolated into a dynamic SQL expression <br/>
        /// getSqlLiteral(true) -> 1<br/>
        /// getSqlLiteral("foo") -> 'foo'<br/>
        /// getSqlLiteral(Guid.Parse("4E77EFD3-2BF6-43A1-9597-398FBEA4FB79")) -> '4e77efd3-2bf6-43a1-9597-398fbea4fb79'<br/>
        /// getSqlLiteral(DateTimeOffset.Parse("2021-09-17T18:25:59.576Z")) -> '9/17/2021 6:25:59 PM +00:00'<br/>
        /// getSqlLiteral(12345) -> 12345
        /// </summary>
        /// <remarks>this implementation is not very sophisticated - pretty much just toString()s anything that isn't an int, long, byte, or bool and wraps it in single ticks</remarks>
        /// <param name="obj"></param>
        /// <returns></returns>
        public static string getSqlLiteral(object obj)
        {
            if (obj == null) return "NULL";

            var objType = obj.GetType();
            if (objType.IsGenericType && objType.GetGenericArguments()[0].IsValueType)
            {
                objType = objType.GetGenericArguments()[0];
            }
            if (noQuoteColumnTypes.Contains(objType))
            {
                return Convert.ToInt64(obj).ToString();
            }

            return $@"'{obj.ToString()}'";
        }

        private static Dictionary<Type, string> netToSqlTypeDict = new Dictionary<Type, string>() {
                { typeof(Guid), "UNIQUEIDENTIFIER" },
                { typeof(int), "INT" },
                { typeof(long), "BIGINT" },
                { typeof(byte), "TINYINT" },
                { typeof(string), "NVARCHAR(MAX)" },
                { typeof(DateTimeOffset), "DATETIMEOFFSET" },
                { typeof(DateTime), "DATETIME" },
                { typeof(bool), "BIT" },
            };

        /// <summary>
        /// This really is only meant to be called internally within SqlTestUtils, but might be useful to someone<br/>
        /// Doesn't support a ton of types, just the ones I could think of off the top of my head<br/>
        /// Tries to detect nullability and include a NULL qualifier if nullable <br/>
        /// todo: current implementation will always consider string-type properties non-nullable - probably not the right move but w/e
        /// </summary>
        /// <param name="t"></param>
        /// <returns></returns>
        public static string getSqlTypeName(Type t)
        {
            var objType = t;
            var isNullable = false;
            if (objType.IsGenericType && objType.GetGenericArguments()[0].IsValueType)
            {
                isNullable = true;
                objType = objType.GetGenericArguments()[0];
            }
            var resolvedSqlTypeName = netToSqlTypeDict.ContainsKey(objType) ? netToSqlTypeDict[objType] : null;
            if (resolvedSqlTypeName == null) { throw new Exception($"Could not map type {objType.FullName} to a SQL type"); }
            return $"{resolvedSqlTypeName}{(isNullable ? " NULL" : "")}";

        }

        /// <summary>
        /// Produces a stable GUID based on an integer
        /// </summary>
        /// <param name="someInt"></param>
        /// <returns></returns>
        public static Guid makeGuid(int someInt)
        {
            var intBytes = BitConverter.GetBytes(someInt);
            return new Guid(Enumerable.Repeat(Convert.ToByte(0), 16 - intBytes.Length).Concat(intBytes).ToArray());
        }

        /// <summary>
        /// produces a stable GUID based on a string's hashcode
        /// </summary>
        /// <param name="someString"></param>
        /// <returns></returns>
        public static Guid makeGuid(string someString)
        {
            //cheesy
            return makeGuid(someString.GetHashCode());
        }

        /// <summary>
        /// Interface that, when implemented, makes some extension methods available for: <br/>
        /// - producing object[] results (with values in the specified column order) <br/>
        /// - producing object[][] resultsets (with elements sorted by column order) <br/>
        /// This is helpful when making assertions about stored procedure SELECT results
        /// where the ordering of columns and sorting is controlled by the sproc definition and can't be rearranged/specified by the test <br/>
        /// TODO: currently operates under the convention that a sproc will ORDER BY results in the same column order as it SELECTs them, make it possible to define both orderings
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public interface ColumnOrdinalSortable<T>
        {
            /// <summary>
            /// A collection of property selectors that defines the expected column ordering in both the SELECT and ORDER BY expressions when receiving a resultset from an uncontrollable source (usually a sproc)<br/>
            /// TODO: add support for separate SELECT order vs ORDER BY order - for now following convention that they are identical
            /// </summary>
            IEnumerable<Func<T, object>> ColumnOrder { get; }
        }

        /// <summary>
        /// produces object[] result (with values in the specified column order)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="inst"></param>
        /// <returns></returns>
        public static object[] ToColumnOrderedResult<T>(this ColumnOrdinalSortable<T> inst)
        {
            var result = new List<object>();
            foreach (var column in inst.ColumnOrder)
            {
                //var expression = (MemberExpression)column.Body;
                result.Add(column((T)inst));
            }
            return result.ToArray();
        }

        /// <summary>
        /// sorts a collection of ColumnOrdinalSortables based on ColumnOrder<br/>
        /// e.g. instList.OrderBy(column1).ThenBy(column2).ThenBy(column3)... etc.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="instList"></param>
        /// <returns></returns>
        public static IEnumerable<ColumnOrdinalSortable<T>> SortByColumnOrder<T>(this IEnumerable<ColumnOrdinalSortable<T>> instList)
        {
            if (!(instList?.Any() ?? false))
            {
                return instList;
            }

            var firstInst = instList.First();
            var firstColumn = firstInst.ColumnOrder.First();
            var orderExpr = instList.OrderBy(i => firstColumn((T)i));
            foreach (var followingColumn in firstInst.ColumnOrder.Skip(1))
            {
                orderExpr = orderExpr.ThenBy(i => followingColumn((T)i));
            }
            return orderExpr.ToArray();
        }

        /// <summary>
        /// produces object[][] resultset (with elements sorted by column order)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="instList"></param>
        /// <returns></returns>
        public static object[][] ToSortedResultSet<T>(this IEnumerable<ColumnOrdinalSortable<T>> instList)
        {
            var result = new List<object[]>();
            var sortedInstList = SortByColumnOrder(instList);
            foreach (var inst in sortedInstList)
            {

                result.Add(ToColumnOrderedResult(inst));
            }
            return result.ToArray();
        }

        /// <summary>
        /// Produces SQL to declare a table variable for any arbitrary enumerable collection <br/>
        /// Only uses properties that are strings/ValueType/ValueType? and ignores the rest <br/>
        /// </summary>
        /// <typeparam name="T">the type of element contained in the collection.  This is the type that will be reflected on for property enumeration.</typeparam>
        /// <param name="instList">the collection to generate a table variable for</param>
        /// <param name="varName">variable name to declare under - this exact name is used (i.e. should specify "@myVariableName" and not "myVariableName")</param>
        /// <param name="udttName">if unspecified a table definition is generated inline, otherwise the UDTT is used</param>
        /// <returns></returns>
        public static string ToTableVariableDeclaration<T>(this IEnumerable<T> instList, string varName, string udttName = null) where T : class
        {
            var instProps = typeof(T).GetProperties().Where(p =>
                p.PropertyType.IsValueType ||
                p.PropertyType == typeof(string) ||
                (p.PropertyType.IsGenericType && p.PropertyType.GetGenericArguments()[0].IsValueType)
            ).ToArray();
            if (udttName == null)
            {
                udttName = $@"TABLE (
                    {instProps.Select(p => $"{p.Name} {SqlTestUtils.getSqlTypeName(p.PropertyType)}").Aggregate((p, c) => $"{p},{c}")}
                )";
            }
            return $@"
            DECLARE {varName} {udttName}
            {instList.Select(i => $@"
                INSERT INTO {varName} ({instProps.Select(p => $"{p.Name}").Aggregate((p, c) => $"{p},{c}")})
                VALUES ({instProps.Select(p => getSqlLiteral(p.GetValue(i))).Aggregate((p, c) => p + "," + c)})
            ").AggregateWithNewlineSeparator()}
            ";
        }
        /*
         *         
        [TestMethod]
        public void TestingArbitraryObjectTableVarGenerator()
        {
            //there's something funky with the way DateTimeOffsets are handled when it comes to ScalarValueCondition.ExpectedValue
            //workaround is to force a particular string format in the SELECT and check against that string
            var arbitraryObj = new { Foo = Utils.makeGuid("foo"), Bar = 2, Zap = false, Zoom = "zoom", Now = DateTimeOffset.Parse("2021-09-29T15:39:00.000Z") };

            var arbArray = new[] { arbitraryObj };

            var tableVarStatement = Utils.ToTableVariableDeclaration(arbArray, "@fooVar");

            RunTest(Actions.CreateBlock($@"
                {tableVarStatement}
                SELECT Foo, Bar, Zap, Zoom, FORMAT(Now,'yyyy-MM-ddTHH:mm:ss.fffZ') from @fooVar
            ").ResultsetShouldBe(1, new object[] { arbitraryObj.Foo, arbitraryObj.Bar, arbitraryObj.Zap, arbitraryObj.Zoom, arbitraryObj.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") }));
        }
         */


        public static string AggregateWithNewlineSeparator(this IEnumerable<string> lines)
        {
            return lines.Aggregate((p, c) => p + Environment.NewLine + c);
        }
    }
}
