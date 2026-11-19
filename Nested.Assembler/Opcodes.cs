using System.Collections.Generic;

namespace Nested.Assembler
{
    enum Mode
    {
        Imp, Acc, Imm,
        Dp, DpX, DpY, Abs, AbsX, AbsY, Long, LongX,
        Ind, IndX, IndY, IndLong, IndLongY, Sr, SrIndY,
        AbsInd, AbsIndX, AbsIndLong,
        Rel, RelLong, Block,
    }

    enum ImmKind { None, M, X, One, Two }

    // 65816 opcode table, mnemonic -> addressing mode -> opcode
    static class Opcodes
    {
        public static readonly Dictionary<string, Dictionary<Mode, byte>> Table = new Dictionary<string, Dictionary<Mode, byte>>();

        static readonly HashSet<string> branches = new HashSet<string> { "bcc", "bcs", "beq", "bmi", "bne", "bpl", "bra", "bvc", "bvs" };

        public static bool IsBranch(string m) => branches.Contains(m);
        public static bool IsLongRelative(string m) => m == "brl" || m == "per";

        public static ImmKind Immediate(string m)
        {
            switch (m)
            {
                case "adc": case "and": case "bit": case "cmp": case "eor": case "lda": case "ora": case "sbc":
                    return ImmKind.M;
                case "cpx": case "cpy": case "ldx": case "ldy":
                    return ImmKind.X;
                case "rep": case "sep": case "cop": case "brk": case "wdm":
                    return ImmKind.One;
                case "pea":
                    return ImmKind.Two;
            }
            return ImmKind.None;
        }

        static void Add(string m, Mode mode, int op)
        {
            if (!Table.TryGetValue(m, out var d))
                Table[m] = d = new Dictionary<Mode, byte>();
            d[mode] = (byte)op;
        }

        // adc and cmp eor lda ora sbc sta share one layout around a base opcode
        static void Alu(string m, int b, bool imm)
        {
            if (imm) Add(m, Mode.Imm, b + 0x09);
            Add(m, Mode.IndX, b + 0x01);
            Add(m, Mode.Sr, b + 0x03);
            Add(m, Mode.Dp, b + 0x05);
            Add(m, Mode.IndLong, b + 0x07);
            Add(m, Mode.Abs, b + 0x0D);
            Add(m, Mode.Long, b + 0x0F);
            Add(m, Mode.IndY, b + 0x11);
            Add(m, Mode.Ind, b + 0x12);
            Add(m, Mode.SrIndY, b + 0x13);
            Add(m, Mode.DpX, b + 0x15);
            Add(m, Mode.IndLongY, b + 0x17);
            Add(m, Mode.AbsY, b + 0x19);
            Add(m, Mode.AbsX, b + 0x1D);
            Add(m, Mode.LongX, b + 0x1F);
        }

        // asl rol lsr ror
        static void ShiftOp(string m, int b)
        {
            Add(m, Mode.Dp, b + 0x06);
            Add(m, Mode.Acc, b + 0x0A);
            Add(m, Mode.Abs, b + 0x0E);
            Add(m, Mode.DpX, b + 0x16);
            Add(m, Mode.AbsX, b + 0x1E);
        }

        static Opcodes()
        {
            Alu("ora", 0x00, true);
            Alu("and", 0x20, true);
            Alu("eor", 0x40, true);
            Alu("adc", 0x60, true);
            Alu("sta", 0x80, false);
            Alu("lda", 0xA0, true);
            Alu("cmp", 0xC0, true);
            Alu("sbc", 0xE0, true);

            ShiftOp("asl", 0x00);
            ShiftOp("rol", 0x20);
            ShiftOp("lsr", 0x40);
            ShiftOp("ror", 0x60);

            Add("inc", Mode.Acc, 0x1A); Add("inc", Mode.Dp, 0xE6); Add("inc", Mode.DpX, 0xF6); Add("inc", Mode.Abs, 0xEE); Add("inc", Mode.AbsX, 0xFE);
            Add("dec", Mode.Acc, 0x3A); Add("dec", Mode.Dp, 0xC6); Add("dec", Mode.DpX, 0xD6); Add("dec", Mode.Abs, 0xCE); Add("dec", Mode.AbsX, 0xDE);

            Add("bit", Mode.Imm, 0x89); Add("bit", Mode.Dp, 0x24); Add("bit", Mode.DpX, 0x34); Add("bit", Mode.Abs, 0x2C); Add("bit", Mode.AbsX, 0x3C);
            Add("cpx", Mode.Imm, 0xE0); Add("cpx", Mode.Dp, 0xE4); Add("cpx", Mode.Abs, 0xEC);
            Add("cpy", Mode.Imm, 0xC0); Add("cpy", Mode.Dp, 0xC4); Add("cpy", Mode.Abs, 0xCC);
            Add("ldx", Mode.Imm, 0xA2); Add("ldx", Mode.Dp, 0xA6); Add("ldx", Mode.DpY, 0xB6); Add("ldx", Mode.Abs, 0xAE); Add("ldx", Mode.AbsY, 0xBE);
            Add("ldy", Mode.Imm, 0xA0); Add("ldy", Mode.Dp, 0xA4); Add("ldy", Mode.DpX, 0xB4); Add("ldy", Mode.Abs, 0xAC); Add("ldy", Mode.AbsX, 0xBC);
            Add("stx", Mode.Dp, 0x86); Add("stx", Mode.DpY, 0x96); Add("stx", Mode.Abs, 0x8E);
            Add("sty", Mode.Dp, 0x84); Add("sty", Mode.DpX, 0x94); Add("sty", Mode.Abs, 0x8C);
            Add("stz", Mode.Dp, 0x64); Add("stz", Mode.DpX, 0x74); Add("stz", Mode.Abs, 0x9C); Add("stz", Mode.AbsX, 0x9E);
            Add("trb", Mode.Dp, 0x14); Add("trb", Mode.Abs, 0x1C);
            Add("tsb", Mode.Dp, 0x04); Add("tsb", Mode.Abs, 0x0C);

            Add("jmp", Mode.Abs, 0x4C); Add("jmp", Mode.Long, 0x5C); Add("jmp", Mode.AbsInd, 0x6C); Add("jmp", Mode.AbsIndX, 0x7C); Add("jmp", Mode.AbsIndLong, 0xDC);
            Add("jml", Mode.Long, 0x5C); Add("jml", Mode.AbsIndLong, 0xDC);
            Add("jsr", Mode.Abs, 0x20); Add("jsr", Mode.Long, 0x22); Add("jsr", Mode.AbsIndX, 0xFC);
            Add("jsl", Mode.Long, 0x22);

            Add("bpl", Mode.Rel, 0x10); Add("bmi", Mode.Rel, 0x30); Add("bvc", Mode.Rel, 0x50); Add("bvs", Mode.Rel, 0x70);
            Add("bra", Mode.Rel, 0x80); Add("bcc", Mode.Rel, 0x90); Add("bcs", Mode.Rel, 0xB0); Add("bne", Mode.Rel, 0xD0); Add("beq", Mode.Rel, 0xF0);
            Add("brl", Mode.RelLong, 0x82);
            Add("per", Mode.RelLong, 0x62);

            Add("pea", Mode.Abs, 0xF4); Add("pea", Mode.Imm, 0xF4);
            Add("pei", Mode.Ind, 0xD4);

            Add("rep", Mode.Imm, 0xC2);
            Add("sep", Mode.Imm, 0xE2);
            Add("cop", Mode.Imm, 0x02);
            Add("brk", Mode.Imm, 0x00); Add("brk", Mode.Imp, 0x00);
            Add("wdm", Mode.Imm, 0x42);
            Add("mvn", Mode.Block, 0x54);
            Add("mvp", Mode.Block, 0x44);

            var implied = new (string, int)[]
            {
                ("clc", 0x18), ("cld", 0xD8), ("cli", 0x58), ("clv", 0xB8), ("sec", 0x38), ("sed", 0xF8), ("sei", 0x78),
                ("dex", 0xCA), ("dey", 0x88), ("inx", 0xE8), ("iny", 0xC8), ("nop", 0xEA),
                ("pha", 0x48), ("phb", 0x8B), ("phd", 0x0B), ("phk", 0x4B), ("php", 0x08), ("phx", 0xDA), ("phy", 0x5A),
                ("pla", 0x68), ("plb", 0xAB), ("pld", 0x2B), ("plp", 0x28), ("plx", 0xFA), ("ply", 0x7A),
                ("rti", 0x40), ("rtl", 0x6B), ("rts", 0x60), ("stp", 0xDB), ("wai", 0xCB), ("xba", 0xEB), ("xce", 0xFB),
                ("tax", 0xAA), ("tay", 0xA8), ("tcd", 0x5B), ("tcs", 0x1B), ("tdc", 0x7B), ("tsc", 0x3B), ("tsx", 0xBA),
                ("txa", 0x8A), ("txs", 0x9A), ("txy", 0x9B), ("tya", 0x98), ("tyx", 0xBB),
            };
            foreach (var (m, op) in implied)
                Add(m, Mode.Imp, op);
        }
    }
}
