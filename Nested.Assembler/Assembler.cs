using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Nested.Assembler
{
    sealed class SourceLine
    {
        public int Id;          // 1-based index into Assembler.AllLines
        public string File;     // path relative to BaseDir, for messages
        public int Number;
        public string Text;
        public override string ToString() => $"{File}:{Number}";
    }

    // Step 1: lexer, expressions, 65816 instructions with explicit operand sizes,
    // labels, near labels, .def .addr .pushaddr/.pulladdr .align/.misalign
    // .data* .db .fill* .string/.string0 .printdate .mx .finalsize .outfile
    // .include, [addr] = value, smx/cmx/rmx.
    // Step 2: .macro/.endm (nested), {n} substitution, macro calls, .repeat.
    // Step 4: .vstack .vstacktop .func .local .unlocal .precall call return.
    // Step 5: .if .ifnamecontains .false .else Game { } .nowrite .fix .addrlow.
    // Step 6: .palette .image (1 bpp, byte).
    // Step 7: SPC700 (.includeSPC, Spc700.cs) .offsetfrom .offsetend.
    // The v1.8 tree assembles byte-identical to the template (README).
    sealed partial class Assembler
    {
        const int MaxPasses = 16;

        public string BaseDir;
        public DateTime BuildDate = DateTime.Today;
        public readonly List<SourceLine> AllLines = new List<SourceLine>();
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();
        public Rom Rom = new Rom();
        public long FinalSize = -1;
        public string OutFile;
        public int PassCount;
        // A Game line matched "Empty": the output starts from the Empty base image (Program.cs)
        public bool EmptyGame;
        // SPC700 incw: false = AB like the original assembler (v1.8 template), true = correct 3A
        public bool SpcFixIncw;

        readonly Dictionary<string, SourceLine[]> fileCache = new Dictionary<string, SourceLine[]>(StringComparer.OrdinalIgnoreCase);

        struct Def
        {
            public int Seq;
            public long Value;
            public Def(int seq, long value) { Seq = seq; Value = value; }
        }

        // Every definition of a symbol in statement order. Symbols can be redefined
        // (temp__, this__, b_1 ...): a plain reference takes the latest definition
        // so far; a name not defined yet in this pass takes the final value of the
        // previous pass; $+name takes the next definition from the previous pass.
        Dictionary<string, List<Def>> cur, prev;

        int seq;            // statement counter, identical between passes
        long pc;            // SNES address
        long limit;         // inclusive end from .addr, -1 = none
        int mx;             // .mx flags (0x20 = 8-bit A, 0x10 = 8-bit X/Y)
        bool unresolved;
        bool reportUnresolved;
        Stack<(long pc, long limit)> addrStack;
        SourceLine line;

        // Macros: own namespace (a label and a macro can share a name), case-insensitive,
        // rebuilt each pass. A macro not defined yet in this pass comes from the previous
        // one (Gui.asm uses Gui__Color long before defining it).
        sealed class Macro
        {
            public string Name;
            public SourceLine Definition;
            public readonly List<(SourceLine origin, string text)> Body = new List<(SourceLine, string)>();
        }

        const int MaxMacroDepth = 64;
        Dictionary<string, Macro> macros, prevMacros;
        Macro recording;            // macro whose body is being collected
        int recordDepth;            // .macro/.endm nesting inside it
        readonly List<string> callChain = new List<string>();   // "file:line (Name)" per active expansion

        // Vstack: every local variable gets a static address. A callee's frame starts
        // after everything its caller allocated up to the call line, freed locals
        // included (highest over all call sites, from the previous pass); its
        // parameters come first, then its locals.
        public sealed class Func
        {
            public string Name;
            public SourceLine Declaration;
            public readonly List<(string name, int size)> Params = new List<(string, int)>();
            public long Base;
            public long HighWater;      // highest address reached by params + locals
        }

        public sealed class CallSite
        {
            public long Bank;           // bank of the call instruction
            public long Mark;           // caller's allocated total at the call line
            public SourceLine Line;
        }

        Dictionary<string, Func> funcs, prevFuncs;                     // by name, per pass
        Dictionary<string, List<CallSite>> calls, prevCalls;            // callee name -> call sites
        Func curFunc;               // function being assembled (null after a bare .vstack)
        long vstackBase;            // last .vstack value: base of the current frame without .func
        long vstackTop;             // .vstacktop, -1 = none
        long localTop, highWater;   // current context
        long allocated;             // base + every local/param since .func/.vstack; .unlocal doesn't lower it
        List<(string name, int size)> localStack;
        string callTarget;          // last .precall or named call target, for an operand-less call

        // Blocks: a condition (.if .ifnamecontains .false Game) opens the { } block on
        // the next line, .else can follow with a second one. A false block is skipped
        // by counting braces only, so nothing inside it (macros, .func) is defined.
        bool? pending;              // condition waiting for its '{' (null = none)
        bool pendingOrig;           // the condition itself (for .else: the one it inverts)
        bool pendingIsElse;
        bool? lastCond;             // condition of the block just closed, for .else
        Stack<(bool orig, bool isElse, SourceLine open)> blocks;
        int skipDepth;              // > 0 while skipping a false block
        bool skipOrig, skipIsElse;
        SourceLine skipOpen;
        bool noWrite;               // .nowrite: ROM writes dropped for the rest of the pass
        string fixName;             // .fix "name", no output effect seen

        // SPC700 (.includeSPC) and .offsetfrom
        bool spcMode;               // mnemonics are SPC700 (Spc700.cs)
        bool inOffset;
        long offsetDelta;           // ROM address - logical PC inside .offsetfrom

        // .palette / .image
        List<int> palette;          // RGB888, index = colour number in the converted image
        readonly Dictionary<string, PngImage> imageCache = new Dictionary<string, PngImage>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, Func> Functions => funcs;
        public IReadOnlyDictionary<string, List<CallSite>> CallSites => calls;

        // ---------------------------------------------------------------
        // Passes

        public bool Assemble(string rootFile)
        {
            string root = Path.GetFullPath(rootFile);
            for (int pass = 1; ; pass++)
            {
                PassCount = pass;
                RunPass(root);
                bool stable = prev != null && SameDefs(prev, cur) && SameCalls(prevCalls, calls);
                if (stable || pass >= MaxPasses)
                {
                    if (unresolved)
                    {
                        // Symbols that never got defined: one more pass that names them
                        prev = cur;
                        prevFuncs = funcs;
                        prevCalls = calls;
                        prevMacros = macros;
                        reportUnresolved = true;
                        RunPass(root);
                    }
                    else if (!stable)
                        Errors.Add($"Symbol values still changing after {MaxPasses} passes");
                    break;
                }
                prev = cur;
                prevFuncs = funcs;
                prevCalls = calls;
                prevMacros = macros;
            }
            return Errors.Count == 0;
        }

        void RunPass(string root)
        {
            // Names are case-insensitive (Interpret__IndirectYIO_Sta is referenced as _sta)
            cur = new Dictionary<string, List<Def>>(StringComparer.OrdinalIgnoreCase);
            Errors = new List<string>();
            Warnings = new List<string>();
            Rom = new Rom();
            addrStack = new Stack<(long, long)>();
            seq = 0;
            pc = 0;
            limit = -1;
            mx = 0x30;
            unresolved = false;
            FinalSize = -1;
            OutFile = null;
            macros = new Dictionary<string, Macro>(StringComparer.OrdinalIgnoreCase);
            recording = null;
            recordDepth = 0;
            callChain.Clear();
            funcs = new Dictionary<string, Func>(StringComparer.OrdinalIgnoreCase);
            calls = new Dictionary<string, List<CallSite>>(StringComparer.OrdinalIgnoreCase);
            curFunc = null;
            vstackBase = 0;
            vstackTop = -1;
            localTop = highWater = allocated = 0;
            localStack = new List<(string, int)>();
            callTarget = null;
            pending = null;
            lastCond = null;
            blocks = new Stack<(bool, bool, SourceLine)>();
            skipDepth = 0;
            noWrite = false;
            fixName = null;
            palette = null;
            EmptyGame = false;
            spcMode = false;
            inOffset = false;
            offsetDelta = 0;
            ProcessFile(root);
            if (recording != null)
                Errors.Add($"{recording.Definition}: .macro {recording.Name} has no .endm");
            if (skipDepth > 0)
                Errors.Add($"{skipOpen}: block has no closing '}}'");
            if (inOffset)
                Errors.Add(".offsetfrom without .offsetend");
            foreach (var b in blocks)
                Errors.Add($"{b.open}: block has no closing '}}'");
        }

        static bool SameDefs(Dictionary<string, List<Def>> a, Dictionary<string, List<Def>> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kv in a)
            {
                if (!b.TryGetValue(kv.Key, out var other) || other.Count != kv.Value.Count) return false;
                for (int i = 0; i < other.Count; i++)
                    if (other[i].Value != kv.Value[i].Value || other[i].Seq != kv.Value[i].Seq) return false;
            }
            return true;
        }

        // Call sites decide jsr/jsl and callee bases in the next pass, so they must settle too
        static bool SameCalls(Dictionary<string, List<CallSite>> a, Dictionary<string, List<CallSite>> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            foreach (var kv in a)
            {
                if (!b.TryGetValue(kv.Key, out var other) || other.Count != kv.Value.Count) return false;
                for (int i = 0; i < other.Count; i++)
                    if (other[i].Bank != kv.Value[i].Bank || other[i].Mark != kv.Value[i].Mark) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------
        // Files and lines

        SourceLine[] Load(string path)
        {
            if (fileCache.TryGetValue(path, out var lines)) return lines;

            string shown = path;
            if (BaseDir != null)
            {
                string rel = Path.GetRelativePath(BaseDir, path);
                if (!rel.StartsWith("..")) shown = rel;
            }

            string[] text = File.ReadAllLines(path, Encoding.UTF8);
            lines = new SourceLine[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                lines[i] = new SourceLine { Id = AllLines.Count + 1, File = shown, Number = i + 1, Text = text[i] };
                AllLines.Add(lines[i]);
            }
            fileCache[path] = lines;
            return lines;
        }

        void ProcessFile(string path)
        {
            var saved = line;
            foreach (var l in Load(path))
                ProcessLine(l);
            line = saved;
        }

        string ResolveInclude(string name)
        {
            var tried = new List<string>();
            void Try(string dir)
            {
                if (dir != null) tried.Add(Path.GetFullPath(Path.Combine(dir, name)));
            }
            Try(BaseDir);
            string from = Path.GetDirectoryName(Path.Combine(BaseDir ?? "", line.File));
            Try(from);
            Try(Path.GetDirectoryName(from));
            foreach (var t in tried)
                if (File.Exists(t)) return t;
            throw new AsmException($"Include '{name}' not found");
        }

        void ProcessLine(SourceLine l) => Process(l, l.Text);

        // One statement. 'origin' is the source line it came from (for macro
        // expansions the body line), 'text' the text after substitution.
        void Process(SourceLine origin, string text)
        {
            line = origin;
            seq++;
            string t = StripComment(text).Trim();
            if (t.Length == 0) return;

            // Skipping a false block: only braces count (before recording, so a
            // skipped .macro is never defined)
            if (skipDepth > 0)
            {
                if (t == "{") skipDepth++;
                else if (t == "}" && --skipDepth == 0)
                    lastCond = skipIsElse ? (bool?)null : skipOrig;
                return;
            }

            if (recording != null)
            {
                Record(t);
                return;
            }

            try
            {
                Statement(t);
            }
            catch (AsmException e)
            {
                var msg = new StringBuilder($"{origin}: {e.Message}\n    {t}");
                for (int i = callChain.Count - 1; i >= 0; i--)
                    msg.Append($"\n    called from {callChain[i]}");
                Errors.Add(msg.ToString());
            }
        }

        static string StripComment(string s)
        {
            bool quote = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '"') quote = !quote;
                else if (!quote && s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
                    return s.Substring(0, i);
            }
            return s;
        }

        static void SplitFirst(string t, out string first, out string rest)
        {
            int i = 0;
            while (i < t.Length && !char.IsWhiteSpace(t[i])) i++;
            first = t.Substring(0, i);
            rest = t.Substring(i).Trim();
        }

        // ---------------------------------------------------------------
        // Statements

        void Statement(string t)
        {
            if (t == "{")
            {
                OpenBlock();
                return;
            }
            if (t == "}")
            {
                CloseBlock();
                return;
            }

            SplitFirst(t, out string first, out string rest);
            if (first.Equals(".else", StringComparison.OrdinalIgnoreCase))
            {
                Else(rest);
                return;
            }

            // Anything else ends a waiting condition (a Game line without a block)
            pending = null;
            lastCond = null;

            if (t[0] == '[')
            {
                Patch(t);
                return;
            }

            if (first.Length > 1 && first.EndsWith(":"))
            {
                Define(first.Substring(0, first.Length - 1), pc);
                if (rest.Length == 0) return;
                SplitFirst(rest, out first, out rest);
            }

            string name = first.ToLowerInvariant();
            if (name[0] == '.')
            {
                Directive(name, rest);
                return;
            }

            // .includeSPC: SPC700 mnemonics, then macros; no 65816 instructions
            if (spcMode)
            {
                if (IsSpcMnemonic(name))
                {
                    SpcInstruction(name, rest);
                    return;
                }
                if (macros.TryGetValue(first, out var spcMacro)
                    || (prevMacros != null && prevMacros.TryGetValue(first, out spcMacro)))
                {
                    Expand(spcMacro, rest);
                    return;
                }
                throw new AsmException($"'{first}' is neither an SPC700 instruction nor a defined macro");
            }

            switch (name)
            {
                case "smx": Smx(rest); return;
                case "cmx":
                case "rmx": Rmx(rest); return;
                case "call": Call(rest); return;
                case "return": Return(rest); return;
                case "game": Game(rest); return;
            }

            if (Opcodes.Table.ContainsKey(name))
            {
                Instruction(name, rest);
                return;
            }

            if (macros.TryGetValue(first, out var macro)
                || (prevMacros != null && prevMacros.TryGetValue(first, out macro)))
            {
                Expand(macro, rest);
                return;
            }

            throw new AsmException($"'{first}' is neither an instruction nor a defined macro");
        }

        // ---------------------------------------------------------------
        // Blocks and conditions

        void SetCondition(bool cond)
        {
            pending = cond;
            pendingOrig = cond;
            pendingIsElse = false;
        }

        void OpenBlock()
        {
            if (pending == null) throw new AsmException("'{' without a condition (.if .ifnamecontains .false Game .else)");
            bool active = pending.Value;
            pending = null;
            lastCond = null;
            if (active)
                blocks.Push((pendingOrig, pendingIsElse, line));
            else
            {
                skipDepth = 1;
                skipOrig = pendingOrig;
                skipIsElse = pendingIsElse;
                skipOpen = line;
            }
        }

        void CloseBlock()
        {
            pending = null;
            if (blocks.Count == 0) throw new AsmException("'}' without an open block");
            var b = blocks.Pop();
            lastCond = b.isElse ? (bool?)null : b.orig;
        }

        // .else: right after a condition without a block (Game ... / .else in
        // Includes.asm) or after the condition's closing '}'
        void Else(string rest)
        {
            if (rest.Length != 0) throw new AsmException(".else takes no operand");
            bool cond;
            if (pending != null && !pendingIsElse) cond = pendingOrig;
            else if (lastCond != null) cond = lastCond.Value;
            else
            {
                pending = null;
                throw new AsmException(".else without a condition before it");
            }
            pending = !cond;
            pendingOrig = cond;
            pendingIsElse = true;
            lastCond = null;
        }

        // Game "title", a, b: the original assembler can patch an existing ROM.
        // Nested builds without one, which is the game "Empty" with 0, 0.
        void Game(string rest)
        {
            SetCondition(false);    // an error below still leaves a condition for the '{'
            var args = SplitTop(rest);
            if (args.Count != 3) throw new AsmException("Game needs a title and two numbers");
            string title = QuotedString(args[0]);
            long a = Value(args[1]);
            long b = Value(args[2]);
            bool match = title.PadRight(21) == "Empty".PadRight(21) && a == 0 && b == 0;
            if (match) EmptyGame = true;
            SetCondition(match);
        }

        static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return s.Substring(1, s.Length - 2);
            return s;
        }

        // ---------------------------------------------------------------
        // Macros

        // Collects body lines; nested .macro/.endm pairs (CoreCall_DEFINE defines
        // macros) stay in the body and are only defined when the outer one expands
        void Record(string t)
        {
            SplitFirst(t, out string first, out _);
            string d = first.ToLowerInvariant();
            if (d == ".macro") recordDepth++;
            else if (d == ".endm" && --recordDepth == 0)
            {
                macros[recording.Name] = recording;
                recording = null;
                return;
            }
            recording.Body.Add((line, t));
        }

        void Expand(Macro m, string argText)
        {
            if (callChain.Count >= MaxMacroDepth)
                throw new AsmException($"Macros nested deeper than {MaxMacroDepth} (recursion?)");

            var args = MacroArgs(argText);
            var call = line;
            // Substitute everything first: a missing argument is reported at the call
            var lines = new List<(SourceLine, string)>(m.Body.Count);
            foreach (var (origin, text) in m.Body)
                lines.Add((origin, Substitute(text, args, m.Name)));

            callChain.Add($"{call} ({m.Name})");
            try
            {
                foreach (var (origin, text) in lines)
                    Process(origin, text);
            }
            finally
            {
                callChain.RemoveAt(callChain.Count - 1);
                line = call;
            }
        }

        // Replaces {n} and {n:format} with argument n, inserted verbatim (String.Format
        // semantics: braces inside an argument aren't substituted again). Any other
        // brace stays, since bodies contain { } blocks.
        static string Substitute(string text, List<string> args, string macroName)
        {
            if (text.IndexOf('{') < 0) return text;
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '{')
                {
                    int j = i + 1;
                    while (j < text.Length && char.IsDigit(text[j])) j++;
                    if (j > i + 1)
                    {
                        int k = j;
                        if (k < text.Length && text[k] == ':')
                            while (k < text.Length && text[k] != '}') k++;
                        if (k < text.Length && text[k] == '}')
                        {
                            int n = int.Parse(text.Substring(i + 1, j - i - 1));
                            if (n >= args.Count)
                                throw new AsmException($"Macro {macroName} uses {{{n}}} but got {args.Count} argument(s)");
                            sb.Append(args[n]);
                            i = k + 1;
                            continue;
                        }
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        // Arguments split at top-level commas; without any, at whitespace
        // ("CoreCall_DEFINE CoreCall_Call ".data16 {0}""). Outer quotes are removed.
        static List<string> MacroArgs(string a)
        {
            a = a.Trim();
            var list = new List<string>();
            if (a.Length == 0) return list;

            bool hasComma = false;
            {
                int depth = 0;
                bool quote = false;
                foreach (char c in a)
                {
                    if (c == '"') quote = !quote;
                    else if (quote) continue;
                    else if (c == '(' || c == '[') depth++;
                    else if (c == ')' || c == ']') depth--;
                    else if (c == ',' && depth == 0) { hasComma = true; break; }
                }
            }

            if (hasComma)
                list.AddRange(SplitTop(a));
            else
            {
                int i = 0;
                while (i < a.Length)
                {
                    while (i < a.Length && char.IsWhiteSpace(a[i])) i++;
                    if (i >= a.Length) break;
                    int start = i;
                    bool quote = false;
                    while (i < a.Length && (quote || !char.IsWhiteSpace(a[i])))
                    {
                        if (a[i] == '"') quote = !quote;
                        i++;
                    }
                    list.Add(a.Substring(start, i - start));
                }
            }

            for (int n = 0; n < list.Count; n++)
            {
                string s = list[n].Trim();
                if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                    s = s.Substring(1, s.Length - 2);
                list[n] = s;
            }
            return list;
        }

        // [80ffd5] = 0x31: writes one byte into the ROM without moving the PC.
        // A bare digit string inside the brackets is hex.
        void Patch(string t)
        {
            int close = t.IndexOf(']');
            if (close < 0) throw new AsmException("Missing ']'");
            string at = t.Substring(1, close - 1).Trim();
            string rest = t.Substring(close + 1).Trim();
            if (!rest.StartsWith("=")) throw new AsmException("Expected '=' after ']'");

            long address = Expr.IsHex(at) ? Convert.ToInt64(at, 16) : Value(at);
            long v = Value(rest.Substring(1));
            long offset = Rom.Map(address & 0xffffff);
            if (offset < 0) throw new AsmException($"Patch address {address:X6} isn't ROM");
            if (!noWrite) Rom.Write(offset, (byte)v, line.Id);
        }

        // ---------------------------------------------------------------
        // Symbols

        void Define(string name, long value)
        {
            if (!Expr.IsIdentifier(name)) throw new AsmException($"Bad symbol name '{name}'");
            if (!cur.TryGetValue(name, out var list))
                cur[name] = list = new List<Def>();
            list.Add(new Def(seq, value));
        }

        long Lookup(string name)
        {
            if (cur.TryGetValue(name, out var list) && list.Count > 0)
                return list[list.Count - 1].Value;
            // Not defined yet in this pass: final value of the previous pass
            // (Header.asm's exceptions use ExceptionData_WritePointer before Gui.asm sets it)
            if (prev != null && prev.TryGetValue(name, out var old) && old.Count > 0)
                return old[old.Count - 1].Value;
            return Unresolved(name);
        }

        long LookupBackward(string name)
        {
            if (cur.TryGetValue(name, out var list) && list.Count > 0)
                return list[list.Count - 1].Value;
            // No earlier definition: like a plain reference (previous pass). Array.asm:1249
            // "bcs $-Array__DeleteDuplicates_loop3_loopEnd", defined only at 1296 -> B0 3B
            // forward at 0x11D17 in the template.
            Warnings.Add($"{line}: $-{name} has no earlier definition, using the later one\n    {line.Text.Trim()}");
            return Lookup(name);
        }

        long LookupForward(string name)
        {
            if (prev != null && prev.TryGetValue(name, out var old))
                foreach (var d in old)
                    if (d.Seq > seq) return d.Value;
            if (prev != null)
                throw new AsmException($"No later definition of '{name}' for $+{name}");
            return Unresolved(name);    // pass 1: not known yet
        }

        long Unresolved(string name)
        {
            unresolved = true;
            if (reportUnresolved) throw new AsmException($"Undefined symbol '{name}'");
            return 0;
        }

        long Eval(string text) => Expr.Eval(text.Trim(), Lookup);

        // Directive values may carry a size prefix (.data16 _Label, .def x =Label);
        // here it only marks the size, ^ takes the bank byte
        long Value(string text)
        {
            text = text.Trim();
            if (text.Length > 1)
            {
                switch (text[0])
                {
                    case '.':
                    case '_':
                    case '=':
                        return Eval(text.Substring(1));
                    case '^':
                        return Eval(text.Substring(1)) >> 16;
                }
            }
            return Eval(text);
        }

        static string Name(string text)
        {
            text = text.Trim();
            if (!Expr.IsIdentifier(text)) throw new AsmException($"Expected a label name, got '{text}'");
            return text;
        }

        // ---------------------------------------------------------------
        // Output

        void Emit(long b)
        {
            // Inside .offsetfrom the PC is logical (SPC addresses); the ROM position is PC + delta
            long physical = (pc + offsetDelta) & 0xffffff;
            if (limit >= 0 && physical > limit)
                throw new AsmException($"Writing at {physical:X6}, past the .addr end {limit:X6}");
            long offset = Rom.Map(physical);
            if (offset >= 0 && !noWrite) Rom.Write(offset, (byte)b, line.Id);
            pc = (pc + 1) & 0xffffff;
        }

        void Emit(long value, int bytes)
        {
            for (int i = 0; i < bytes; i++)
                Emit(value >> (8 * i));
        }

        // ---------------------------------------------------------------
        // Directives

        void Directive(string d, string a)
        {
            switch (d)
            {
                case ".def":
                    {
                        int i = 0;
                        while (i < a.Length && !char.IsWhiteSpace(a[i]) && a[i] != ',') i++;
                        string name = a.Substring(0, i);
                        string value = a.Substring(i).Trim().TrimStart(',').Trim();
                        if (value.Length == 0) throw new AsmException(".def without a value");
                        // The value is one word; text after it is a note
                        // (".def IO_2002_LastReturn 0x4315 16-bit" in Variables.asm)
                        int end = 0;
                        while (end < value.Length && !char.IsWhiteSpace(value[end])) end++;
                        value = value.Substring(0, end);
                        Define(name, Value(value));
                        return;
                    }

                case ".addr":
                    {
                        var args = Args(a, 2);
                        pc = Value(args[0]) & 0xffffff;
                        limit = args.Count > 1 ? Value(args[1]) & 0xffffff : -1;
                        return;
                    }

                case ".pushaddr":
                    addrStack.Push((pc, limit));
                    return;

                case ".pulladdr":
                    if (addrStack.Count == 0) throw new AsmException(".pulladdr without .pushaddr");
                    (pc, limit) = addrStack.Pop();
                    return;

                case ".align":
                case ".misalign":
                    {
                        var args = Args(a, 2);
                        long div = Value(args[0]);
                        long rem = args.Count > 1 ? Value(args[1]) : 0;
                        if (div <= 0) throw new AsmException($"{d} needs a positive dividend");
                        // Bank is ignored; skipped bytes are zero.
                        // TODO verify .misalign: assumed "step off the remainder by one byte"
                        if (d == ".align")
                            while ((pc & 0xffff) % div != rem) Emit(0);
                        else
                            while ((pc & 0xffff) % div == rem) Emit(0);
                        return;
                    }

                case ".db":
                case ".data8": Data(a, 1); return;
                case ".data16": Data(a, 2); return;
                // .dw = 16-bit (Spc700/tritabl3.asm, 2047x: SPC code size 0x2FBB in Sound.asm)
                case ".dw": Data(a, 2); return;
                case ".data24": Data(a, 3); return;
                case ".data32": Data(a, 4); return;

                case ".fill": Fill(a, 1); return;
                case ".fill16": Fill(a, 2); return;
                case ".fill24": Fill(a, 3); return;
                case ".fill32": Fill(a, 4); return;

                case ".string": EmitString(QuotedString(a), false); return;
                case ".string0": EmitString(QuotedString(a), true); return;

                case ".printdate":
                    EmitString(BuildDate.ToString(QuotedString(a), CultureInfo.InvariantCulture), false);
                    return;

                case ".mx":
                    mx = (int)Value(a) & 0x30;
                    return;

                case ".finalsize":
                    FinalSize = Value(a);
                    return;

                case ".outfile":
                    OutFile = QuotedString(a);
                    return;

                case ".include":
                    ProcessFile(ResolveInclude(QuotedString(a)));
                    return;

                case ".macro":
                    {
                        SplitFirst(a, out string name, out _);     // parameter names are documentation only
                        if (name.Length == 0) throw new AsmException(".macro without a name");
                        recording = new Macro { Name = name, Definition = line };
                        recordDepth = 1;
                        return;
                    }

                case ".endm":
                    throw new AsmException(".endm without .macro");

                case ".repeat":
                    {
                        // .repeat 0x100, "LUT8_Maker {0}": runs the statement N times.
                        // Quotes are optional: Mapper69.asm:133 ".repeat 8, Mapper69__a000_Switch_RepeatMac"
                        // -> the 32-byte table 8 times at 0x1E508 in the template.
                        var args = SplitTop(a);
                        if (args.Count != 2) throw new AsmException(".repeat needs a count and a statement");
                        long count = Value(args[0]);
                        string statement = Unquote(args[1]);
                        if (statement.Trim().Length == 0) throw new AsmException(".repeat with an empty statement");
                        if (count < 0 || count > 0x100000) throw new AsmException($"Bad .repeat count {count}");
                        var origin = line;
                        for (long i = 0; i < count; i++)
                            Process(origin, statement);
                        line = origin;
                        return;
                    }

                case ".vstack": VstackDirective(a); return;
                case ".vstacktop": vstackTop = Value(a); return;
                case ".func": FuncDirective(a); return;
                case ".local": LocalDirective(a); return;
                case ".unlocal": UnlocalDirective(a); return;
                case ".precall": PrecallDirective(a); return;

                case ".if":
                    SetCondition(false);
                    SetCondition(Eval(a) != 0);
                    return;

                case ".ifnamecontains":
                    {
                        // Case-sensitive substring test, quotes optional
                        SetCondition(false);
                        var args = SplitTop(a);
                        if (args.Count != 2) throw new AsmException(".ifnamecontains needs a name and a text");
                        SetCondition(Unquote(args[0]).Contains(Unquote(args[1]), StringComparison.Ordinal));
                        return;
                    }

                case ".false":
                    if (a.Length != 0) throw new AsmException(".false takes no operand");
                    SetCondition(false);
                    return;

                case ".nowrite":
                    noWrite = true;
                    return;

                case ".fix":
                    fixName = QuotedString(a);
                    return;

                case ".includespc":
                    {
                        // The file (and its .includes) in SPC700 mode
                        bool saved = spcMode;
                        spcMode = true;
                        try { ProcessFile(ResolveInclude(QuotedString(a))); }
                        finally { spcMode = saved; }
                        return;
                    }

                case ".offsetfrom":
                    {
                        // Labels and .addr use logical addresses from here (SPC code at 0x0400),
                        // bytes still go to the current ROM position
                        if (inOffset) throw new AsmException(".offsetfrom inside .offsetfrom");
                        long logical = Value(a) & 0xffffff;
                        offsetDelta = pc - logical;
                        pc = logical;
                        inOffset = true;
                        return;
                    }

                case ".offsetend":
                    if (!inOffset) throw new AsmException(".offsetend without .offsetfrom");
                    pc = (pc + offsetDelta) & 0xffffff;
                    offsetDelta = 0;
                    inOffset = false;
                    return;

                case ".palette":
                    palette = new List<int>();
                    foreach (var item in SplitTop(a))
                        palette.Add((int)(Value(item) & 0xffffff));
                    return;

                case ".image": ImageDirective(a); return;

                case ".addrlow":
                    // Low 16 bits of the address only; bank and .addr limit stay
                    // (terminators at the end of the Exception/EmuCalls/SelfMod lists)
                    pc = (pc & 0xff0000) | (Value(a) & 0xffff);
                    return;
            }

            throw new AsmException($"Directive '{d}' isn't implemented yet");
        }

        // .image "file", bpp, format: each pixel becomes its .palette index; 8x8 tiles
        // row by row. "byte" with 1 bpp: 8 bytes per tile, one per pixel row, leftmost
        // pixel in the MSB, bit 0 of the index (Gui.asm's font at C0:40E6 in the template).
        // Other bpp/formats aren't used by v1.8, so their layout isn't known.
        void ImageDirective(string a)
        {
            var args = SplitTop(a);
            if (args.Count != 3) throw new AsmException(".image needs a file, a bpp and a format");
            if (palette == null) throw new AsmException(".image needs a .palette before it");
            string path = ResolveInclude(QuotedString(args[0]));
            long bpp = Value(args[1]);
            string format = Unquote(args[2]).ToLowerInvariant();
            if (bpp != 1 || format != "byte")
                throw new AsmException($".image with {bpp} bpp and format '{format}' isn't supported (v1.8 only uses 1, byte)");

            if (!imageCache.TryGetValue(path, out var img))
                imageCache[path] = img = PngImage.Load(path);
            if (img.Width % 8 != 0 || img.Height % 8 != 0)
                throw new AsmException($"Image is {img.Width}x{img.Height}, not a multiple of 8x8");

            var index = new Dictionary<int, int>();
            for (int i = palette.Count - 1; i >= 0; i--)
                index[palette[i]] = i;      // first occurrence wins

            for (int ty = 0; ty < img.Height / 8; ty++)
                for (int tx = 0; tx < img.Width / 8; tx++)
                    for (int y = 0; y < 8; y++)
                    {
                        int b = 0;
                        for (int x = 0; x < 8; x++)
                        {
                            int px = tx * 8 + x, py = ty * 8 + y;
                            if (!index.TryGetValue(img[px, py], out int c))
                                throw new AsmException($"Pixel {px},{py} has colour {img[px, py]:X6}, which isn't in the .palette");
                            b = b << 1 | (c & 1);
                        }
                        Emit(b);
                    }
        }

        void Data(string a, int size)
        {
            foreach (var item in DataItems(a))
            {
                if (item.Length == 0) continue;
                // "+name" / "-name": distance from the byte after the value to the near label,
                // optionally followed by more expression. "+5" / "-1" stay numbers.
                //  CoreCall_CopyUpTo +b_1 -> ".data8 +b_1": 1C 03 8E 11 0A 02 at 0x1034A
                //  SelfMod_QuickCopy -> ".data8 -temp__-1": -5 - 1 = FA at 0x206A9
                if (item.Length > 1 && (item[0] == '+' || item[0] == '-')
                    && (char.IsLetter(item[1]) || item[1] == '_'))
                {
                    int end = 1;
                    while (end < item.Length && (char.IsLetterOrDigit(item[end]) || item[end] == '_')) end++;
                    string name = Name(item.Substring(1, end - 1));
                    string rest = item.Substring(end).Trim();
                    long target = item[0] == '+' ? LookupForward(name) : LookupBackward(name);
                    long d = (target & 0xffffff) - ((pc + size) & 0xffffff);
                    if (rest.Length > 0) d += Eval("0" + rest);
                    long min = -(1L << (8 * size - 1)), max = (1L << (8 * size)) - 1;
                    if (!unresolved && (d < min || d > max))
                        Warnings.Add($"{line}: distance to {name} ({d}) doesn't fit in {size} byte(s)\n    {line.Text.Trim()}");
                    Emit(d, size);
                    continue;
                }
                Emit(Value(item), size);
            }
        }

        // Data items are separated by commas and by whitespace: Spc.asm:1896 writes
        // ".data16 _tri_samp0,_tri_samp0 _tri_samp1,..." and the template has 8 words per
        // row at 0x20F40. Whitespace next to an operator stays inside the item ("a + b").
        static List<string> DataItems(string a)
        {
            var result = new List<string>();
            const string ops = "+-*/%&|^<>=!~(";
            foreach (var piece in SplitTop(a))
            {
                var words = piece.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length <= 1)
                {
                    result.Add(piece.Trim());
                    continue;
                }
                var cur = words[0];
                for (int i = 1; i < words.Length; i++)
                {
                    string w = words[i];
                    bool join = ops.IndexOf(cur[cur.Length - 1]) >= 0 || ops.IndexOf(w[0]) >= 0
                        || w[0] == ')' || cur.Contains('"') || w.Contains('"');
                    if (join) cur += " " + w;
                    else
                    {
                        result.Add(cur);
                        cur = w;
                    }
                }
                result.Add(cur);
            }
            return result;
        }

        void Fill(string a, int size)
        {
            var args = Args(a, 2);
            long count = Value(args[0]);
            long v = args.Count > 1 ? Value(args[1]) : 0;
            if (count < 0 || count > Rom.MaxSize) throw new AsmException($"Bad .fill count {count}");
            for (long i = 0; i < count; i++) Emit(v, size);
        }

        void EmitString(string s, bool terminate)
        {
            foreach (char c in s)
            {
                if (c > 0xff) throw new AsmException($"Character '{c}' doesn't fit in a byte");
                Emit(c);
            }
            if (terminate) Emit(0);
        }

        static string QuotedString(string a)
        {
            a = a.Trim();
            if (a.Length < 2 || a[0] != '"') throw new AsmException($"Expected a quoted string, got '{a}'");
            int end = a.IndexOf('"', 1);
            if (end < 0) throw new AsmException("Missing closing quote");
            if (a.Substring(end + 1).Trim().Length != 0) throw new AsmException($"Unexpected text after string: '{a.Substring(end + 1).Trim()}'");
            return a.Substring(1, end - 1);
        }

        // Comma-separated arguments; without commas, up to 'max' whitespace-separated
        // ones (".addr temp__ temp__&0xff0000|0xfeff" in Includes.asm)
        static List<string> Args(string a, int max)
        {
            var list = SplitTop(a);
            if (list.Count == 1 && max > 1)
            {
                string s = list[0];
                int i = 0;
                while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
                if (i < s.Length)
                    return new List<string> { s.Substring(0, i), s.Substring(i).Trim() };
            }
            if (list.Count == 0 || list[0].Length == 0) throw new AsmException("Missing argument");
            return list;
        }

        // Splits at commas outside () [] and quotes
        static List<string> SplitTop(string a)
        {
            var list = new List<string>();
            int depth = 0, start = 0;
            bool quote = false;
            for (int i = 0; i < a.Length; i++)
            {
                char c = a[i];
                if (c == '"') quote = !quote;
                else if (quote) continue;
                else if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    list.Add(a.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            list.Add(a.Substring(start).Trim());
            return list;
        }

        // ---------------------------------------------------------------
        // Vstack

        // ".x" 1 byte, "_x" 2, "=x" 3, "N x" N bytes; comma-separated
        List<(string name, int size)> VarList(string a)
        {
            var list = new List<(string, int)>();
            if (a.Trim().Length == 0) return list;
            foreach (var raw in SplitTop(a))
            {
                string item = raw.Trim();
                if (item.Length < 2) throw new AsmException($"Bad variable '{item}'");
                int size;
                string name;
                switch (item[0])
                {
                    case '.': size = 1; name = item.Substring(1); break;
                    case '_': size = 2; name = item.Substring(1); break;
                    case '=': size = 3; name = item.Substring(1); break;
                    default:
                        {
                            int sp = 0;
                            while (sp < item.Length && !char.IsWhiteSpace(item[sp])) sp++;
                            if (sp >= item.Length) throw new AsmException($"Variable '{item}' has no size prefix");
                            size = (int)Eval(item.Substring(0, sp));
                            name = item.Substring(sp).Trim();
                            if (size <= 0) throw new AsmException($"Bad variable size in '{item}'");
                            break;
                        }
                }
                list.Add((Name(name), size));
            }
            return list;
        }

        // Puts a variable at the top of the current frame
        void Allocate(string name, int size)
        {
            Define(name, localTop);
            localStack.Add((name, size));
            localTop += size;
            allocated += size;
            if (localTop > highWater) highWater = localTop;
            if (curFunc != null && highWater > curFunc.HighWater) curFunc.HighWater = highWater;
            if (vstackTop >= 0 && ((localTop - 1) & 0xffff) > (vstackTop & 0xffff))
                throw new AsmException($"Vstack overflow: '{name}' ends at {localTop - 1:X4}, .vstacktop is {vstackTop & 0xffff:X4}");
        }

        // .vstack v: a new frame starting at v (for code without .func; functions
        // without callers start at 0). Right at a function's entry (label, then .vstack, as in
        // Start__Reset and Start__Irq_Fast) it sets that function's base. Anywhere
        // else (Includes.asm per file, Cop__Wait4VBlank) it ends the function context.
        void VstackDirective(string a)
        {
            vstackBase = Value(a);
            localStack = new List<(string, int)>();
            localTop = highWater = allocated = vstackBase;
            callTarget = null;
            if (curFunc == null) return;

            bool atEntry = cur.TryGetValue(curFunc.Name, out var defs) && defs.Count > 0
                && (defs[defs.Count - 1].Value & 0xffffff) == pc;
            if (!atEntry)
            {
                curFunc = null;
                return;
            }
            if (curFunc.Params.Count > 0)
                throw new AsmException($".vstack at the entry of {curFunc.Name}, which has parameters (not seen in v1.8, behavior unknown)");
            curFunc.Base = curFunc.HighWater = vstackBase;
        }

        // .func Name params: new frame at the callers' highest mark (previous pass),
        // or at 0 when nothing calls it
        void FuncDirective(string a)
        {
            SplitFirst(a, out string name, out string rest);
            name = Name(name);
            if (funcs.TryGetValue(name, out var old))
                throw new AsmException($"Function '{name}' already declared at {old.Declaration}");

            var f = new Func { Name = name, Declaration = line };
            f.Params.AddRange(VarList(rest));

            // No callers: base 0, not the file's .vstack (Array__DeleteDuplicates firstP = $00,
            // listPointer = $0C at 0x11CC3; Memory__Zero length = $00 at 0x11EEA)
            long b = 0;
            if (prevCalls != null && prevCalls.TryGetValue(name, out var sites) && sites.Count > 0)
            {
                b = sites[0].Mark;
                foreach (var s in sites)
                    if (s.Mark > b) b = s.Mark;
            }
            f.Base = f.HighWater = b;
            funcs[name] = f;

            curFunc = f;
            callTarget = null;
            localStack = new List<(string, int)>();
            localTop = highWater = allocated = b;
            foreach (var (pn, ps) in f.Params)
                Allocate(pn, ps);
        }

        void LocalDirective(string a)
        {
            foreach (var (name, size) in VarList(a))
                Allocate(name, size);
        }

        // .unlocal: the named variables must be the top of the frame. The top drops,
        // the allocated total (base for later callees) stays.
        void UnlocalDirective(string a)
        {
            var list = VarList(a);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var (name, size) = list[i];
                if (localStack.Count == 0)
                    throw new AsmException($".unlocal '{name}': no local variables left");
                var top = localStack[localStack.Count - 1];
                if (!string.Equals(top.name, name, StringComparison.OrdinalIgnoreCase) || top.size != size)
                    throw new AsmException($".unlocal '{name}' ({size} bytes), but the top is '{top.name}' ({top.size} bytes)");
                localStack.RemoveAt(localStack.Count - 1);
                localTop -= size;
            }
        }

        // Function as known so far: this pass if declared already, else the previous pass
        Func FindFunc(string name)
        {
            if (funcs.TryGetValue(name, out var f)) return f;
            if (prevFuncs != null && prevFuncs.TryGetValue(name, out f)) return f;
            return null;
        }

        // .precall Name [params]: defines Param_x for the callee's parameters.
        // A parameter list, when given, must match the .func declaration.
        void PrecallDirective(string a)
        {
            SplitFirst(a, out string name, out string rest);
            name = Name(name);
            var given = VarList(rest);
            callTarget = name;

            var f = FindFunc(name);
            if (f == null)
            {
                if (prevFuncs != null)
                    throw new AsmException($".precall: '{name}' isn't declared with .func");
                unresolved = true;      // pass 1: declared further down
                return;
            }

            if (given.Count > 0)
            {
                bool same = given.Count == f.Params.Count;
                for (int i = 0; same && i < given.Count; i++)
                    same = string.Equals(given[i].name, f.Params[i].name, StringComparison.OrdinalIgnoreCase) && given[i].size == f.Params[i].size;
                if (!same)
                    throw new AsmException($".precall parameters don't match .func {name} at {f.Declaration}");
            }

            long at = f.Base;
            foreach (var (pn, ps) in f.Params)
            {
                Define("Param_" + pn, at);
                at += ps;
            }
        }

        // jsl/rtl when any call site is in another bank than the function
        // (previous pass); pass 1 assumes long until the call sites are known
        bool IsLong(string name, long funcBank, long thisCallBank)
        {
            if (thisCallBank >= 0 && thisCallBank != funcBank) return true;
            if (prevCalls == null) return true;
            if (prevCalls.TryGetValue(name, out var sites))
                foreach (var s in sites)
                    if (s.Bank != funcBank) return true;
            return false;
        }

        void Call(string a)
        {
            string name = a.Trim();
            // Without an operand: the last target from .precall or a named call
            // (Interpret__Rts_Changed: "phb / call / plb" repeats the macro's
            // call Recompiler__CallFunction, 8B 22 BC C7 81 AB at 0xA5DA)
            if (name.Length == 0)
            {
                if (callTarget == null) throw new AsmException("call without a function, and no earlier .precall or call in this function");
                name = callTarget;
            }
            name = Name(name);
            callTarget = name;

            if (curFunc != null && string.Equals(curFunc.Name, name, StringComparison.OrdinalIgnoreCase))
                throw new AsmException($"Recursive call to {name} (not supported by vstack)");

            long bank = pc >> 16;
            if (!calls.TryGetValue(name, out var sites))
                calls[name] = sites = new List<CallSite>();
            // The callee starts after everything allocated so far, freed locals included:
            // Gui__WriteTextFormat frees _rtn/_keepY (4) and reuses them, yet Dict__FindElement
            // starts at 0x85 = 0x81 + 4 in the template
            sites.Add(new CallSite { Bank = bank, Mark = allocated, Line = line });

            // A target without an address is accepted from pass 2 on: Gfx_Interrupt.asm:791
            // "FakeCall Gfx__VramQueue" names a macro. FakeCall restores the PC, so the
            // bytes are overwritten by the following code anyway.
            long target;
            bool known = cur.ContainsKey(name) || (prev != null && prev.ContainsKey(name));
            if (!known && prev != null)
            {
                Warnings.Add($"{line}: call target '{name}' is not a label, address 0\n    {line.Text.Trim()}");
                target = 0;
            }
            else
                target = Lookup(name) & 0xffffff;
            if (IsLong(name, target >> 16, bank))
            {
                Emit(0x22);
                Emit(target, 3);
            }
            else
            {
                Emit(0x20);
                Emit(target, 2);
            }
        }

        void Return(string a)
        {
            if (a.Trim().Length != 0) throw new AsmException("return takes no operand");
            if (curFunc == null) throw new AsmException("return outside a .func");
            long bank = (Lookup(curFunc.Name) & 0xffffff) >> 16;
            // No callers at all: rts (assumption, see README)
            Emit(IsLong(curFunc.Name, bank, -1) ? 0x6B : 0x60);
        }

        // ---------------------------------------------------------------
        // smx/rmx

        long FlagValue(string a)
        {
            a = a.Trim();
            if (a.StartsWith("#")) a = a.Substring(1);
            return Value(a) & 0xff;
        }

        // smx #v: sets the tracked flags to exactly v. Emits rep for bits to clear
        // and sep for bits to set (seen: mx=0x20, "smx #0x00" -> C2 20).
        // Untracked bits (i, c ...) in v always go into the sep.
        // TODO verify: order when both are needed, and output when nothing changes.
        void Smx(string a)
        {
            int v = (int)FlagValue(a);
            int clear = mx & ~v & 0x30;
            int set = v & ~mx;
            if (clear != 0) { Emit(0xC2); Emit(clear); }
            if (set != 0) { Emit(0xE2); Emit(set); }
            mx = v & 0x30;
        }

        // rmx v ('#' optional): rep for the m/x bits that are set now but not in v,
        // plus v's other bits (carry, decimal ...). Template:
        //  .mx 0x30: rmx #0x01 -> C2 31, rmx 0x20 -> C2 10, rmx #0x00 -> C2 30, rmx #0x15 -> C2 25
        //  .mx 0x20: rmx #0x01 -> C2 21 (Gui.StringFormat.asm:105)
        void Rmx(string a)
        {
            int v = (int)FlagValue(a);
            Emit(0xC2);
            Emit((mx & ~v & 0x30) | (v & ~0x30));
            mx &= v & 0x30;
        }

        // ---------------------------------------------------------------
        // Instructions

        struct Operand
        {
            public Mode Mode;
            public long Value;
            public int Size;
            public Operand(Mode mode, long value, int size) { Mode = mode; Value = value; Size = size; }
        }

        void Instruction(string m, string a)
        {
            var modes = Opcodes.Table[m];
            a = a.Trim();

            if (Opcodes.IsBranch(m) || Opcodes.IsLongRelative(m))
            {
                bool lng = Opcodes.IsLongRelative(m);

                // per/brl with a number literal: the value itself, not a target.
                // Interpret_and_Inline.asm:623 "per $0xff07" -> 62 07 FF at 0xA175 (self-modifying
                // code patches it). Labels stay relative (not used with per/brl in v1.8).
                string lit = a.StartsWith("$") ? a.Substring(1).Trim() : a;
                if (lng && Expr.TryParseNumber(lit, out long raw, out _))
                {
                    Emit(modes[Mode.RelLong]);
                    Emit(raw, 2);
                    return;
                }

                long target = BranchTarget(a);
                long next = (pc + (lng ? 3 : 2)) & 0xffffff;
                long rel = (target & 0xffff) - (next & 0xffff);
                if (!lng && !unresolved)
                {
                    if ((target & 0xff0000) != (next & 0xff0000))
                        throw new AsmException($"Branch to another bank ({target:X6})");
                    // Out of range: the v1.8 template has 0 there (IO.asm:2520/2538,
                    // "bne $+b_else" without a b_else in the file -> D0 00 at 0x1172F/0x11749)
                    if (rel < -128 || rel > 127)
                    {
                        Warnings.Add($"{line}: branch out of range ({rel} bytes), written as 0 like the v1.8 template\n    {line.Text.Trim()}");
                        rel = 0;
                    }
                }
                Emit(modes[lng ? Mode.RelLong : Mode.Rel]);
                Emit(rel, lng ? 2 : 1);
                return;
            }

            if (m == "mvn" || m == "mvp")
            {
                // Standard order "mvn src, dst", bytes are dst then src. Unused by Nested.
                var args = SplitTop(a);
                if (args.Count != 2) throw new AsmException($"{m} needs two banks");
                long src = Value(args[0].TrimStart('#', '$'));
                long dst = Value(args[1].TrimStart('#', '$'));
                Emit(modes[Mode.Block]);
                Emit(dst);
                Emit(src);
                return;
            }

            var op = ParseOperand(m, a);
            if (!modes.TryGetValue(op.Mode, out byte opcode))
                throw new AsmException($"'{m}' has no {op.Mode} addressing mode");
            Emit(opcode);
            if (op.Size > 0) Emit(op.Value, op.Size);
        }

        long BranchTarget(string a)
        {
            string s = a.Trim();
            if (s.StartsWith("$")) s = s.Substring(1).TrimStart();
            if (s.StartsWith("+")) return LookupForward(Name(s.Substring(1)));
            if (s.StartsWith("-")) return LookupBackward(Name(s.Substring(1)));
            return Value(s);
        }

        Operand ParseOperand(string m, string a)
        {
            var modes = Opcodes.Table[m];
            if (a.Length == 0)
                return new Operand(modes.ContainsKey(Mode.Imp) ? Mode.Imp : Mode.Acc, 0, 0);
            if (a.Equals("a", StringComparison.OrdinalIgnoreCase))
                return new Operand(Mode.Acc, 0, 0);

            if (a[0] == '#')
                return Immediate(m, a.Substring(1).Trim());

            SplitIndex(a, out string b, out char idx);

            if (b.Length >= 2 && b[0] == '(' && b[b.Length - 1] == ')')
            {
                SplitIndex(b.Substring(1, b.Length - 2), out string inner, out char innerIdx);
                long v = Address(inner, out int size);
                if (idx == 'y' && innerIdx == 's') return Sized(Mode.SrIndY, Mode.SrIndY, v, size, a, true);
                if (idx == 'y' && innerIdx == '\0') return Sized(Mode.IndY, Mode.IndY, v, size, a, true);
                if (idx == '\0' && innerIdx == 'x') return Sized(Mode.IndX, Mode.AbsIndX, v, size, a, false);
                if (idx == '\0' && innerIdx == '\0') return Sized(Mode.Ind, Mode.AbsInd, v, size, a, false);
                throw new AsmException($"Unknown indirect form '{a}'");
            }

            if (b.Length >= 2 && b[0] == '[' && b[b.Length - 1] == ']')
            {
                long v = Address(b.Substring(1, b.Length - 2), out int size);
                if (idx == 'y') return Sized(Mode.IndLongY, Mode.IndLongY, v, size, a, true);
                if (idx == '\0') return Sized(Mode.IndLong, Mode.AbsIndLong, v, size, a, false);
                throw new AsmException($"Unknown indirect form '{a}'");
            }

            {
                long v = Address(b, out int size);
                Mode mode;
                switch (idx)
                {
                    case '\0': mode = size == 1 ? Mode.Dp : size == 2 ? Mode.Abs : Mode.Long; break;
                    case 'x': mode = size == 1 ? Mode.DpX : size == 2 ? Mode.AbsX : Mode.LongX; break;
                    case 'y':
                        if (size == 3) throw new AsmException("There's no long,y addressing mode");
                        mode = size == 1 ? Mode.DpY : Mode.AbsY;
                        break;
                    case 's':
                        if (size != 1) throw new AsmException("Stack relative needs a 1-byte offset");
                        mode = Mode.Sr;
                        break;
                    default: throw new AsmException($"Bad index '{idx}'");
                }
                return new Operand(mode, v, size);
            }
        }

        // Indirect modes: direct page (1 byte) or absolute (2 bytes) form
        static Operand Sized(Mode dpMode, Mode absMode, long v, int size, string a, bool dpOnly)
        {
            if (size == 1) return new Operand(dpMode, v, 1);
            if (size == 2 && !dpOnly) return new Operand(absMode, v, 2);
            throw new AsmException($"Operand size {size} doesn't fit '{a}'");
        }

        Operand Immediate(string m, string e)
        {
            int want;
            switch (Opcodes.Immediate(m))
            {
                case ImmKind.M: want = (mx & 0x20) != 0 ? 1 : 2; break;
                case ImmKind.X: want = (mx & 0x10) != 0 ? 1 : 2; break;
                case ImmKind.One: want = 1; break;
                case ImmKind.Two: want = 2; break;
                default: throw new AsmException($"'{m}' takes no constant");
            }

            int given = 0;
            bool bank = false;
            if (e.Length > 1)
            {
                switch (e[0])
                {
                    case '.': given = 1; e = e.Substring(1); break;
                    case '_': given = 2; e = e.Substring(1); break;
                    case '=': given = 3; e = e.Substring(1); break;
                    case '^': bank = true; e = e.Substring(1); break;
                }
            }
            if (given != 0 && given != want)
                throw new AsmException($"Constant is {given} byte(s) but '{m}' expects {want} with .mx 0x{mx:x2}");

            long v = Eval(e);
            if (bank) v >>= 16;
            return new Operand(Mode.Imm, v, want);
        }

        // $ operand: prefix gives the size (. 1, _ 2, = 3, ^ bank byte);
        // an unprefixed literal is sized by its hex digits ($0x00 = 1, $0x420d = 2,
        // $0xb07ffe = 3) or a decimal by value ($2,s = 1)
        long Address(string s, out int size)
        {
            s = s.Trim();
            if (s.StartsWith("$")) s = s.Substring(1).TrimStart();
            if (s.Length == 0) throw new AsmException("Missing address");

            switch (s[0])
            {
                case '.': size = 1; return Eval(s.Substring(1));
                case '_': size = 2; return Eval(s.Substring(1));
                case '=': size = 3; return Eval(s.Substring(1));
                case '^': size = 1; return Eval(s.Substring(1)) >> 16;
                case '+':
                case '-':
                    throw new AsmException("Near labels ($+ $-) only work with branches");
            }

            long v = Eval(s);
            if (Expr.TryParseNumber(s, out _, out int hexDigits) && hexDigits > 0)
                size = hexDigits <= 2 ? 1 : hexDigits <= 4 ? 2 : 3;
            else
                size = v <= 0xff ? 1 : v <= 0xffff ? 2 : 3;
            return v;
        }

        // "x,y" -> base "x", index 'y'; only a trailing ,x ,y or ,s counts
        static void SplitIndex(string s, out string b, out char idx)
        {
            s = s.Trim();
            int depth = 0, comma = -1;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 0) comma = i;
            }
            if (comma >= 0)
            {
                string i = s.Substring(comma + 1).Trim().ToLowerInvariant();
                if (i == "x" || i == "y" || i == "s")
                {
                    b = s.Substring(0, comma).Trim();
                    idx = i[0];
                    return;
                }
            }
            b = s;
            idx = '\0';
        }
    }
}
