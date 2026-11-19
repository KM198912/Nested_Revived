# NestedAsm

A rebuild of Myself086's private assembler, so `Assembly/` can be assembled again.
The original was never released (see `Assembly/HOW_TO_ASSEMBLE.txt`).

**Goal, reached:** assembling the v1.8 sources (commit `c4c292d`) with `--date 2023-04-09` produces a
byte-identical `Project Nested/Project_Nested.smc` (MD5 `0aa4002338a84a0a7111cd2aaf8281c8`).
Run from `Nested.Assembler\`:

    NestedAsm reference\v1.8\Assembly\Project\Includes.asm --base reference\v1.8\Assembly --date 2023-04-09 -o Project_Nested.smc --compare "..\Project Nested\Project_Nested.smc"

Result: 0 errors, 9 warnings (see "Known quirks of the v1.8 sources"), `MATCH (327680 bytes)`.

The spec is `Assembly/Asm_syntax.txt`. Every rule it leaves open is worked out from the
v1.8 binary and listed below.

## Usage

    NestedAsm <root.asm> [-o out.smc] [--base dir] [--date yyyy-MM-dd]
              [--compare ref.smc] [--range a-b,...] [--show n] [--vstack] [--no-header]
              [--spc-fix-incw]

`--compare` lists differing bytes with the source line that wrote each one.
`--vstack` lists every `.func` with its frame base, size and call sites.
`--no-header` skips the Empty base image and the checksum (both only for outputs of 64 KB and more).
`--spc-fix-incw` encodes SPC700 `incw` correctly (see SPC700 rules); the output then differs from the v1.8 template.

## Status

| Step | Content | State |
|------|---------|-------|
| 1 | Lexer, expressions, 65816 encoding, labels, near labels, data directives, `.addr`, patches, smx/rmx | MATCH, `tests/step1_header.asm` |
| 2 | Macros (`.macro`/`.endm`, String.Format parameters, `.repeat`) | MATCH, `tests/step2_macros.asm` |
| 3 | `.mx` checking for constants | MATCH (full tree) |
| 4 | vstack: `.vstack` `.vstacktop` `.func` `.local` `.unlocal` `.precall` `call` `return` | MATCH (full tree), rule test: `tests/step4_vstack.asm` (`--vstack`) |
| 5 | Conditionals: `.if` `.else` `.false` `.ifnamecontains`, `Game` blocks, `{ }`, `.nowrite` `.fix` `.addrlow` | MATCH (full tree), rule test: `tests/step5_blocks.asm` |
| 6 | `.image` / `.palette` | MATCH, `tests/step6_image.asm` |
| 7 | SPC700: `.includeSPC` `.offsetfrom` `.offsetend` | MATCH (full tree) |
| - | Full v1.8 tree | MATCH, MD5 `0aa4002338a84a0a7111cd2aaf8281c8` |

## Known quirks of the v1.8 sources

The full build gives 9 warnings. Each marks a quirk in the sources or the original assembler that the template contains, not a gap in NestedAsm:

| Warning | Meaning |
|---|---|
| IO.asm:2520, 2538 | `bne $+b_else` without a `b_else` in the file; offset 0, so the "Controller B" code always runs |
| Array.asm:1249 | `$-Array__DeleteDuplicates_loop3_loopEnd` should be `$+`; the label comes later |
| Macros.asm:365 (`FakeCall Gfx__VramQueue`) | call to a name that is only a macro; no effect, the bytes are overwritten |
| Spc700/Spc.asm 2227, 2230, 2238, 2448, 2449 | the original assembler writes `incw` as `AB` (`inc dp`); `--spc-fix-incw` writes the correct `3A` |

## Rules confirmed from the v1.8 binary

- Fast HiROM layout: C0-FF whole bank, 80-BF upper half, both map to file offset `(bank & 0x3f) << 16 | addr`.
- `$` operands: `.` `_` `=` choose 1/2/3 bytes. An unprefixed hex literal is sized by its digits (`$0x420d` is absolute), a decimal one by its value (`$2,s`).
- `call` turns into `jsl` when the callers are in other banks (Header.asm: `22 8D E7 81`).
- `trap` = `bra` to itself (`80 FE`). `unlock` = `cli`.
- `Exception "..."` emits no inline code. It appends `next(16) | 03 | trap address(24) | string\0` to a linked list through `.pushaddr` / `.addr ExceptionData_WritePointer`.
- `smx #0x00` with `.mx 0x20` emits `C2 20` (rep for the bits being cleared).
- `rmx v` (`#` optional) emits `rep #((mx & ~v & 0x30) | (v & ~0x30))`: the m/x bits that are set now but not in `v`, plus `v`'s other bits. Afterwards `.mx` keeps only the m/x bits in `v`.

  | Source | `.mx` before | Template |
  |---|---|---|
  | `rmx #0x01` (4x, Interpret_IndirectIO) | 0x30 | `C2 31` |
  | `rmx 0x20` (IO.4014.asm, then `lda #0xf0` = `A9 F0`) | 0x30 | `C2 10` |
  | `rmx #0x00` (Gui.asm:833) | 0x30 | `C2 30` |
  | `rmx #0x15` (Gfx_Interrupt.asm:121) | 0x30 or 0x20 | `C2 25` |
  | `rmx #0x01` (Gui.StringFormat.asm:105) | 0x20 | `C2 21` |
- `.printdate "yyyy-MM-dd"` writes the assembly date. The template holds `2023-04-09`.
- `[80ffd5] = 0x31`: bare digits inside the brackets are hex. Writes one byte without moving the PC.
- `.addrlow X` sets only the low 16 bits of the PC; the bank and the `.addr` limit stay. The Exception list proves it: it runs contiguously from `ExceptionData` (`$49A4`) with the entries of Gui.asm and every later file, then Header.asm's entries (`5C8B`, `5D46`, written before Gui.asm via the previous pass's final pointer), then a single `00` at `5DC7` from `.addrlow ExceptionData_WritePointer` / `.data8 0`. The next file (Gui.StringFormat.asm, `64 4A A9 00 00 E2 20 ...`) continues at `5DC8`. EmuCalls, SelfMod and `SegmentEnd` use it the same way.

- For the game `"Empty"` the assembler starts from a base image and writes the source on top. Each 64 KB bank has the same header at `FFC0-FFD8`: `"Empty"` padded to 21 characters, then `19 02 0C 05`; everything else is 0. The values `19 02 0C 05` don't come from the `Game` line, so they are taken from the template.

  | Place | Template | Explanation |
  |---|---|---|
  | C1:FFC0, C2:FFC0 | `"Empty ..." 19 02 0C 05` | base image; the code in these banks ends earlier |
  | C0:FFD6 | `02` | base image; the source writes `FFC0-FFD5`, `FFD7`, `FFD8`, `FFDB` around it |
  | C0:FFD9-FFDA | `00 00` | base image |
  | C3/C4:FFC0 | code / zeros | overwritten by `JMPiUnrolled` |

- The assembler computes the checksum; the sources never write `FFDC-FFDF`. The checksum is the plain 16-bit sum of the whole output with `FFDC-FFDF` counted as `FF FF 00 00`, without mirroring to a power of two: 0x9239 for the 0x50000-byte template (`39 92` at `FFDE`, complement `C6 6D` at `FFDC`). Mirroring to 8 Mbit would give 0x266A.
- A data item `+name` is the distance from the byte after the value to the next `name` (like a branch offset). `CoreCall_CopyUpTo +b_1` expands to `.data8 +b_1`: `1C 03 8E 11 0A 02` at `0x1034A` (3 bytes of `stx`, then `b_1`). Used 116 times (`CoreCall_CopyUpTo`, `CoreCall_IfNotFreeA`, the goto byte of `CoreCall_IfSet`/`IfClear`). `-name` is the distance back to the latest `name`, and more expression may follow: `SelfMod_QuickCopy` expands to `.data8 -temp__-1`, which is -5 - 1 = `FA` at `0x206A9`.
- `per` with a number literal writes the value itself: `per $0xff07` = `62 07 FF` at `0xA175` (Interpret_and_Inline.asm:623, also 648 and 664; self-modifying code patches the value).
- A branch out of range is written with offset 0 and reported as a warning. IO.asm:2520 and 2538 have `bne $+b_else` with no `b_else` after them in the file; the template has `D0 00` at `0x1172F` and `0x11749` (the "Controller B" code always runs). These are the only two such branches in the tree.

## SPC700 rules

- `.includeSPC "file"` assembles the file as SPC700 code; `.include`s inside it (the `pl*.asm` sample files) stay in SPC700 mode. Directives and macros work as usual.
- SPC700 syntax: `#imm`, `dp`, `!abs`, `dp+x`, `dp+y`, `!abs+x`, `!abs+y`, `[dp]+y`, `[dp+x]`, `[!abs+x]`, `(x)`, `(x)+`, `(y)`; bit forms `set1 dp, bit` and `bbs dp, bit, target`; branch targets are labels or `+name` / `-name`.
- `.offsetfrom 0x0400` ... `.offsetend`: labels and `.addr` inside use logical (SPC) addresses from 0x0400, the bytes go to the current ROM position. `dmc_pitch_table` uses `.addr dmc_pitch_table_lo+index__` with SPC addresses. After `.offsetend` the PC is the ROM position after the code (`Spc_Code_End`).
- Data items are separated by commas and whitespace: Spc.asm:1896-1897 writes `.data16 _tri_samp0,_tri_samp0 _tri_samp1,...`, and the template has 8 words per row at `0x20F40`. These are the only such items in the tree.
- Binary literals with a `b` suffix: `#00000111b`.
- **Bug in the original assembler:** `incw dp` is written as `AB` (`inc dp`, 8-bit) instead of `3A`. All five uses in Spc.asm (2227, 2230 `incw temp_pointer`; 2238, 2448, 2449 `incw temp7`) are `AB` in the template (`0x2111C`, `0x21122`, `0x2112A`, `0x21386`, `0x21388`), so these pointers only count up their low byte and wrap within the page (DMC sample conversion). NestedAsm reproduces this by default, with a warning per `incw`; `--spc-fix-incw` writes the correct `3A`. For the fork that fixes the sound driver, but the result is no longer the v1.8 template. `decw` (unused in v1.8) is encoded correctly as `1A`.
- `.dw` writes 16-bit words like `.data16`: tritabl3.asm uses it 2047 times, and the SPC code size in Sound.asm (`Spc_Code_End-Spc_Code_Start`) is `0x2FBB` in the template, 4094 bytes more than without them.

## Image rules

- `.palette c0, c1, ...` lists RGB888 colours; a pixel's colour number is its index in that list.
- `.image "file", 1, byte`: 8x8 tiles row by row, 8 bytes per tile, one per pixel row, leftmost pixel in the MSB, bit 0 of the colour number. Gui.asm's font (`Font.png`, 128x64, only `0x000000` and `0xFFFFFF`) converts to exactly the 1024 bytes at `0x40E6` (`Gui__Init_CHR`). White is index 1 (bit set), black index 2 (bit clear), so the palette order matters.
- v1.8 only uses 1 bpp with `byte`. Other bpp values and `word` give an error instead of a guessed bitplane layout. PNGs must be 8-bit RGB or RGBA without interlacing.

## Block rules (from the v1.8 sources)

- `{` and `}` always stand alone on a line. A condition opens the block on the next line; `.else` may follow with a second one.
- `.if expr`: true when nonzero. `.ifnamecontains a, b`: case-sensitive substring test, quotes optional (`CoreCall_Use "A8nz"`). `.false`: never true.
- A false block is skipped by counting braces only, so nothing in it is defined (Gui.asm switches off a second `Gui__GetRamUsage` macro and a `.func` this way).
- `Game "title", a, b`: the original assembler apparently also patches existing ROMs. Nested always builds the game `"Empty"` (padded to the 21-character SNES title) with `0x00, 0x00`. Includes.asm uses a `Game` line without a block, followed by `.else { .nowrite }`.
- `.nowrite`: drops all ROM writes for the rest of the pass; the PC keeps moving.
- `.outfile` is set twice in Includes.asm; the last one (`Project_Nested.smc`) wins.

## Macro rules (from the v1.8 sources)

- Macros have their own namespace, so a label and a macro can share a name. Names are case-insensitive like symbols: Macros.asm defines `exception` and then `Exception`, but `exception` only calls `Exception "{0}"`, so the later definition replacing it gives the same result.
- A macro not defined yet in the current pass comes from the previous pass (Gui.asm uses `Gui__Color` at line 210 and defines it at 698; Variables.asm uses `EmuCallAt` from EmuCalls.asm).
- Parameter names after `.macro Name` are documentation only. Bodies use `{0}`, `{1}` and so on.
- Arguments split at top-level commas. If there are none, they split at whitespace (`CoreCall_DEFINE CoreCall_Call ".data16 {0}"`). Outer quotes are removed, so `""` is an empty argument.
- Substitution runs once over the whole body with String.Format semantics. An argument is inserted verbatim, and braces inside it are not substituted again. This is how `CoreCall_DEFINE` defines an inner macro whose `{0}` belongs to the inner call. Only `{n}` and `{n:fmt}` are replaced; `{ }` block braces stay.
- `.macro` / `.endm` nest. An inner macro is only defined when the outer one expands.
- `.repeat N, "statement"` runs the statement N times. The quotes are optional: Mapper69.asm:133 has `.repeat 8, Mapper69__a000_Switch_RepeatMac`, and the template holds its 32-byte table 8 times at `0x1E508`.

## Symbol rules

- Symbols are case-insensitive: `Interpret__IndirectYIO_Sta:` is referenced as `$_Interpret__IndirectYIO_sta`. The only labels in the tree that differ just by case are `b_loop`/`b_Loop` and `this__`/`This__`, both temporary labels where merging them changes no reference.
- `.def name value`: the value is one word; anything after it is a note (`.def IO_2002_LastReturn 0x4315 16-bit`).
- Symbols can be redefined (`temp__`, `this__`, `b_1`). A plain reference takes the latest definition so far.
- A symbol not yet defined in the current pass takes its **final** value from the previous pass. `ExceptionData_WritePointer` is first set in Gui.asm, after Header.asm's exceptions have already used it. To verify in the full tree: the Header entries land at `5C8B`.
- `$+name` takes the next definition after the current statement (from the previous pass), and `$-name` the latest one before it.
- `$-name` without any earlier definition works like a plain reference (with a warning). Array.asm:1249 has `bcs $-Array__DeleteDuplicates_loop3_loopEnd`, but the label only comes at line 1296; the template branches forward (`B0 3B` at `0x11D17`).

## Vstack rules

- A callee's frame starts **after everything the caller has allocated up to the `call` line**: the caller's base plus every parameter and `.local` so far, in source order. Locals declared after the `call` don't count. `.unlocal` frees addresses for the caller's own later locals, but the freed bytes still count for later calls.
- A function without any `call` callers starts at 0, not at the file's `.vstack` value: `Array__DeleteDuplicates` has `firstP` = `$00` and `listPointer` = `$0C` (`0x11CC3`), `Memory__Zero` has `length` = `$00` (`0x11EEA`). These are the only two such functions with locals, apart from `Start__Irq_Fast`, which sets its base with `.vstack`.
- Locals are allocated in declaration order. `.local 4 count__, _index__` takes 4 bytes, then 2.
- Parameters (`.func Name =list, =compareString, .length`) come first in the callee's frame, in order, and its locals follow. `Dict__FindElement`: `list` `$85`, `compareString` `$88`, `length` `$8B`, local `firstChar` `$8C`. The caller's `.precall` defines `Param_list` = `$85` (`86 85` at `0x457B`). `.precall` may leave out the parameter list (`Gui__WriteTextFormat` macros); the names come from `.func`.
- `call` without an operand calls the last target, set by `.precall X` or a named `call X`. Interpret__Rts_Changed: the macro `Recompiler__CallFunction` does `call Recompiler__CallFunction`, then `phb / call / plb` calls it again (`8B 22 BC C7 81 AB` at `0xA5DA`).
- A `call` to a name without an address works, with a warning, and uses address 0. Gfx_Interrupt.asm:791 has `FakeCall Gfx__VramQueue`, where `Gfx__VramQueue` is only a macro. `FakeCall` (`.pushaddr` / `call {0}` / `.pulladdr`) restores the PC, so the bytes are always overwritten and only the call site counts.
- `.vstack v` right at a function's entry sets that function's base: `Start__Irq_Fast` has `.vstack _IRQ_VSTACK_START`, and its `_a`/`_s` are at `$1D3C`/`$1D3E` (`8D 3C 1D`). Code without `.func` uses it as well (`Cop__Wait4VBlank`), and `Includes.asm` resets it at the start of each file. Bases are full 16-bit addresses; `$.x` takes the low byte.
- `call` emits `jsl` for every call site as soon as one caller is in another bank, even when caller and callee share a bank (`Memory__AllocMax` -> `Memory__AllocInBank`, both in C1: `22 1D 1E C1`). A callee with only same-bank callers gets `jsr`.

Evidence (the first three callees have exactly one caller, `Gui__FrameUpdate`):

| Caller | Callee's first local | Bytes |
|---|---|---|
| `call Gui__CalcCpuPercent` comes before `.local _temp` (`_temp` = `$40`) | `_low` = `$40` | `aa 85 40 64 42` at `0x46B8` |
| `.local _temp`, then `.unlocal _temp`, then `call Gui__GetRamUsed` | `count__` = `$42` (`_index__` `$46`, `_temp__` `$48`) | `85 48 ... 85 42 ... 85 46` at `0x4639` |
| `.local _used, _total` after the `.unlocal` | `_used` = `$40` (reuses `_temp`'s slot), `_total` = `$42` | `85 40 ... 85 42` at `0x452E` |

| `Gui__WriteTextFormat`: base `$40` + params and locals to `$6E`, then `_rtn, _keepY` (4, freed again), then the `Formatting` locals reuse `$6E`-`$80` | `Dict__FindElement` `list` = `$85` = `$6E` + 4 + `$13`, not the high-water mark `$81` | `64 71 64 7C ...` at `0x5E6F` (locals), `86 85` at `0x5EAC` |

This rules out "after the caller's whole local size" (`CalcCpuPercent` would be at `$44`), "at the caller's current top" (`GetRamUsed` would be at `$40`) and "at the caller's high-water mark" (`Dict__FindElement` would be at `$81`). `Memory.asm` fits too: `_bank7e` at `$6C`, unlocaled, then `call Memory__AllocInBank`, which starts at `$6E`.

## Assumptions still to verify

- Operator precedence: comparisons bind weaker than `&` / `|` (`.if temp__&0xffff < 0x8000`).
- `smx` when both rep and sep are needed (order), and when nothing changes.
- `.misalign`: assumed to step one byte off the remainder.
- Vstack, callee with several callers: its base is the highest base any of its call sites gives it (`Memory__AllocInBank` has seven callers).
- `return` in a function without any `call` callers emits `rts`.
- A `.vstack` that isn't right at a function's entry (label, then `.vstack`) ends the function context, so a later `return` there is an error.
- The Empty base header sits in every bank of the output; in C3/C4 it is fully overwritten, so the template can't show it there.
- `per`/`brl` with a label are relative to the label; v1.8 only uses `per` with literals and never uses `brl`.
- "Branch out of range -> 0" was chosen over "a near label not found in the same file -> 0"; for v1.8 both give the same bytes.
- The target of an operand-less `call` is reset at `.func` and `.vstack`, so a bare `call` at a function's start is an error instead of reusing the previous function's target.
- `.fix "name"` has no effect on the output (revisit with SPC700, where `.fix "Sound"` surrounds `.includeSPC`).
- When a condition flips between passes, the statement count shifts (a skipped macro call counts as one statement), so `$+label` needs an extra pass to settle.
