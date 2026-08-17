using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    public class NifExprTests
    {
        /// <summary>
        /// Stands in for the model lookup: field names resolve from a dictionary,
        /// anything unknown resolves to 0 exactly as NifModelEval does.
        /// </summary>
        private static Func<object?, object?> Resolver(Dictionary<string, object> fields) =>
            v => v is string name ? (fields.TryGetValue(name, out var value) ? value : 0) : v;

        private static readonly Func<object?, object?> Empty = Resolver([]);

        [Theory]
        [InlineData("1", true)]
        [InlineData("0", false)]
        [InlineData("0x1", true)]
        [InlineData("0x0", false)]
        public void EvaluatesBareLiterals(string expr, bool expected) =>
            Assert.Equal(expected, new NifExpr(expr).EvaluateBool(Empty));

        [Fact]
        public void ResolvesFieldNamesThroughTheCallback()
        {
            var resolve = Resolver(new Dictionary<string, object> { ["Flags"] = 5u });

            Assert.Equal(5u, new NifExpr("Flags").EvaluateUInt(resolve));
        }

        [Fact]
        public void UnknownFieldNamesResolveToZero() =>
            Assert.False(new NifExpr("No Such Field").EvaluateBool(Empty));

        [Fact]
        public void FieldNamesMayContainSpaces()
        {
            var resolve = Resolver(new Dictionary<string, object> { ["User Version"] = 11u });

            Assert.True(new NifExpr("User Version == 11").EvaluateBool(resolve));
        }

        [Theory]
        [InlineData("Flags & 1", true)]
        [InlineData("Flags & 2", false)]
        [InlineData("Flags & 4", true)]
        [InlineData("Flags == 5", true)]
        [InlineData("Flags != 5", false)]
        [InlineData("Flags > 4", true)]
        [InlineData("Flags >= 5", true)]
        [InlineData("Flags < 5", false)]
        [InlineData("Flags <= 5", true)]
        public void EvaluatesBinaryOperators(string expr, bool expected)
        {
            var resolve = Resolver(new Dictionary<string, object> { ["Flags"] = 5u });

            Assert.Equal(expected, new NifExpr(expr).EvaluateBool(resolve));
        }

        [Fact]
        public void EvaluatesUnaryNot()
        {
            var resolve = Resolver(new Dictionary<string, object> { ["Flags"] = 1u });

            Assert.False(new NifExpr("!(Flags & 1)").EvaluateBool(resolve));
            Assert.True(new NifExpr("!(Flags & 2)").EvaluateBool(resolve));
        }

        [Fact]
        public void EvaluatesParenthesisedGroupsOnBothSides()
        {
            var resolve = Resolver(new Dictionary<string, object> { ["BS Version"] = 26u });

            // Straight from nif.xml, after token expansion.
            Assert.True(new NifExpr("(BS Version >= 24) && (BS Version <= 28)").EvaluateBool(resolve));
        }

        [Fact]
        public void EvaluatesMaskThenCompare()
        {
            // "(#ARG# #BITAND# 0x411) == 0x401" expands to this shape.
            var resolve = Resolver(new Dictionary<string, object> { ["Arg"] = 0x401u });

            Assert.True(new NifExpr("(Arg & 0x411) == 0x401").EvaluateBool(resolve));
            Assert.False(new NifExpr("(Arg & 0x411) == 0x11").EvaluateBool(resolve));
        }

        [Fact]
        public void ComparesVersionLiterals()
        {
            var resolve = Resolver(new Dictionary<string, object> { ["Version"] = 0x14020007u });

            Assert.True(new NifExpr("Version == 20.2.0.7").EvaluateBool(resolve));
            Assert.False(new NifExpr("Version == 20.0.0.5").EvaluateBool(resolve));
        }

        [Fact]
        public void FullyParenthesisedBooleanExpressionsGroupAsWritten()
        {
            // The shape nif.xml uses for almost every compound condition: each
            // comparison is wrapped, so the first-operator rule never bites.
            const string expr = "(BS Version >= 24) && ((BS Version <= 28) || (User Version == 12))";

            Assert.True(new NifExpr(expr).EvaluateBool(Resolver(new Dictionary<string, object>
            {
                ["BS Version"] = 26u,
                ["User Version"] = 0u
            })));

            Assert.True(new NifExpr(expr).EvaluateBool(Resolver(new Dictionary<string, object>
            {
                ["BS Version"] = 100u,
                ["User Version"] = 12u
            })));

            Assert.False(new NifExpr(expr).EvaluateBool(Resolver(new Dictionary<string, object>
            {
                ["BS Version"] = 100u,
                ["User Version"] = 0u
            })));

            Assert.False(new NifExpr(expr).EvaluateBool(Resolver(new Dictionary<string, object>
            {
                ["BS Version"] = 12u,
                ["User Version"] = 12u
            })));
        }

        [Fact]
        public void ComparisonBindsBeforeConjunctionWhenUnparenthesised()
        {
            // The parser takes the *first* operator it sees, with no precedence.
            // So this does NOT mean "(Version == 20.6.5.0) && (User Version >= 11)";
            // it means "Version == (20.6.5.0 && (User Version >= 11))".
            //
            // We reproduce that faithfully rather than quietly "fixing" it. Adding
            // precedence here would silently change how every other condition in
            // nif.xml parses, and nif.xml is written against this behaviour.
            //
            // In practice the only condition in the file that trips over this is a
            // vercond on the Epic Mickey blocks, all of which nif.xml marks
            // supported="false".
            var expr = new NifExpr("Version == 20.6.5.0 && User Version >= 11");

            Assert.Equal(
                "(Version == (335938816 && (User Version >= 11)))",
                expr.ToString());

            // 20.6.5.0 is truthy and 0 >= 11 is false, so the right side collapses
            // to false(0), and the whole thing reduces to "Version == 0".
            Assert.True(expr.EvaluateBool(Resolver(new Dictionary<string, object>
            {
                ["Version"] = 0u,
                ["User Version"] = 0u
            })));
        }

        [Fact]
        public void EmptyExpressionIsNop()
        {
            Assert.True(new NifExpr("").IsNop);
            Assert.True(new NifExpr(null).IsNop);
        }

        [Fact]
        public void UnbalancedParenthesesAreRejected() =>
            Assert.Throws<NifFormatException>(() => new NifExpr("(Flags & 1"));

        [Theory]
        [InlineData("20.2.0.7", 0x14020007u)]
        [InlineData("10.0.1.2", 0x0A000102u)]
        [InlineData("4.0.0.2", 0x04000002u)]
        [InlineData("", 0u)]
        public void ParsesVersionStrings(string s, uint expected) =>
            Assert.Equal(expected, NifVersion.FromString(s));

        [Fact]
        public void ParsesOldStyleTwoComponentVersion() =>
            // "4.123" means 4.1.2.3 -- digits after the dot are taken one at a time.
            Assert.Equal(0x04010203u, NifVersion.FromString("4.123"));

        [Fact]
        public void FormatsVersionStrings() =>
            Assert.Equal("20.2.0.7", NifVersion.ToVersionString(0x14020007));
    }
}
