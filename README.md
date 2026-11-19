# Nested Revived

A continuation of **Project Nested**, the NES emulator for the SNES by Myself086 (Laurent Courville).
The original project was last released as v1.8.1 in July 2023. Nested Revived picks it up from there:
it makes the SNES core buildable again and adds the most requested mapper, MMC5.

Project Nested is MIT-licensed; the original copyright notice is kept in [License.md](License.md).
All credit for the emulator itself goes to Myself086.

## What's new

- **NestedAsm**: a rebuild of the private assembler the SNES core was written for, which was never released.
  It assembles the v1.8 sources into a `Project_Nested.smc` that is byte-identical to the official one
  (MD5 `0aa4002338a84a0a7111cd2aaf8281c8`), so the core can finally be changed and rebuilt.
  Details, usage and every rule worked out from the original binary: [Nested.Assembler/README.md](Nested.Assembler/README.md).
- **MMC5 (mapper 5), work in progress**: PRG modes 2 and 3, CHR mode 3 (1 KB banks), CIRAM nametable mapping.
  Features that aren't emulated yet stop with an error screen that names the MMC5 register, instead of
  running wrong. Status below.
- **Per-game SNES header title**: the converted ROM's internal title holds the game name and its CRC32, so flash
  carts that name save files by header title keep a separate save per game.
- The build date on the error screen is the date the core was assembled.

## MMC5 status

| Feature | State |
|---|---|
| PRG mode 3 (4x 8 KB) and mode 2 (16 KB + 2x 8 KB) | done |
| PRG modes 0 and 1, PRG-RAM in ROM ranges | not yet |
| CHR mode 3 (8x 1 KB, `$5120-$5127`) | done |
| Other CHR modes, `$5128-$512B` (8x16 sprites), `$5130` high bits | not yet |
| Nametable mapping: vertical, horizontal, single screen | done |
| ExRAM, fill mode, vertical split | not yet |
| Scanline IRQ, multiplier, MMC5 audio | not yet |

Tested: Hero of Aethel (NES homebrew) runs completely. Castlevania III is the current target.

## Building

The exe (C#, .NET Framework 4.8) builds with Visual Studio from `Project Nested.sln`.

The SNES core is assembled with NestedAsm (.NET 8, also in the solution), from the `Nested.Assembler` folder:

    bin\Release\net8.0\NestedAsm.exe ..\Assembly\Project\Includes.asm --base ..\Assembly -o "..\Project Nested\bin\Release\Project_Nested.smc"

Write the result next to the exe as shown. `Project Nested\Project_Nested.smc` is the official v1.8 template and
serves as the reference for NestedAsm's byte-for-byte check, so it isn't overwritten. The exe loads the template when
a NES ROM is opened, so open the ROM again after rebuilding the core.

## About Project Nested

Project Nested is a NES emulator running on the raw power of the SNES. Inspired by the SNES originally designed to be
backward compatible and wanting to code a JIT compiler. This project was started in the summer of 2018 after 3 casual
years (2015-2018) doing math on its potential performance until the day the design was in theory capable of running
games at full speed.

Emulators are software capable of executing code normally incompatible to the current hardware. They often come at a
big performance loss due to hardware differences and CPU code running indirectly. High performance emulators will
rewrite code JIT (just-in-time) as opposed to AOT (ahead-of-time) due to unpredictable code destinations.

If you have legally owned ROM dumps of NES games, they may be playable on SNES by using Nested and a flash cart
(SD2SNES, Super Everdrive, etc). No ROMs are included or distributed with this project.

Myself086's compatibility list (state of v1.8.1): https://docs.google.com/spreadsheets/d/1xKZIyNz1DSI3ZBdMfaTEaa_9b6IEABx-ZPwOb6XqcLQ/edit?usp=sharing

Instructions on how to use Nested can be found on the exe's main window.
