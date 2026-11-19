using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Nested.Assembler
{
    static class Program
    {
        const string Usage =
@"NestedAsm <root.asm> [options]
  -o <file>          output file (default: NestedAsm_out.smc in the current folder)
  --base <dir>       folder that .include paths start from (default: root file's folder)
  --date yyyy-MM-dd  date for .printdate (v1.8 template: 2023-04-09)
  --compare <smc>    compare the output against a reference ROM
  --range a-b,...    hex file ranges for --compare (default: whole file)
  --show <n>         differences to list (default 20)
  --vstack           list every .func: frame base, size, call sites, jsr/jsl
  --no-header        no Empty base image, no checksum at FFDC-FFDF
  --spc-fix-incw     SPC700 incw as 3A (correct) instead of AB like the original assembler";

        static int Main(string[] args)
        {
            string root = null, outFile = null, baseDir = null, compare = null, ranges = null;
            int show = 20;
            bool vstack = false, noHeader = false, spcFixIncw = false;
            DateTime date = DateTime.Today;

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{a} needs a value");
                    switch (a)
                    {
                        case "-o": outFile = Next(); break;
                        case "--base": baseDir = Next(); break;
                        case "--date": date = DateTime.ParseExact(Next(), "yyyy-MM-dd", CultureInfo.InvariantCulture); break;
                        case "--compare": compare = Next(); break;
                        case "--range": ranges = Next(); break;
                        case "--show": show = int.Parse(Next()); break;
                        case "--vstack": vstack = true; break;
                        case "--no-header": noHeader = true; break;
                        case "--spc-fix-incw": spcFixIncw = true; break;
                        case "-h":
                        case "--help": Console.WriteLine(Usage); return 0;
                        default:
                            if (a.StartsWith("-") || root != null) throw new ArgumentException($"Unknown argument '{a}'");
                            root = a;
                            break;
                    }
                }
                if (root == null) throw new ArgumentException("No root file given");
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.WriteLine(Usage);
                return 2;
            }

            var asm = new Assembler
            {
                BaseDir = Path.GetFullPath(baseDir ?? Path.GetDirectoryName(Path.GetFullPath(root))),
                BuildDate = date,
                SpcFixIncw = spcFixIncw,
            };
            bool ok = asm.Assemble(root);

            const int maxErrors = 50;
            for (int i = 0; i < asm.Errors.Count && i < maxErrors; i++)
                Console.Error.WriteLine(asm.Errors[i]);
            if (asm.Errors.Count > maxErrors)
                Console.Error.WriteLine($"... and {asm.Errors.Count - maxErrors} more");
            for (int i = 0; i < asm.Warnings.Count && i < maxErrors; i++)
                Console.Error.WriteLine("warning: " + asm.Warnings[i]);
            if (asm.Warnings.Count > maxErrors)
                Console.Error.WriteLine($"... and {asm.Warnings.Count - maxErrors} more warnings");
            Console.WriteLine($"{asm.PassCount} passes, {asm.Errors.Count} errors, {asm.Warnings.Count} warnings");
            if (vstack)
                DumpVstack(asm);

            long size = asm.FinalSize > 0 ? asm.FinalSize : asm.Rom.HighWater;
            var bytes = new byte[size];
            Array.Copy(asm.Rom.Data, bytes, Math.Min(size, asm.Rom.Data.Length));
            if (!noHeader && bytes.Length >= 0x10000)
            {
                if (asm.EmptyGame)
                    ApplyEmptyBase(asm, bytes);
                FinishHeader(asm, bytes);
            }

            outFile = Path.GetFullPath(outFile ?? "NestedAsm_out.smc");
            if (compare != null && string.Equals(outFile, Path.GetFullPath(compare), StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Refusing to overwrite the reference ROM; choose another -o");
                return 2;
            }
            File.WriteAllBytes(outFile, bytes);
            Console.WriteLine($"Wrote {outFile} ({size} bytes)");

            if (compare != null && !Compare(asm, bytes, compare, ranges, show))
                ok = false;

            return ok ? 0 : 1;
        }

        static bool Compare(Assembler asm, byte[] got, string refPath, string rangeText, int show)
        {
            byte[] want = File.ReadAllBytes(refPath);
            var ranges = new List<(long start, long end)>();
            if (rangeText == null)
                ranges.Add((0, Math.Max(want.Length, got.Length) - 1));
            else
                foreach (var part in rangeText.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var ends = part.Split('-');
                    long s = Convert.ToInt64(ends[0].Trim(), 16);
                    long e = ends.Length > 1 ? Convert.ToInt64(ends[1].Trim(), 16) : s;
                    ranges.Add((s, e));
                }

            long total = 0, bad = 0, shown = 0;
            foreach (var (start, end) in ranges)
            {
                long rangeBad = 0;
                for (long o = start; o <= end; o++)
                {
                    int g = o < got.Length ? got[o] : -1;
                    int w = o < want.Length ? want[o] : -1;
                    total++;
                    if (g == w) continue;
                    bad++;
                    rangeBad++;
                    if (shown++ < show)
                    {
                        string origin = "never written";
                        if (o < asm.Rom.Origin.Length && asm.Rom.Origin[o] == HeaderOrigin)
                            origin = "header checksum";
                        else if (o < asm.Rom.Origin.Length && asm.Rom.Origin[o] == BaseOrigin)
                            origin = "base image (Empty)";
                        else if (o < asm.Rom.Origin.Length && asm.Rom.Origin[o] > 0)
                        {
                            var l = asm.AllLines[asm.Rom.Origin[o] - 1];
                            origin = $"{l}: {l.Text.Trim()}";
                        }
                        Console.WriteLine($"  0x{o:X6}: got {Hex(g)} want {Hex(w)}   {origin}");
                    }
                }
                Console.WriteLine($"{start:X6}-{end:X6}: {end - start + 1 - rangeBad}/{end - start + 1} bytes match");
            }
            Console.WriteLine(bad == 0 ? $"MATCH ({total} bytes)" : $"{bad} of {total} bytes differ");
            return bad == 0;
        }

        static string Hex(int b) => b < 0 ? "--" : b.ToString("X2");

        const int HeaderOrigin = -1;    // Rom.Origin marker for bytes set by FinishHeader
        const int BaseOrigin = -2;      // Rom.Origin marker for bytes from the Empty base image

        // Header of the "Empty" game, found in every bank at FFC0 where the source doesn't
        // write: C1:FFC0 and C2:FFC0 in full, C0:FFD6 (02) and C0:FFD9-FFDA (00).
        // 19 02 0C 05 don't come from the Game line; they are taken from the template.
        static readonly byte[] EmptyHeader = BuildEmptyHeader();

        static byte[] BuildEmptyHeader()
        {
            var h = new byte[0x19];     // FFC0-FFD8
            for (int i = 0; i < 21; i++)
                h[i] = (byte)"Empty                "[i];
            h[0x15] = 0x19;
            h[0x16] = 0x02;
            h[0x17] = 0x0c;
            h[0x18] = 0x05;
            return h;
        }

        // The original assembler starts from a base image for the matched game and writes the
        // source on top: every unwritten byte of FFC0-FFD8 in each 64 KB bank gets the header.
        static void ApplyEmptyBase(Assembler asm, byte[] b)
        {
            for (int bank = 0; bank + 0x10000 <= b.Length; bank += 0x10000)
                for (int i = 0; i < EmptyHeader.Length; i++)
                {
                    int o = bank + 0xffc0 + i;
                    if (asm.Rom.Origin[o] != 0) continue;
                    b[o] = EmptyHeader[i];
                    asm.Rom.Origin[o] = BaseOrigin;
                }
        }

        // The original assembler finished the header itself (the v1.8 sources never write
        // these bytes). Checksum: plain 16-bit sum of the whole output with FFDC-FFDF counted
        // as FF FF 00 00, no mirroring -> 0x9239 for the template.
        static void FinishHeader(Assembler asm, byte[] b)
        {
            b[0xffdc] = 0xff; b[0xffdd] = 0xff; b[0xffde] = 0x00; b[0xffdf] = 0x00;
            int sum = 0;
            foreach (byte x in b) sum += x;
            sum &= 0xffff;
            int complement = sum ^ 0xffff;
            b[0xffdc] = (byte)complement;
            b[0xffdd] = (byte)(complement >> 8);
            b[0xffde] = (byte)sum;
            b[0xffdf] = (byte)(sum >> 8);
            for (int o = 0xffdc; o <= 0xffdf; o++)
                asm.Rom.Origin[o] = HeaderOrigin;
        }

        // One block per function, in declaration order
        static void DumpVstack(Assembler asm)
        {
            if (asm.Functions == null) return;
            var list = new List<Assembler.Func>(asm.Functions.Values);
            list.Sort((a, b) => a.Declaration.Id.CompareTo(b.Declaration.Id));
            foreach (var f in list)
            {
                asm.CallSites.TryGetValue(f.Name, out var sites);
                int n = sites?.Count ?? 0;
                Console.WriteLine($"{f.Name}  base {f.Base & 0xffff:X4}  size {f.HighWater - f.Base}  {n} call(s)  ({f.Declaration})");
                if (f.Params.Count > 0)
                {
                    var p = new List<string>();
                    long at = f.Base;
                    foreach (var (name, size) in f.Params)
                    {
                        p.Add($"{name}={at & 0xffff:X4}");
                        at += size;
                    }
                    Console.WriteLine($"    params {string.Join(", ", p)}");
                }
                if (sites != null)
                    foreach (var s in sites)
                        Console.WriteLine($"    from bank {s.Bank:X2} mark {s.Mark & 0xffff:X4}  {s.Line}");
            }
        }
    }
}
