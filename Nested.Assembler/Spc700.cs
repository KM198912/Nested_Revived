using System;
using System.Collections.Generic;

namespace Nested.Assembler
{
    // SPC700 instructions for code included with .includeSPC (Project/Spc700/Spc.asm).
    // Syntax: #imm, dp, !abs, dp+x, dp+y, !abs+x, !abs+y, [dp]+y, [dp+x], [!abs+x],
    // (x), (x)+, (y), registers a x y ya sp psw c, bit forms "set1 dp, bit" /
    // "bbs dp, bit, rel" (also dp.bit), m.b forms "and1 c, addr.bit" / "c, /addr.bit".
    // Branch targets: label, +near, -near (an optional '$' in front is accepted).
    sealed partial class Assembler
    {
        enum SpcKind
        {
            A, X, Y, YA, SP, PSW, C,
            Imm, Dp, DpX, DpY, Abs, AbsX, AbsY,
            IndX, IndXInc, IndY, IndDpX, IndDpY, AbsIndX,
        }

        struct SpcOperand
        {
            public SpcKind Kind;
            public long Value;
        }

        // "mnemonic|Kind,Kind" -> opcode; rev = operand bytes are written last-to-first
        // (mov dp,#imm = 8F imm dp; mov dd,ds = FA ds dd; and the ALU forms of both)
        static readonly Dictionary<string, (byte op, bool rev)> spcTable = BuildSpcTable();

        static readonly HashSet<string> spcBranches = new HashSet<string> { "bra", "beq", "bne", "bcc", "bcs", "bmi", "bpl", "bvc", "bvs" };
        static readonly Dictionary<string, byte> spcBranchOps = new Dictionary<string, byte>
        {
            ["bpl"] = 0x10, ["bra"] = 0x2F, ["bmi"] = 0x30, ["bvc"] = 0x50, ["bvs"] = 0x70,
            ["bcc"] = 0x90, ["bcs"] = 0xB0, ["bne"] = 0xD0, ["beq"] = 0xF0,
        };
        static readonly Dictionary<string, byte> spcMemBitOps = new Dictionary<string, byte>
        {
            ["or1"] = 0x0A, ["and1"] = 0x4A, ["eor1"] = 0x8A, ["mov1"] = 0xAA, ["not1"] = 0xEA,
        };

        static readonly HashSet<string> spcMnemonics = BuildSpcMnemonics();

        static Dictionary<string, (byte, bool)> BuildSpcTable()
        {
            var t = new Dictionary<string, (byte, bool)>();
            void Add(string m, int op, bool rev, params SpcKind[] k)
                => t[m + "|" + string.Join(",", k)] = ((byte)op, rev);

            foreach (var (m, b) in new[] { ("or", 0x00), ("and", 0x20), ("eor", 0x40), ("cmp", 0x60), ("adc", 0x80), ("sbc", 0xA0) })
            {
                Add(m, b + 0x04, false, SpcKind.A, SpcKind.Dp);
                Add(m, b + 0x05, false, SpcKind.A, SpcKind.Abs);
                Add(m, b + 0x06, false, SpcKind.A, SpcKind.IndX);
                Add(m, b + 0x07, false, SpcKind.A, SpcKind.IndDpX);
                Add(m, b + 0x08, false, SpcKind.A, SpcKind.Imm);
                Add(m, b + 0x09, true, SpcKind.Dp, SpcKind.Dp);
                Add(m, b + 0x14, false, SpcKind.A, SpcKind.DpX);
                Add(m, b + 0x15, false, SpcKind.A, SpcKind.AbsX);
                Add(m, b + 0x16, false, SpcKind.A, SpcKind.AbsY);
                Add(m, b + 0x17, false, SpcKind.A, SpcKind.IndDpY);
                Add(m, b + 0x18, true, SpcKind.Dp, SpcKind.Imm);
                Add(m, b + 0x19, false, SpcKind.IndX, SpcKind.IndY);
            }
            Add("cmp", 0x1E, false, SpcKind.X, SpcKind.Abs);
            Add("cmp", 0x3E, false, SpcKind.X, SpcKind.Dp);
            Add("cmp", 0x5E, false, SpcKind.Y, SpcKind.Abs);
            Add("cmp", 0x7E, false, SpcKind.Y, SpcKind.Dp);
            Add("cmp", 0xC8, false, SpcKind.X, SpcKind.Imm);
            Add("cmp", 0xAD, false, SpcKind.Y, SpcKind.Imm);

            foreach (var (m, b) in new[] { ("asl", 0x00), ("rol", 0x20), ("lsr", 0x40), ("ror", 0x60) })
            {
                Add(m, b + 0x0B, false, SpcKind.Dp);
                Add(m, b + 0x0C, false, SpcKind.Abs);
                Add(m, b + 0x1B, false, SpcKind.DpX);
                Add(m, b + 0x1C, false, SpcKind.A);
            }
            Add("inc", 0xAB, false, SpcKind.Dp); Add("inc", 0xAC, false, SpcKind.Abs); Add("inc", 0xBB, false, SpcKind.DpX);
            Add("inc", 0xBC, false, SpcKind.A); Add("inc", 0x3D, false, SpcKind.X); Add("inc", 0xFC, false, SpcKind.Y);
            Add("dec", 0x8B, false, SpcKind.Dp); Add("dec", 0x8C, false, SpcKind.Abs); Add("dec", 0x9B, false, SpcKind.DpX);
            Add("dec", 0x9C, false, SpcKind.A); Add("dec", 0x1D, false, SpcKind.X); Add("dec", 0xDC, false, SpcKind.Y);

            Add("decw", 0x1A, false, SpcKind.Dp);
            Add("cmpw", 0x5A, false, SpcKind.YA, SpcKind.Dp);
            Add("addw", 0x7A, false, SpcKind.YA, SpcKind.Dp);
            Add("subw", 0x9A, false, SpcKind.YA, SpcKind.Dp);
            Add("movw", 0xBA, false, SpcKind.YA, SpcKind.Dp);
            Add("movw", 0xDA, false, SpcKind.Dp, SpcKind.YA);

            Add("push", 0x0D, false, SpcKind.PSW); Add("push", 0x2D, false, SpcKind.A);
            Add("push", 0x4D, false, SpcKind.X); Add("push", 0x6D, false, SpcKind.Y);
            Add("pop", 0x8E, false, SpcKind.PSW); Add("pop", 0xAE, false, SpcKind.A);
            Add("pop", 0xCE, false, SpcKind.X); Add("pop", 0xEE, false, SpcKind.Y);

            Add("mov", 0xE6, false, SpcKind.A, SpcKind.IndX);
            Add("mov", 0xE7, false, SpcKind.A, SpcKind.IndDpX);
            Add("mov", 0xE8, false, SpcKind.A, SpcKind.Imm);
            Add("mov", 0xE4, false, SpcKind.A, SpcKind.Dp);
            Add("mov", 0xE5, false, SpcKind.A, SpcKind.Abs);
            Add("mov", 0xF4, false, SpcKind.A, SpcKind.DpX);
            Add("mov", 0xF5, false, SpcKind.A, SpcKind.AbsX);
            Add("mov", 0xF6, false, SpcKind.A, SpcKind.AbsY);
            Add("mov", 0xF7, false, SpcKind.A, SpcKind.IndDpY);
            Add("mov", 0xBF, false, SpcKind.A, SpcKind.IndXInc);
            Add("mov", 0x7D, false, SpcKind.A, SpcKind.X);
            Add("mov", 0xDD, false, SpcKind.A, SpcKind.Y);
            Add("mov", 0x5D, false, SpcKind.X, SpcKind.A);
            Add("mov", 0x9D, false, SpcKind.X, SpcKind.SP);
            Add("mov", 0xCD, false, SpcKind.X, SpcKind.Imm);
            Add("mov", 0xF8, false, SpcKind.X, SpcKind.Dp);
            Add("mov", 0xF9, false, SpcKind.X, SpcKind.DpY);
            Add("mov", 0xE9, false, SpcKind.X, SpcKind.Abs);
            Add("mov", 0xFD, false, SpcKind.Y, SpcKind.A);
            Add("mov", 0x8D, false, SpcKind.Y, SpcKind.Imm);
            Add("mov", 0xEB, false, SpcKind.Y, SpcKind.Dp);
            Add("mov", 0xFB, false, SpcKind.Y, SpcKind.DpX);
            Add("mov", 0xEC, false, SpcKind.Y, SpcKind.Abs);
            Add("mov", 0xBD, false, SpcKind.SP, SpcKind.X);
            Add("mov", 0xC4, false, SpcKind.Dp, SpcKind.A);
            Add("mov", 0xC5, false, SpcKind.Abs, SpcKind.A);
            Add("mov", 0xC6, false, SpcKind.IndX, SpcKind.A);
            Add("mov", 0xC7, false, SpcKind.IndDpX, SpcKind.A);
            Add("mov", 0xD4, false, SpcKind.DpX, SpcKind.A);
            Add("mov", 0xD5, false, SpcKind.AbsX, SpcKind.A);
            Add("mov", 0xD6, false, SpcKind.AbsY, SpcKind.A);
            Add("mov", 0xD7, false, SpcKind.IndDpY, SpcKind.A);
            Add("mov", 0xAF, false, SpcKind.IndXInc, SpcKind.A);
            Add("mov", 0xD8, false, SpcKind.Dp, SpcKind.X);
            Add("mov", 0xD9, false, SpcKind.DpY, SpcKind.X);
            Add("mov", 0xC9, false, SpcKind.Abs, SpcKind.X);
            Add("mov", 0xCB, false, SpcKind.Dp, SpcKind.Y);
            Add("mov", 0xDB, false, SpcKind.DpX, SpcKind.Y);
            Add("mov", 0xCC, false, SpcKind.Abs, SpcKind.Y);
            Add("mov", 0x8F, true, SpcKind.Dp, SpcKind.Imm);
            Add("mov", 0xFA, true, SpcKind.Dp, SpcKind.Dp);

            foreach (var (m, op) in new[] { ("nop", 0x00), ("brk", 0x0F), ("clrp", 0x20), ("setp", 0x40), ("clrc", 0x60),
                ("ret", 0x6F), ("reti", 0x7F), ("setc", 0x80), ("ei", 0xA0), ("di", 0xC0), ("clrv", 0xE0),
                ("notc", 0xED), ("sleep", 0xEF), ("stop", 0xFF) })
                Add(m, op, false);
            Add("xcn", 0x9F, false, SpcKind.A);
            Add("das", 0xBE, false, SpcKind.A);
            Add("daa", 0xDF, false, SpcKind.A);
            Add("mul", 0xCF, false, SpcKind.YA);
            Add("div", 0x9E, false, SpcKind.YA, SpcKind.X);
            Add("tset1", 0x0E, false, SpcKind.Abs);
            Add("tclr1", 0x4E, false, SpcKind.Abs);
            Add("jmp", 0x5F, false, SpcKind.Abs);
            Add("jmp", 0x1F, false, SpcKind.AbsIndX);
            Add("call", 0x3F, false, SpcKind.Abs);
            return t;
        }

        static HashSet<string> BuildSpcMnemonics()
        {
            var h = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in spcTable.Keys)
                h.Add(key.Substring(0, key.IndexOf('|')));
            foreach (var m in spcBranches) h.Add(m);
            foreach (var m in spcMemBitOps.Keys) h.Add(m);
            foreach (var m in new[] { "cbne", "dbnz", "bbs", "bbc", "set1", "clr1", "tcall", "pcall", "incw" }) h.Add(m);
            return h;
        }

        static bool IsSpcMnemonic(string m) => spcMnemonics.Contains(m);

        // ---------------------------------------------------------------

        void SpcInstruction(string m, string a)
        {
            var ops = a.Trim().Length == 0 ? new List<string>() : SplitTop(a);
            long start = pc;

            if (spcBranchOps.TryGetValue(m, out byte bop))
            {
                Need(m, ops, 1);
                SpcRelative(new[] { bop }, ops[0], start);
                return;
            }

            switch (m)
            {
                case "cbne":
                    {
                        Need(m, ops, 2);
                        var d = SpcParse(ops[0]);
                        if (d.Kind != SpcKind.Dp && d.Kind != SpcKind.DpX) throw new AsmException("cbne needs dp or dp+x");
                        SpcRelative(new[] { (byte)(d.Kind == SpcKind.Dp ? 0x2E : 0xDE), (byte)d.Value }, ops[1], start);
                        return;
                    }
                case "dbnz":
                    {
                        Need(m, ops, 2);
                        var d = SpcParse(ops[0]);
                        if (d.Kind == SpcKind.Y) SpcRelative(new byte[] { 0xFE }, ops[1], start);
                        else if (d.Kind == SpcKind.Dp) SpcRelative(new[] { (byte)0x6E, (byte)d.Value }, ops[1], start);
                        else throw new AsmException("dbnz needs y or dp");
                        return;
                    }
                case "bbs":
                case "bbc":
                    {
                        // bbs dp, bit, rel  (or dp.bit, rel)
                        SplitBit(ops, out long dp, out int bit, out var rest);
                        if (rest.Count != 1) throw new AsmException($"{m} needs dp, bit, target");
                        int op = (m == "bbs" ? 0x03 : 0x13) | bit << 5;
                        SpcRelative(new[] { (byte)op, (byte)dp }, rest[0], start);
                        return;
                    }
                case "set1":
                case "clr1":
                    {
                        SplitBit(ops, out long dp, out int bit, out var rest);
                        if (rest.Count != 0) throw new AsmException($"{m} needs dp, bit");
                        Emit((m == "set1" ? 0x02 : 0x12) | bit << 5);
                        Emit(dp);
                        return;
                    }
                case "tcall":
                    {
                        Need(m, ops, 1);
                        long n = Eval(ops[0]);
                        if (n < 0 || n > 15) throw new AsmException("tcall takes 0-15");
                        Emit(0x01 | n << 4);
                        return;
                    }
                case "incw":
                    {
                        // The original assembler writes incw dp as AB (inc dp, 8-bit) instead of 3A:
                        // Spc.asm:2227/2230/2238/2448/2449 are AB in the template (0x2111C ...).
                        // --spc-fix-incw writes the correct 3A.
                        Need(m, ops, 1);
                        var d = SpcParse(ops[0]);
                        if (d.Kind != SpcKind.Dp) throw new AsmException("incw needs dp");
                        if (SpcFixIncw)
                            Emit(0x3A);
                        else
                        {
                            Warnings.Add($"{line}: incw encoded as AB (inc dp) like the original assembler; --spc-fix-incw for 3A\n    {line.Text.Trim()}");
                            Emit(0xAB);
                        }
                        Emit(d.Value);
                        return;
                    }
                case "pcall":
                    {
                        Need(m, ops, 1);
                        Emit(0x4F);
                        Emit(Eval(ops[0]));
                        return;
                    }
            }

            if (spcMemBitOps.TryGetValue(m, out byte mop))
            {
                SpcMemBit(m, mop, ops);
                return;
            }

            var parsed = new List<SpcOperand>();
            foreach (var o in ops) parsed.Add(SpcParse(o));
            string key = m + "|" + string.Join(",", parsed.ConvertAll(p => p.Kind.ToString()));
            if (!spcTable.TryGetValue(key, out var entry))
                throw new AsmException($"'{m} {a.Trim()}' isn't an SPC700 instruction ({string.Join(", ", parsed.ConvertAll(p => p.Kind.ToString()))})");

            Emit(entry.op);
            if (entry.rev)
                for (int i = parsed.Count - 1; i >= 0; i--) SpcEmitOperand(parsed[i]);
            else
                foreach (var p in parsed) SpcEmitOperand(p);
        }

        static void Need(string m, List<string> ops, int n)
        {
            if (ops.Count != n) throw new AsmException($"{m} needs {n} operand(s)");
        }

        void SpcEmitOperand(SpcOperand p)
        {
            switch (p.Kind)
            {
                case SpcKind.Imm:
                case SpcKind.Dp:
                case SpcKind.DpX:
                case SpcKind.DpY:
                case SpcKind.IndDpX:
                case SpcKind.IndDpY:
                    Emit(p.Value);
                    break;
                case SpcKind.Abs:
                case SpcKind.AbsX:
                case SpcKind.AbsY:
                case SpcKind.AbsIndX:
                    Emit(p.Value, 2);
                    break;
            }
        }

        // Writes the prefix bytes, then the branch offset to 'target'
        void SpcRelative(byte[] prefix, string target, long start)
        {
            long to = SpcTarget(target);
            long next = start + prefix.Length + 1;
            long rel = (to & 0xffff) - (next & 0xffff);
            if (!unresolved && (rel < -128 || rel > 127))
            {
                Warnings.Add($"{line}: branch out of range ({rel} bytes), written as 0\n    {line.Text.Trim()}");
                rel = 0;
            }
            foreach (byte b in prefix) Emit(b);
            Emit(rel);
        }

        long SpcTarget(string t)
        {
            t = t.Trim();
            if (t.StartsWith("$")) t = t.Substring(1).TrimStart();
            if (t.StartsWith("+")) return LookupForward(Name(t.Substring(1)));
            if (t.StartsWith("-") && t.Length > 1 && (char.IsLetter(t[1]) || t[1] == '_'))
                return LookupBackward(Name(t.Substring(1)));
            return Eval(t);
        }

        // "dp, bit, ..." or "dp.bit, ..."
        void SplitBit(List<string> ops, out long dp, out int bit, out List<string> rest)
        {
            if (ops.Count == 0) throw new AsmException("Missing dp and bit");
            string first = ops[0].Trim();
            int dot = first.LastIndexOf('.');
            int used;
            if (dot > 0 && ops.Count >= 1 && !first.StartsWith("!"))
            {
                dp = Eval(first.Substring(0, dot));
                bit = (int)Eval(first.Substring(dot + 1));
                used = 1;
            }
            else
            {
                if (ops.Count < 2) throw new AsmException("Missing bit number");
                dp = Eval(first);
                bit = (int)Eval(ops[1]);
                used = 2;
            }
            if (bit < 0 || bit > 7) throw new AsmException($"Bit {bit} out of range");
            CheckDp(dp);
            rest = ops.GetRange(used, ops.Count - used);
        }

        // and1 c, m.b / c, /m.b; mov1 c, m.b / m.b, c; not1 m.b ...
        void SpcMemBit(string m, byte op, List<string> ops)
        {
            string mb;
            if (m == "not1")
            {
                Need(m, ops, 1);
                mb = ops[0];
            }
            else
            {
                Need(m, ops, 2);
                bool cFirst = ops[0].Trim().Equals("c", StringComparison.OrdinalIgnoreCase);
                bool cSecond = ops[1].Trim().Equals("c", StringComparison.OrdinalIgnoreCase);
                if (cFirst) mb = ops[1];
                else if (cSecond && m == "mov1") { mb = ops[0]; op = 0xCA; }
                else throw new AsmException($"{m} needs c as an operand");
                mb = mb.Trim();
                if (mb.StartsWith("/"))
                {
                    if (m != "and1" && m != "or1") throw new AsmException($"{m} has no /m.b form");
                    mb = mb.Substring(1);
                    op = (byte)(op + 0x20);
                }
            }
            mb = mb.Trim();
            if (mb.StartsWith("!")) mb = mb.Substring(1);
            int dot = mb.LastIndexOf('.');
            if (dot <= 0) throw new AsmException($"Expected addr.bit, got '{mb}'");
            long addr = Eval(mb.Substring(0, dot));
            long bit = Eval(mb.Substring(dot + 1));
            if (bit < 0 || bit > 7) throw new AsmException($"Bit {bit} out of range");
            if (!unresolved && (addr < 0 || addr > 0x1fff)) throw new AsmException($"m.b address {addr:X} is above 0x1FFF");
            Emit(op);
            Emit((addr & 0x1fff) | bit << 13, 2);
        }

        void CheckDp(long v)
        {
            if (!unresolved && (v < 0 || v > 0xff))
                throw new AsmException($"Direct page address {v:X} is above 0xFF (use ! for absolute)");
        }

        static bool StripIndex(ref string s, char reg)
        {
            string t = s.TrimEnd();
            if (t.Length >= 2 && char.ToLowerInvariant(t[t.Length - 1]) == reg)
            {
                string b = t.Substring(0, t.Length - 1).TrimEnd();
                if (b.EndsWith("+"))
                {
                    s = b.Substring(0, b.Length - 1).TrimEnd();
                    return true;
                }
            }
            return false;
        }

        SpcOperand SpcParse(string text)
        {
            string t = text.Trim();
            switch (t.ToLowerInvariant())
            {
                case "a": return new SpcOperand { Kind = SpcKind.A };
                case "x": return new SpcOperand { Kind = SpcKind.X };
                case "y": return new SpcOperand { Kind = SpcKind.Y };
                case "ya": return new SpcOperand { Kind = SpcKind.YA };
                case "sp": return new SpcOperand { Kind = SpcKind.SP };
                case "psw": return new SpcOperand { Kind = SpcKind.PSW };
                case "c": return new SpcOperand { Kind = SpcKind.C };
                case "(x)": return new SpcOperand { Kind = SpcKind.IndX };
                case "(x)+": return new SpcOperand { Kind = SpcKind.IndXInc };
                case "(y)": return new SpcOperand { Kind = SpcKind.IndY };
            }

            if (t.StartsWith("#"))
            {
                long v = Eval(t.Substring(1));
                return new SpcOperand { Kind = SpcKind.Imm, Value = v & 0xff };
            }

            if (t.StartsWith("["))
            {
                string inner;
                if (StripIndex(ref t, 'y'))
                {
                    if (!t.EndsWith("]")) throw new AsmException($"Bad operand '{text}'");
                    inner = t.Substring(1, t.Length - 2);
                    long v = Eval(inner);
                    CheckDp(v);
                    return new SpcOperand { Kind = SpcKind.IndDpY, Value = v };
                }
                if (!t.EndsWith("]")) throw new AsmException($"Bad operand '{text}'");
                inner = t.Substring(1, t.Length - 2).Trim();
                if (!StripIndex(ref inner, 'x')) throw new AsmException($"Bad operand '{text}'");
                if (inner.StartsWith("!"))
                    return new SpcOperand { Kind = SpcKind.AbsIndX, Value = Eval(inner.Substring(1)) };
                long d = Eval(inner);
                CheckDp(d);
                return new SpcOperand { Kind = SpcKind.IndDpX, Value = d };
            }

            if (t.StartsWith("!"))
            {
                string e = t.Substring(1);
                if (StripIndex(ref e, 'x')) return new SpcOperand { Kind = SpcKind.AbsX, Value = Eval(e) };
                if (StripIndex(ref e, 'y')) return new SpcOperand { Kind = SpcKind.AbsY, Value = Eval(e) };
                return new SpcOperand { Kind = SpcKind.Abs, Value = Eval(e) };
            }

            {
                string e = t;
                var kind = SpcKind.Dp;
                if (StripIndex(ref e, 'x')) kind = SpcKind.DpX;
                else if (StripIndex(ref e, 'y')) kind = SpcKind.DpY;
                long v = Eval(e);
                CheckDp(v);
                return new SpcOperand { Kind = kind, Value = v };
            }
        }
    }
}
