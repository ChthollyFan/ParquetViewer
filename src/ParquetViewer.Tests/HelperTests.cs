using ParquetViewer.Controls;
using ParquetViewer.Engine.Types;
using ParquetViewer.Helpers;
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
    }
}