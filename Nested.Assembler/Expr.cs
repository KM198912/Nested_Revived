using System;

namespace Nested.Assembler
{
    sealed class AsmException : Exception
    {
        public AsmException(string message) : base(message) { }
    }

    // Expression evaluator.
    // Precedence, high to low: unary - ~ ! +, * / %, + -, << >>, &, ^, |,
    // comparisons (== != < > <= >=), &&, ||
    // Comparisons bind weaker than & ^ | (unlike C), because the sources write
    // ".if temp__&0xffff < 0x8000" and mean (temp__&0xffff) < 0x8000.
    // TODO verify against the binary: LUT16 "Zero+0x200-value__*2" (Macros.asm).
    sealed class Expr
    {
        readonly string s;
        readonly Func<string, long> lookup;
        int p;

        Expr(string s, Func<string, long> lookup)
        {
            this.s = s;
            this.lookup = lookup;
        }

        public static long Eval(string text, Func<string, long> lookup)
        {
            var e = new Expr(text, lookup);
            long v = e.LogicOr();
            e.Skip();
            if (e.p != e.s.Length)
                throw new AsmException($"Unexpected '{e.s.Substring(e.p)}' in expression '{text}'");
            return v;
        }

        // ---------------------------------------------------------------

        void Skip()
        {
            while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
        }

        bool At(string op)
        {
            Skip();
            return p + op.Length <= s.Length && string.CompareOrdinal(s, p, op, 0, op.Length) == 0;
        }

        bool Eat(string op)
        {
            if (!At(op)) return false;
            p += op.Length;
            return true;
        }

        // Eats a single-char operator only if it isn't the start of a two-char one
        bool EatSingle(char c, string notFollowedBy)
        {
            Skip();
            if (p >= s.Length || s[p] != c) return false;
            if (p + 1 < s.Length && notFollowedBy.IndexOf(s[p + 1]) >= 0) return false;
            p++;
            return true;
        }

        // ---------------------------------------------------------------

        long LogicOr()
        {
            long l = LogicAnd();
            while (Eat("||")) { long r = LogicAnd(); l = (l != 0 || r != 0) ? 1 : 0; }
            return l;
        }

        long LogicAnd()
        {
            long l = Compare();
            while (Eat("&&")) { long r = Compare(); l = (l != 0 && r != 0) ? 1 : 0; }
            return l;
        }

        long Compare()
        {
            long l = BitOr();
            while (true)
            {
                if (Eat("==")) l = l == BitOr() ? 1 : 0;
                else if (Eat("!=")) l = l != BitOr() ? 1 : 0;
                else if (Eat("<=")) l = l <= BitOr() ? 1 : 0;
                else if (Eat(">=")) l = l >= BitOr() ? 1 : 0;
                else if (EatSingle('<', "<")) l = l < BitOr() ? 1 : 0;
                else if (EatSingle('>', ">")) l = l > BitOr() ? 1 : 0;
                else return l;
            }
        }

        long BitOr()
        {
            long l = BitXor();
            while (EatSingle('|', "|")) l |= BitXor();
            return l;
        }

        long BitXor()
        {
            long l = BitAnd();
            while (EatSingle('^', "")) l ^= BitAnd();
            return l;
        }

        long BitAnd()
        {
            long l = Shift();
            while (EatSingle('&', "&")) l &= Shift();
            return l;
        }

        long Shift()
        {
            long l = Add();
            while (true)
            {
                if (Eat("<<")) l <<= (int)Add();
                else if (Eat(">>")) l >>= (int)Add();
                else return l;
            }
        }

        long Add()
        {
            long l = Mul();
            while (true)
            {
                if (EatSingle('+', "")) l += Mul();
                else if (EatSingle('-', "")) l -= Mul();
                else return l;
            }
        }

        long Mul()
        {
            long l = Unary();
            while (true)
            {
                if (EatSingle('*', "")) l *= Unary();
                else if (EatSingle('/', ""))
                {
                    long r = Unary();
                    if (r == 0) throw new AsmException($"Division by zero in '{s}'");
                    l /= r;
                }
                else if (EatSingle('%', ""))
                {
                    long r = Unary();
                    if (r == 0) throw new AsmException($"Division by zero in '{s}'");
                    l %= r;
                }
                else return l;
            }
        }

        long Unary()
        {
            if (EatSingle('-', "")) return -Unary();
            if (EatSingle('~', "")) return ~Unary();
            if (EatSingle('!', "=")) return Unary() == 0 ? 1 : 0;
            if (EatSingle('+', "")) return Unary();
            return Primary();
        }

        long Primary()
        {
            Skip();
            if (p >= s.Length)
                throw new AsmException($"Expression '{s}' ends early");

            char c = s[p];
            if (c == '(')
            {
                p++;
                long v = LogicOr();
                if (!Eat(")")) throw new AsmException($"Missing ')' in '{s}'");
                return v;
            }

            if (char.IsDigit(c))
            {
                int start = p;
                while (p < s.Length && (char.IsLetterOrDigit(s[p]) || s[p] == '_')) p++;
                return ParseNumber(s.Substring(start, p - start));
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = p;
                while (p < s.Length && (char.IsLetterOrDigit(s[p]) || s[p] == '_')) p++;
                return lookup(s.Substring(start, p - start));
            }

            if (c == '{')
                throw new AsmException($"Unsubstituted macro parameter in '{s}'");

            throw new AsmException($"Unexpected '{c}' in expression '{s}'");
        }

        // ---------------------------------------------------------------

        // 0 decimal, 0x0 hex, 0h hex (trailing h), 0b0 / 0b binary
        public static long ParseNumber(string t)
        {
            if (TryParseNumber(t, out long v, out _)) return v;
            throw new AsmException($"Bad number '{t}'");
        }

        // hexDigits = number of hex digits written (0 for decimal/binary);
        // used to size unprefixed address literals like $0x00 vs $0x420d
        public static bool TryParseNumber(string t, out long v, out int hexDigits)
        {
            v = 0;
            hexDigits = 0;
            string l = t.ToLowerInvariant();
            try
            {
                if (l.StartsWith("0x") && l.Length > 2 && IsHex(l.Substring(2)))
                {
                    hexDigits = l.Length - 2;
                    v = Convert.ToInt64(l.Substring(2), 16);
                    return true;
                }
                if (l.StartsWith("0b") && l.Length > 2 && l.Substring(2).Trim('0', '1').Length == 0)
                {
                    v = Convert.ToInt64(l.Substring(2), 2);
                    return true;
                }
                if (l.EndsWith("h") && l.Length > 1 && IsHex(l.Substring(0, l.Length - 1)))
                {
                    hexDigits = l.Length - 1;
                    v = Convert.ToInt64(l.Substring(0, l.Length - 1), 16);
                    return true;
                }
                // 00000111b (SPC700 sources): only 0/1 digits before the b
                if (l.EndsWith("b") && l.Length > 1 && l.Substring(0, l.Length - 1).Trim('0', '1').Length == 0)
                {
                    v = Convert.ToInt64(l.Substring(0, l.Length - 1), 2);
                    return true;
                }
                if (l.Length > 0 && l.Trim('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').Length == 0)
                {
                    v = long.Parse(l);
                    return true;
                }
            }
            catch (OverflowException) { }
            return false;
        }

        public static bool IsHex(string t)
        {
            if (t.Length == 0) return false;
            foreach (char c in t)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        public static bool IsIdentifier(string t)
        {
            if (string.IsNullOrEmpty(t) || !(char.IsLetter(t[0]) || t[0] == '_')) return false;
            foreach (char c in t)
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            return true;
        }
    }
}
