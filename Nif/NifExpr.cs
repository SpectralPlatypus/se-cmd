using System.Globalization;
using System.Text.RegularExpressions;

namespace SECmd.Nif
{
    /// <summary>
    /// A parsed <c>cond</c>, <c>vercond</c>, <c>arg</c> or array-length expression
    /// from nif.xml.
    /// </summary>
    /// <remarks>
    /// This is a port of NifSkope's NifExpr, and deliberately keeps its parsing
    /// strategy: split on the first operator found left to right, recursing into
    /// parenthesised groups. That is not operator-precedence parsing — <c>a &amp;&amp; b == c</c>
    /// groups as <c>a &amp;&amp; (b == c)</c> — but nif.xml is written against exactly this
    /// behaviour, so reproducing it is the correct thing to do. Every real
    /// expression in the file parenthesises anything ambiguous.
    ///
    /// Operands are stored untyped: a nested <see cref="NifExpr"/>, a
    /// <see cref="string"/> naming a field, or a boxed numeric literal.
    /// </remarks>
    public sealed partial class NifExpr
    {
        public enum Op
        {
            Nop,
            NotEq,
            Eq,
            Gte,
            Lte,
            Gt,
            Lt,
            BitAnd,
            BitOr,
            Add,
            Sub,
            Div,
            Mul,
            BoolAnd,
            BoolOr,
            Not,
            Lsh,
            Rsh
        }

        private object? _lhs;
        private object? _rhs;
        private Op _opcode = Op.Nop;

        /// <summary>True when the expression is empty and evaluates to nothing.</summary>
        public bool IsNop => _opcode == Op.Nop && _lhs is null;

        public NifExpr()
        {
        }

        public NifExpr(string? cond)
        {
            Partition(cond);
        }

        [GeneratedRegex(@"^\s*!(.*)", RegexOptions.Singleline)]
        private static partial Regex UnaryRegex();

        [GeneratedRegex(@"(!=|==|>=|<=|>>|<<|>|<|\+|-|/|\*|&&|\|\||&|\|)")]
        private static partial Regex OperatorRegex();

        [GeneratedRegex(@"^\s*\(")]
        private static partial Regex LeftParenRegex();

        [GeneratedRegex(@"\A[-+]?[0-9]+\z")]
        private static partial Regex IntRegex();

        [GeneratedRegex(@"\A0[xX][0-9a-fA-F]+\z")]
        private static partial Regex HexRegex();

        /// <summary>
        /// Locates the outermost parenthesised group starting at <paramref name="offset"/>.
        /// </summary>
        private static bool MatchGroup(string cond, int offset, out int startPos, out int endPos)
        {
            int depth = 0;
            startPos = -1;
            endPos = -1;

            for (int i = offset; i < cond.Length; i++)
            {
                switch (cond[i])
                {
                    case '(':
                        if (startPos == -1)
                            startPos = i;

                        depth++;
                        break;

                    case ')':
                        if (--depth == 0)
                        {
                            endPos = i;
                            return true;
                        }

                        break;
                }
            }

            if (startPos != -1 || endPos != -1)
                throw new NifFormatException($"expression syntax error, non-matching brackets in \"{cond}\"");

            return false;
        }

        private static Op OperatorFromString(string s) => s switch
        {
            "!" => Op.Not,
            "!=" => Op.NotEq,
            "==" => Op.Eq,
            ">=" => Op.Gte,
            "<=" => Op.Lte,
            ">" => Op.Gt,
            "<" => Op.Lt,
            "&" => Op.BitAnd,
            "|" => Op.BitOr,
            "+" => Op.Add,
            "-" => Op.Sub,
            "/" => Op.Div,
            "*" => Op.Mul,
            "&&" => Op.BoolAnd,
            "||" => Op.BoolOr,
            "<<" => Op.Lsh,
            ">>" => Op.Rsh,
            _ => Op.Nop
        };

        private void Partition(string? condition)
        {
            if (string.IsNullOrEmpty(condition))
            {
                _opcode = Op.Nop;
                return;
            }

            string cond = condition;

            // Unary not binds the whole remainder, as it does in NifSkope.
            Match unary = UnaryRegex().Match(cond);
            if (unary.Success)
            {
                _opcode = Op.Not;
                _rhs = new NifExpr(unary.Groups[1].Value.Trim());
                return;
            }

            int lStart, lEnd, oStart, oEnd;

            if (LeftParenRegex().IsMatch(cond))
            {
                // The expression opens with a group: consume it, then look for an
                // operator after the closing paren.
                MatchGroup(cond, 0, out lStart, out lEnd);
                Match afterGroup = OperatorRegex().Match(cond, lEnd + 1);

                // Step inside the parentheses.
                lStart++;
                lEnd--;

                if (!afterGroup.Success)
                {
                    // Nothing follows the group, so the group *is* the expression.
                    Partition(cond.Substring(lStart, lEnd - lStart + 1));
                    return;
                }

                oStart = afterGroup.Index;
                oEnd = oStart + afterGroup.Value.Length;
            }
            else
            {
                Match op = OperatorRegex().Match(cond);
                if (!op.Success)
                {
                    // Terminal: a literal or a field name.
                    _lhs = ParseTerminal(cond);
                    _opcode = Op.Nop;
                    return;
                }

                lStart = 0;
                lEnd = op.Index - 1;
                oStart = op.Index;
                oEnd = oStart + op.Value.Length;
            }

            var lhsExpr = new NifExpr(cond.Substring(lStart, lEnd - lStart + 1).Trim());
            var rhsExpr = new NifExpr(cond[oEnd..].Trim());

            _lhs = lhsExpr._opcode == Op.Nop ? lhsExpr._lhs : lhsExpr;
            _opcode = OperatorFromString(cond[oStart..oEnd]);
            _rhs = rhsExpr._opcode == Op.Nop ? rhsExpr._lhs : rhsExpr;
        }

        /// <summary>
        /// Classifies a leaf token as a hex literal, a decimal literal, a version
        /// literal, or a field name left as a string for the evaluator to resolve.
        /// </summary>
        private static object ParseTerminal(string cond)
        {
            if (HexRegex().IsMatch(cond))
                return uint.Parse(cond.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            if (IntRegex().IsMatch(cond))
            {
                if (int.TryParse(cond, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                    return i;

                if (uint.TryParse(cond, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint u))
                    return u;
            }

            if (NifVersion.IsVersionLiteral(cond))
                return NifVersion.FromString(cond);

            return cond;
        }

        // --- evaluation ------------------------------------------------------

        /// <summary>
        /// Evaluates the expression, using <paramref name="resolve"/> to turn field
        /// names into their current values.
        /// </summary>
        public object? EvaluateValue(Func<object?, object?> resolve)
        {
            object? l = ResolveOperand(_lhs, resolve);
            object? r = ResolveOperand(_rhs, resolve);

            return _opcode switch
            {
                Op.Not => !ToBool(r),
                Op.NotEq => !ValuesEqual(l, r),
                Op.Eq => ValuesEqual(l, r),
                Op.Gte => ToUInt(l) >= ToUInt(r),
                Op.Lte => ToUInt(l) <= ToUInt(r),
                Op.Gt => ToUInt(l) > ToUInt(r),
                Op.Lt => ToUInt(l) < ToUInt(r),
                Op.BitAnd => ToUInt(l) & ToUInt(r),
                Op.BitOr => ToUInt(l) | ToUInt(r),
                Op.Add => ToUInt(l) + ToUInt(r),
                Op.Sub => ToUInt(l) - ToUInt(r),
                Op.Div => ToUInt(r) == 0 ? 0u : ToUInt(l) / ToUInt(r),
                Op.Mul => ToUInt(l) * ToUInt(r),
                Op.BoolAnd => ToBool(l) && ToBool(r),
                Op.BoolOr => ToBool(l) || ToBool(r),
                Op.Lsh => ToULong(l) << (int)ToUInt(r),
                Op.Rsh => ToULong(l) >> (int)ToUInt(r),
                _ => l
            };
        }

        public bool EvaluateBool(Func<object?, object?> resolve) => ToBool(EvaluateValue(resolve));

        public uint EvaluateUInt(Func<object?, object?> resolve) => ToUInt(EvaluateValue(resolve));

        public ulong EvaluateUInt64(Func<object?, object?> resolve) => ToULong(EvaluateValue(resolve));

        private static object? ResolveOperand(object? operand, Func<object?, object?> resolve) =>
            operand is NifExpr nested ? nested.EvaluateValue(resolve) : resolve(operand);

        /// <summary>
        /// Compares two evaluated operands. Numerics compare numerically; a string
        /// that survived resolution compares as text against another string.
        /// </summary>
        private static bool ValuesEqual(object? l, object? r)
        {
            if (l is string ls && r is string rs)
                return string.Equals(ls, rs, StringComparison.Ordinal);

            return ToULong(l) == ToULong(r);
        }

        internal static bool ToBool(object? v) => v switch
        {
            null => false,
            bool b => b,
            string s => !string.IsNullOrEmpty(s) && s != "0",
            _ => ToULong(v) != 0
        };

        internal static uint ToUInt(object? v) => unchecked((uint)ToULong(v));

        internal static ulong ToULong(object? v) => v switch
        {
            null => 0UL,
            bool b => b ? 1UL : 0UL,
            int i => unchecked((ulong)(long)i),
            uint u => u,
            long l => unchecked((ulong)l),
            ulong ul => ul,
            string s => ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed) ? parsed : 0UL,
            _ => 0UL
        };

        public override string ToString()
        {
            string l = _lhs?.ToString() ?? string.Empty;
            string r = _rhs?.ToString() ?? string.Empty;

            return _opcode switch
            {
                Op.Not => $"!{r}",
                Op.NotEq => $"({l} != {r})",
                Op.Eq => $"({l} == {r})",
                Op.Gte => $"({l} >= {r})",
                Op.Lte => $"({l} <= {r})",
                Op.Gt => $"({l} > {r})",
                Op.Lt => $"({l} < {r})",
                Op.BitAnd => $"({l} & {r})",
                Op.BitOr => $"({l} | {r})",
                Op.Add => $"({l} + {r})",
                Op.Sub => $"({l} - {r})",
                Op.Div => $"({l} / {r})",
                Op.Mul => $"({l} * {r})",
                Op.BoolAnd => $"({l} && {r})",
                Op.BoolOr => $"({l} || {r})",
                Op.Lsh => $"({l} << {r})",
                Op.Rsh => $"({l} >> {r})",
                _ => l
            };
        }
    }
}
