using System.Reflection;
using FlightAssistant.Application.Administration;
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Tests.Administration
{
    /// <summary>
    /// The classifier the data browser advertises columns with, and that
    /// <c>BrowsableDataRepository.ApplyFilter</c> branches on. A kind is a promise the panel acts on
    /// — offer a dropdown, a date box, nothing at all — so what matters here is that the promise
    /// matches what the repository will accept, which is why the two read the same classifier.
    /// </summary>
    public class BrowsableColumnKindsTests
    {
        /// <summary>Every CLR type <c>ConvertFilterValue</c> parses, and the kind the panel is told
        /// to expect for it. A type this list gets wrong is a control that produces a 400.</summary>
        [Theory]
        [InlineData(typeof(string), BrowsableColumnKind.Text)]
        [InlineData(typeof(Guid), BrowsableColumnKind.Guid)]
        [InlineData(typeof(bool), BrowsableColumnKind.Bool)]
        [InlineData(typeof(DateTime), BrowsableColumnKind.Date)]
        [InlineData(typeof(DateTimeOffset), BrowsableColumnKind.Date)]
        [InlineData(typeof(DateOnly), BrowsableColumnKind.Date)]
        [InlineData(typeof(int), BrowsableColumnKind.Number)]
        [InlineData(typeof(long), BrowsableColumnKind.Number)]
        [InlineData(typeof(short), BrowsableColumnKind.Number)]
        [InlineData(typeof(double), BrowsableColumnKind.Number)]
        [InlineData(typeof(float), BrowsableColumnKind.Number)]
        [InlineData(typeof(decimal), BrowsableColumnKind.Number)]
        [InlineData(typeof(VerificationRequestStatus), BrowsableColumnKind.Enum)]
        public void Classify_NamesTheKindTheRepositoryCanFilter(Type type, BrowsableColumnKind expected)
        {
            Assert.Equal(expected, BrowsableColumnKinds.Classify(type));
        }

        /// <summary>A nullable column filters exactly like its underlying type — <c>ApplyFilter</c>
        /// unwraps it before converting — so it must advertise the same kind rather than falling
        /// through to Unsupported and disabling a control that works.</summary>
        [Theory]
        [InlineData(typeof(DateTime?), BrowsableColumnKind.Date)]
        [InlineData(typeof(int?), BrowsableColumnKind.Number)]
        [InlineData(typeof(bool?), BrowsableColumnKind.Bool)]
        [InlineData(typeof(VerificationRequestStatus?), BrowsableColumnKind.Enum)]
        public void Classify_UnwrapsNullable(Type type, BrowsableColumnKind expected)
        {
            Assert.Equal(expected, BrowsableColumnKinds.Classify(type));
        }

        /// <summary>Anything the repository has no conversion for is Unsupported, not guessed at:
        /// the panel disables the filter rather than offering one that 400s.</summary>
        [Theory]
        [InlineData(typeof(TimeSpan))]
        [InlineData(typeof(byte[]))]
        [InlineData(typeof(Uri))]
        public void Classify_IsUnsupportedForATypeTheRepositoryCannotConvert(Type type)
        {
            Assert.Equal(BrowsableColumnKind.Unsupported, BrowsableColumnKinds.Classify(type));
        }

        [Fact]
        public void Describe_GivesAnEnumColumnItsValues_AndEveryOtherKindNone()
        {
            var status = BrowsableColumnKinds.Describe(typeof(VerificationRequestStatus), "Status");
            Assert.Equal(BrowsableColumnKind.Enum, status.Kind);
            Assert.Equal(Enum.GetNames<VerificationRequestStatus>(), status.Values);

            var requested = BrowsableColumnKinds.Describe(typeof(DateTime), "RequestedUtc");
            Assert.Equal(BrowsableColumnKind.Date, requested.Kind);
            Assert.Null(requested.Values);
        }

        /// <summary>Not a snapshot of today's registry — a guard that every column the browser
        /// exposes resolves to a kind at all, so a new column of an exotic type shows up as
        /// Unsupported rather than throwing when the listing is built.</summary>
        [Fact]
        public void EveryExposedColumn_Classifies()
        {
            foreach (var table in BrowsableTables.All)
            {
                foreach (var columnName in table.Columns)
                {
                    var property = table.EntityType.GetProperty(columnName, BindingFlags.Public | BindingFlags.Instance)!;
                    var column = BrowsableColumnKinds.Describe(property.PropertyType, columnName);

                    Assert.Equal(columnName, column.Name);
                    Assert.True(
                        Enum.IsDefined(column.Kind),
                        $"{table.Slug}.{columnName} classified as {column.Kind}, which is not a kind.");
                    // Values belong to Enum columns alone: a null list is what tells the panel to
                    // render something other than a dropdown.
                    Assert.Equal(column.Kind == BrowsableColumnKind.Enum, column.Values is not null);
                }
            }
        }

        /// <summary>The queue's Status column is the one this whole endpoint exists for (#451): the
        /// panel needs its values to offer them instead of a text box.</summary>
        [Fact]
        public void TheVerificationQueuesStatusColumn_CarriesItsValues()
        {
            var table = BrowsableTables.All.Single(t => t.Slug == "verification-requests");
            var status = BrowsableColumnKinds.Describe(
                table.EntityType.GetProperty("Status", BindingFlags.Public | BindingFlags.Instance)!.PropertyType, "Status");

            Assert.Equal(BrowsableColumnKind.Enum, status.Kind);
            Assert.Contains("Pending", status.Values!);
        }
    }
}
