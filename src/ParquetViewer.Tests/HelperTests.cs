using ParquetViewer.Controls;
using ParquetViewer.Engine.Types;
using ParquetViewer.Helpers;
using System.ComponentModel;
using System.Data;
using System.Globalization;

namespace ParquetViewer.Tests
{
    [TestClass]
    public class HelperTests
    {
        [TestMethod]
        [DataRow("1.0", null)]
        [DataRow("1.0.0", "1.0.0")]
        [DataRow("1.0.0.0", "1.0.0")]
        [DataRow("1.0.0.0.0", null)]
        [DataRow("v1.0.0", "1.0.0")]
        [DataRow("v1.0.0.0", "1.0.0")]
        [DataRow("99.99.99", "99.99.99")]
        [DataRow("99.99.99.99", "99.99.99.99")]
        [DataRow("1.0.0.1", "1.0.0.1")] //Build 非 0 时应保留 4 位版本号
        public void SEMANTIC_VERSION_PARSER_TESTS(string versionNumber, string? expectedParsedVersionNumber)
        {
            var isExpectedToBeValid = expectedParsedVersionNumber is not null;
            Assert.AreEqual(SemanticVersion.TryParse(versionNumber, out var semanticVersion), isExpectedToBeValid);
            if (isExpectedToBeValid)
            {
                Assert.AreEqual(expectedParsedVersionNumber, semanticVersion.ToString());
            }
        }

        [TestMethod]
        [DataRow("1.0.0", "1.0.1")]
        [DataRow("1.0.0.0", "1.0.0.1")]
        [DataRow("2.3.4", "3.0.1")]
        [DataRow("2.3.4.5", "3.0.0.1")]
        [DataRow("v1.2.3", "1.2.4")]
        [DataRow("v1.0.0.99", "1.0.1")]
        [DataRow("v99.98.99.99", "99.99.0.0")]
        public void SEMANTIC_VERSION_COMPARISON_TESTS(string smallerVersionNumber, string higherVersionNumber)
        {
            Assert.IsTrue(SemanticVersion.TryParse(smallerVersionNumber, out var smallerSemanticVersion), $"{smallerVersionNumber} is not a valid semantic version");
            Assert.IsTrue(SemanticVersion.TryParse(higherVersionNumber, out var higherSemanticVersion), $"{higherVersionNumber} is not a valid semantic version");
            Assert.IsTrue(smallerSemanticVersion < higherSemanticVersion, $"{smallerSemanticVersion} should have been lesser than {higherSemanticVersion}");
        }

        [TestMethod]
        public void ReturnsEmptyString_WhenInputIsNull()
        {
            Assert.IsEmpty(ParquetGridView.GenerateFilterQuery(null!));
        }

        [TestMethod]
        public void ReturnsEmptyString_WhenInputIsEmpty()
        {
            Assert.IsEmpty(ParquetGridView.GenerateFilterQuery(new()));
        }

        [TestMethod]
        public void SingleStringValue_GeneratesEqualsClause()
        {
            var query = ParquetGridView.GenerateFilterQuery(new()
            {
                ("Name", typeof(string), new object[] { "Alice" })
            });
            Assert.AreEqual("Name = 'Alice'", query);
        }

        [TestMethod]
        public void SingleIntValue_GeneratesEqualsClause()
        {
            var query = ParquetGridView.GenerateFilterQuery(new()
            {
                ("Age", typeof(int), new object[] { 42 })
            });
            Assert.AreEqual("Age = 42", query);
        }

        [TestMethod]
        public void SingleDateTimeValue_GeneratesEqualsClause()
        {
            var dt = new DateTime(2024, 1, 2, 3, 4, 5, 678);
            var query = ParquetGridView.GenerateFilterQuery(new()
            {
                ("Created", typeof(DateTime), new object[] { dt })
            });
            Assert.AreEqual($"Created = #{dt:yyyy-MM-dd HH:mm:ss.FFFFFFF}#", query);
        }

        [TestMethod]
        public void MultipleValues_GeneratesInClause()
        {
            var query = ParquetGridView.GenerateFilterQuery(new()
            {
                ("Age", typeof(int), new object[] { 1, 2, 3 })
            });
            Assert.AreEqual("Age IN (1,2,3)", query);
        }

        [TestMethod]
        public void MultipleStringValues_GeneratesInClauseWithQuotes()
        {
            var query = ParquetGridView.GenerateFilterQuery(new()
            {
                ("City", typeof(string), new object[] { "London", "Paris" })
            });
            Assert.AreEqual("City IN ('London','Paris')", query);
        }

        [TestMethod]
        public void HandlesNullValue_GeneratesIsNull()
        {
            var query = ParquetGridView.GenerateFilterQuery(new()
            {
                ("Name", typeof(string), new object[] { null! })
            });
            Assert.AreEqual("Name IS NULL", query);
        }

        [TestMethod]
        public void HandlesDBNullValue_GeneratesIsNull()
        {
            var query = ParquetGridView.GenerateFilterQuery(new()
            {
                ("Name", typeof(string), new object[] { DBNull.Value })
            });
            Assert.AreEqual("Name IS NULL", query);
        }

        [TestMethod]
        public void HandlesNullAndNonNullValues_GeneratesOrIsNull()
        {
            var query = ParquetGridView.GenerateFilterQuery(new()
            {
                ("Age", typeof(int), new object[] { 1, null! })
            });
            Assert.AreEqual("(Age IN (1) OR Age IS NULL)", query);
        }

        [TestMethod]
        public void HandlesNullAndNonNullValues_GeneratesOrIsNullAndCombinesWithAnd()
        {
            var query = ParquetGridView.GenerateFilterQuery(new()
            {
                ("Age", typeof(int), new object[] { 1, null! }),
                ("Name", typeof(string), new object[] { "Alice", "Alice" })
            });
            Assert.AreEqual("(Age IN (1) OR Age IS NULL) AND Name = 'Alice'", query);
        }

        [TestMethod]
        public void HandlesMultipleColumns_CombinesWithAnd()
        {
            var query = ParquetGridView.GenerateFilterQuery(new()
            {
                ("Name", typeof(string), new object[] { "Alice" }),
                ("Age", typeof(int), new object[] { 30 })
            });
            Assert.AreEqual("Name = 'Alice' AND Age = 30", query);
        }

        [TestMethod]
        public void ColumnNameWithSpaces_IsWrappedInBrackets()
        {
            var query = ParquetGridView.GenerateFilterQuery(new()
            {
                ("First Name", typeof(string), new object[] { "Bob" })
            });
            Assert.AreEqual("[First Name] = 'Bob'", query);
        }

        [TestMethod]
        public void FloatScientificNotation_IsWrappedInQuotes()
        {
            var query = ParquetGridView.GenerateFilterQuery(new()
            {
                ("Value", typeof(double), new object[] { 1.23e20 })
            });
            Assert.AreEqual("Value = '1.23E+20'", query);
        }

        [TestMethod]
        [DataRow("de-DE")] //comma decimal separator
        [DataRow("fr-FR")] //comma decimal separator + narrow no-break group separator
        [DataRow("th-TH")] //non-Gregorian calendar by default
        public void NumericAndDateFilters_AreCultureInvariant(string cultureName)
        {
            //DataView.RowFilter always expects '.' as the decimal separator and a Gregorian date,
            //regardless of the user's locale. Without invariant formatting, a culture that uses ','
            //would corrupt the filter since ',' also separates values inside an `IN (...)` clause.
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(cultureName);

                Assert.AreEqual("Value = 1.5", ParquetGridView.GenerateFilterQuery(new()
                {
                    ("Value", typeof(double), new object[] { 1.5d })
                }));

                Assert.AreEqual("Value = '1.23E+20'", ParquetGridView.GenerateFilterQuery(new()
                {
                    ("Value", typeof(double), new object[] { 1.23e20 })
                }));

                Assert.AreEqual("Value IN (1.5,2.5)", ParquetGridView.GenerateFilterQuery(new()
                {
                    ("Value", typeof(double), new object[] { 1.5d, 2.5d })
                }));

                Assert.AreEqual("Value = #2024-01-31 13:45:30#", ParquetGridView.GenerateFilterQuery(new()
                {
                    ("Value", typeof(DateTime), new object[] { new DateTime(2024, 1, 31, 13, 45, 30) })
                }));
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        [TestMethod]
        public void BuildSortExpression_ReturnsEmptyString_WhenThereAreNoSortDescriptors()
        {
            Assert.IsEmpty(ParquetGridView.BuildSortExpression(null!));
            Assert.IsEmpty(ParquetGridView.BuildSortExpression(new List<ParquetGridView.SortDescriptor>()));
        }

        [TestMethod]
        public void BuildSortExpression_UsesClickOrderAsSortPriority()
        {
            var expression = ParquetGridView.BuildSortExpression(new List<ParquetGridView.SortDescriptor>
            {
                new ParquetGridView.SortDescriptor("Name", ListSortDirection.Ascending),
                new ParquetGridView.SortDescriptor("Age", ListSortDirection.Descending)
            });

            Assert.AreEqual("Name ASC, Age DESC", expression);
        }

        [TestMethod]
        public void BuildSortExpression_EscapesColumnNamesThatAreNotPlainIdentifiers()
        {
            var expression = ParquetGridView.BuildSortExpression(new List<ParquetGridView.SortDescriptor>
            {
                new ParquetGridView.SortDescriptor("First Name", ListSortDirection.Descending),
                new ParquetGridView.SortDescriptor("a.b", ListSortDirection.Ascending),
                new ParquetGridView.SortDescriptor("_id9", ListSortDirection.Ascending)
            });

            Assert.AreEqual("[First Name] DESC, [a.b] ASC, _id9 ASC", expression);
        }

        [TestMethod]
        public void BuildSortExpression_ProducesMultiColumnSortAndEmptySortRestoresOriginalRowOrder()
        {
            //验证生成的表达式能被 DataView 直接使用：点击顺序决定优先级，清空表达式后恢复原始行序
            using var dataTable = new DataTable();
            dataTable.Columns.Add("Group", typeof(string));
            dataTable.Columns.Add("Score", typeof(int));
            dataTable.Rows.Add("B", 1);
            dataTable.Rows.Add("A", 2);
            dataTable.Rows.Add("B", 3);
            dataTable.Rows.Add("A", 1);

            dataTable.DefaultView.Sort = ParquetGridView.BuildSortExpression(new List<ParquetGridView.SortDescriptor>
            {
                new ParquetGridView.SortDescriptor("Group", ListSortDirection.Ascending),
                new ParquetGridView.SortDescriptor("Score", ListSortDirection.Descending)
            });

            CollectionAssert.AreEqual(new[] { "A2", "A1", "B3", "B1" }, ReadGroupAndScore(dataTable.DefaultView));

            dataTable.DefaultView.Sort = string.Empty;
            CollectionAssert.AreEqual(new[] { "B1", "A2", "B3", "A1" }, ReadGroupAndScore(dataTable.DefaultView));
        }

        private static string[] ReadGroupAndScore(DataView dataView)
        {
            return dataView.Cast<DataRowView>().Select(row => $"{row["Group"]}{row["Score"]}").ToArray();
        }

        [TestMethod]
        public void ByteArrayValue_IsCorrectlyTruncated()
        {
            var byteArrayValue = new Engine.Types.ByteArrayValue([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x10]);
            Assert.AreEqual("01[...]10", byteArrayValue.ToStringTruncated(1));
            Assert.AreEqual("01[...]10", byteArrayValue.ToStringTruncated(2));
            Assert.AreEqual("01-02[...]09-10", byteArrayValue.ToStringTruncated(11));
            Assert.AreEqual("01-02-03-04[...]07-08-09-10", byteArrayValue.ToStringTruncated(28));
            Assert.AreEqual("01-02-03-04-05-06-07-08-09-10", byteArrayValue.ToStringTruncated(29));
            Assert.AreEqual("01-02-03-04-05-06-07-08-09-10", byteArrayValue.ToString());
        }

        [TestMethod]
        [DataRow("1.0.0", "1.0.1", null, true)] //远端有新版本且用户没有记住过任何版本
        [DataRow("1.0.0", "1.0.1", "1.0.1", false)] //记住的正是本次要提示的版本
        [DataRow("1.0.0", "1.0.2", "1.0.1", true)] //远端又出现了更新的版本，记住的更旧版本不应继续生效
        [DataRow("1.0.0", "1.0.0", null, false)] //远端版本与当前版本相同
        [DataRow("1.0.1", "1.0.0", null, false)] //远端版本落后于当前版本
        [DataRow("1.0.0", null, null, false)] //没有查询到远端版本
        [DataRow("1.0.0", "1.0.1", "not-a-version", true)] //记住的版本号无法解析，视为没有记住
        public void UPDATE_PROMPT_POLICY_TESTS(string currentVersion, string? latestVersion, string? skippedVersion, bool expectedShouldPrompt)
        {
            Assert.IsTrue(SemanticVersion.TryParse(currentVersion, out var parsedCurrentVersion));

            SemanticVersion? parsedLatestVersion = null;
            if (latestVersion is not null)
            {
                Assert.IsTrue(SemanticVersion.TryParse(latestVersion, out var parsedVersion));
                parsedLatestVersion = parsedVersion;
            }

            Assert.AreEqual(expectedShouldPrompt, UpdatePromptPolicy.ShouldPrompt(parsedCurrentVersion, parsedLatestVersion, skippedVersion));
        }
    }
}