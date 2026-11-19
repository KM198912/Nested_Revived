
	// Mapper 5 (MMC5)
	//  Step 1 (Hero of Aethel): PRG mode 3, CHR mode 3 ($5120-$5127), CIRAM mirroring.
	//  Step 2 (Castlevania III init): PRG mode 2, audio registers ignored, $5106/$5107
	//  stored, $5200 split off.
	// Not yet: PRG modes 0/1, PRG-RAM, $5128-$512B, $5130 high bits, ExRAM, fill mode,
	// split, scanline IRQ, multiplier, MMC5 audio, PCM IRQ, register reads.
	// Those stop with an exception naming the register, instead of running wrong.

	.pushaddr
		Mapper_Main		5, Mapper5__Main
		Mapper_Init		5, Mapper5__Init

		.addr	=Mapper_Memory
Mapper5_PrgMode:
		.fill	1
Mapper5_ChrMode:
		.fill	1
Mapper5_PrgRamProtect1:
		.fill	1
Mapper5_PrgRamProtect2:
		.fill	1
Mapper5_ExRamMode:
		.fill	1
Mapper5_NameTables:
		.fill	1
Mapper5_PrgRamBank:
		.fill	1
Mapper5_ChrHigh:
		.fill	1
Mapper5_FillTile:
		.fill	1
Mapper5_FillColor:
		.fill	1
Mapper5_PrgRegs:
		// Raw values of $5114-$5117
		.fill	4
Mapper5_LastOther:
		// Last register without a handler that the recompiler saw (for the exception)
		.fill	2
	.pulladdr

	.mx	0x00

Mapper5__Main:
	// Load current bank address
	ldx	#_Mapper5__Main/0x10000
	stx	$.DP_ZeroBank

	// Registers by exact address
	IOPort_Compare	0x5100, bne, Mapper5__5100
	IOPort_Compare	0x5101, bne, Mapper5__5101
	IOPort_Compare	0x5102, bne, Mapper5__5102
	IOPort_Compare	0x5103, bne, Mapper5__5103
	IOPort_Compare	0x5104, bne, Mapper5__5104
	IOPort_Compare	0x5105, bne, Mapper5__5105
	IOPort_Compare	0x5106, bne, Mapper5__5106
	IOPort_Compare	0x5107, bne, Mapper5__5107
	IOPort_Compare	0x5113, bne, Mapper5__5113
	IOPort_Compare	0x5114, bne, Mapper5__5114
	IOPort_Compare	0x5115, bne, Mapper5__5115
	IOPort_Compare	0x5116, bne, Mapper5__5116
	IOPort_Compare	0x5117, bne, Mapper5__5117
	IOPort_Compare	0x5120, bne, Mapper5__5120
	IOPort_Compare	0x5121, bne, Mapper5__5121
	IOPort_Compare	0x5122, bne, Mapper5__5122
	IOPort_Compare	0x5123, bne, Mapper5__5123
	IOPort_Compare	0x5124, bne, Mapper5__5124
	IOPort_Compare	0x5125, bne, Mapper5__5125
	IOPort_Compare	0x5126, bne, Mapper5__5126
	IOPort_Compare	0x5127, bne, Mapper5__5127
	IOPort_Compare	0x5130, bne, Mapper5__5130
	IOPort_Compare	0x5200, bne, Mapper5__5200
	IOPort_Compare	0x5204, bne, Mapper5__5204
	IOPort_Compare	0x5010, bne, Mapper5__5010

	// Other audio registers ($5000-$501F): ignored, MMC5 audio isn't emulated
	tax
	and	#0xffe0
	IOPort_Compare	0x5000, bne, Mapper5__Audio
	txa

	// Any other MMC5 register ($5000-$5FFF) isn't supported yet
	and	#0xf000
	cmp	#0x5000
	bne	$+b_1
		// Remember the address for the exception (the last one compiled is
		// almost always the one that runs into it)
		// (long store: this runs while compiling, DB isn't known)
		txa
		sta	$=Mapper5_LastOther
		ldx	#_Mapper5__OtherRegister
		jmp	$=Recompiler__GetIOAccess_ReturnMapper
b_1:

	IOPort_CompareEnd

	.mx	0x30

	//	-----------------------------------------------------------------------

Mapper5__Init:
	// Matches the exe's starting banks (0, 1, -2, -1) in mode 3
	lda	#0x03
	sta	$_Mapper5_PrgMode
	sta	$_Mapper5_ChrMode
	lda	#0x80
	sta	$_Mapper5_PrgRegs+0
	lda	#0x81
	sta	$_Mapper5_PrgRegs+1
	lda	#0xfe
	sta	$_Mapper5_PrgRegs+2
	lda	#0xff
	sta	$_Mapper5_PrgRegs+3

	IOPort_InitEnd

	//	-----------------------------------------------------------------------

	// Register write entry: stores the value to IO_temp, then A and X are free
	// (8-bit). Loads and indexed stores go to the default: unsupported.
	.macro	Mapper5__Register	name
Mapper5__{0}:
		iIOPort_InterfaceSwitch		Mapper5__Unsupported
			case	iIOPort_sty
				CoreCall_Begin
				CoreCall_ResetMemoryPrefix
				CoreCall_Continue
				php
				sty	$_IO_temp
				bra	$+b_in__
			case	iIOPort_stx
				CoreCall_Begin
				CoreCall_ResetMemoryPrefix
				CoreCall_Continue
				php
				stx	$_IO_temp
				bra	$+b_in__
			case	iIOPort_sta
				CoreCall_Begin
				CoreCall_ResetMemoryPrefix
				CoreCall_Continue
			case	iIOPort_stai
				php
				sta	$_IO_temp
b_in__:
				lock
				xba
				phx
	.endm

	.macro	Mapper5__Exit
		plx
		xba
		plp
		rtl
	.endm

	// Stops with an exception naming the register and the value written
	.macro	Mapper5__Fail	register
		lda	$_IO_temp
		unlock
		trap
		Exception	"MMC5 Feature Unsupported{}{}{}The game wrote 0x{a:X} to {0}, which needs an MMC5 feature that isn't emulated yet.{}{}MMC5 support is work in progress."
	.endm

	//	-----------------------------------------------------------------------

	// Loads, indexed stores and RMW on MMC5 registers
Mapper5__Unsupported:
	unlock
	trap
	Exception	"MMC5 Access Unsupported{}{}{}The game read an MMC5 register, or wrote one with an indexed or read-modify-write instruction. This isn't emulated yet.{}{}MMC5 support is work in progress."

	// Any register without its own handler
Mapper5__OtherRegister:
	iIOPort_InterfaceSwitch		Mapper5__OtherRegister_Trap

Mapper5__OtherRegister_Trap:
	lda	$_Mapper5_LastOther+1
	xba
	lda	$_Mapper5_LastOther
	unlock
	trap
	Exception	"MMC5 Register Unsupported{}{}{}The game accessed an MMC5 register that isn't emulated yet, probably 0x{A:X} (the last one compiled).{}{}MMC5 support is work in progress."

	//	-----------------------------------------------------------------------

	// PRG mapping from mode and raw $5114-$5117, after any write to them.
	// Mode 3: 8 KB banks at $8000/$A000/$C000/$E000 ($5114-$5117).
	// Mode 2: 16 KB at $8000 ($5115, bit 0 ignored), 8 KB at $C000 ($5116), $E000 ($5117).
	// Bit 7 = ROM; PRG-RAM in these ranges isn't supported yet. $E000 is always ROM.
Mapper5__ApplyPrg:
	stz	$_Memory_NesBank

	lda	$_Mapper5_PrgMode
	and	#0x03
	cmp	#0x02
	beq	$+b_mode2

		// Mode 3 (X = register low byte, for the exception)
		ldx	#0x14
		lda	$_Mapper5_PrgRegs+0
		bpl	$+b_ram
		and	#0x7f
		tax
		lda	$=RomInfo_BankLut_80,x
		sta	$_Program_Bank_0+2
		stx	$_Program_BankNum_8000

		ldx	#0x15
		lda	$_Mapper5_PrgRegs+1
		bpl	$+b_ram
		and	#0x7f
		tax
		lda	$=RomInfo_BankLut_a0,x
		sta	$_Program_Bank_1+2
		stx	$_Program_BankNum_a000
		bra	$+b_upper

b_mode2:
		ldx	#0x15
		lda	$_Mapper5_PrgRegs+1
		bpl	$+b_ram
		and	#0x7e
		tax
		lda	$=RomInfo_BankLut_80,x
		sta	$_Program_Bank_0+2
		stx	$_Program_BankNum_8000
		inx
		lda	$=RomInfo_BankLut_a0,x
		sta	$_Program_Bank_1+2
		stx	$_Program_BankNum_a000

b_upper:
	ldx	#0x16
	lda	$_Mapper5_PrgRegs+2
	bpl	$+b_ram
	and	#0x7f
	tax
	lda	$=RomInfo_BankLut_c0,x
	sta	$_Program_Bank_2+2
	stx	$_Program_BankNum_c000

	lda	$_Mapper5_PrgRegs+3
	and	#0x7f
	tax
	lda	$=RomInfo_BankLut_e0,x
	sta	$_Program_Bank_3+2
	stx	$_Program_BankNum_e000
	rts

b_ram:
	// A = register value (bit 7 clear), X = register low byte
	unlock
	trap
	Exception	"MMC5 PRG-RAM Unsupported{}{}{}$51{x:X} = 0x{a:X} maps PRG-RAM into $8000-$DFFF. This isn't emulated yet.{}{}MMC5 support is work in progress."

	//	-----------------------------------------------------------------------

	// PRG mode: modes 2 and 3
	Mapper5__Register	5100
		lda	$_IO_temp
		and	#0x03
		cmp	#0x02
		bcc	$+b_fail
		sta	$_Mapper5_PrgMode
		jsr	$_Mapper5__ApplyPrg
		Mapper5__Exit
b_fail:
		Mapper5__Fail	"$5100 (PRG mode, only modes 2 and 3 are supported)"

	// CHR mode: only mode 3 (8x 1 KB)
	Mapper5__Register	5101
		lda	$_IO_temp
		sta	$_Mapper5_ChrMode
		and	#0x03
		cmp	#0x03
		bne	$+b_fail
		Mapper5__Exit
b_fail:
		Mapper5__Fail	"$5101 (CHR mode, only mode 3 is supported)"

	// Kept, no effect yet: PRG-RAM protection, ExRAM mode, fill tile/color,
	// PRG-RAM bank (PRG-RAM, ExRAM and fill mode aren't emulated)
	.macro	Mapper5__StoreRegister	name, variable
	Mapper5__Register	{0}
		lda	$_IO_temp
		sta	$_{1}
		Mapper5__Exit
	.endm
	Mapper5__StoreRegister	5102, Mapper5_PrgRamProtect1
	Mapper5__StoreRegister	5103, Mapper5_PrgRamProtect2
	Mapper5__StoreRegister	5104, Mapper5_ExRamMode
	Mapper5__StoreRegister	5106, Mapper5_FillTile
	Mapper5__StoreRegister	5107, Mapper5_FillColor
	Mapper5__StoreRegister	5113, Mapper5_PrgRamBank

	// Nametable mapping: CIRAM layouts only, ExRAM and fill mode aren't supported
	Mapper5__Register	5105
		lda	$_IO_temp
		sta	$_Mapper5_NameTables
		ldx	#0x03
b_loop:
			cmp	$=Mapper5__NameTableValues,x
			beq	$+b_found
			dex
			bpl	$-b_loop
		bra	$+b_fail
b_found:
		// Load mirrors: 2x1, 1x2, 1x1, 1x1+
		lda	$=Mapper5__NameTableMirrors,x
		jsr	$=Gfx__NameTableMirrorChange
		Mapper5__Exit
b_fail:
		Mapper5__Fail	"$5105 (nametable mapping, only vertical, horizontal and single screen are supported)"

Mapper5__NameTableValues:
	// Vertical, horizontal, single screen A, single screen B
	.data8	0x44, 0x50, 0x00, 0x55
Mapper5__NameTableMirrors:
	.data8	1, 2, 0, 4

	// PRG bank registers: stored raw, the mapping depends on the mode
	.macro	Mapper5__PrgRegister	name, index
	Mapper5__Register	{0}
		lda	$_IO_temp
		sta	$_Mapper5_PrgRegs+{1}
		jsr	$_Mapper5__ApplyPrg
		Mapper5__Exit
	.endm
	Mapper5__PrgRegister	5114, 0
	Mapper5__PrgRegister	5115, 1
	Mapper5__PrgRegister	5116, 2
	Mapper5__PrgRegister	5117, 3

	// CHR banks, 1 KB each ($5120-$5127 = $0000-$1FFF; 8x8 sprites use the same set)
	.macro	Mapper5__ChrRegister	name, index
	Mapper5__Register	{0}
		lda	$_IO_temp
		sta	$_CHR_{1}_NesBank
		Mapper5__Exit
	.endm
	Mapper5__ChrRegister	5120, 0
	Mapper5__ChrRegister	5121, 1
	Mapper5__ChrRegister	5122, 2
	Mapper5__ChrRegister	5123, 3
	Mapper5__ChrRegister	5124, 4
	Mapper5__ChrRegister	5125, 5
	Mapper5__ChrRegister	5126, 6
	Mapper5__ChrRegister	5127, 7

	// CHR bank high bits: only 0 (up to 256 KB CHR)
	Mapper5__Register	5130
		lda	$_IO_temp
		sta	$_Mapper5_ChrHigh
		and	#0x03
		bne	$+b_fail
		Mapper5__Exit
b_fail:
		Mapper5__Fail	"$5130 (CHR bank high bits, only 0 is supported)"

	// Vertical split: off is fine, enabling isn't supported yet
	Mapper5__Register	5200
		lda	$_IO_temp
		bmi	$+b_fail
		Mapper5__Exit
b_fail:
		Mapper5__Fail	"$5200 (vertical split enable, the split isn't emulated yet)"

	// Scanline IRQ: disabling is fine, enabling isn't supported yet
	Mapper5__Register	5204
		lda	$_IO_temp
		bmi	$+b_fail
		Mapper5__Exit
b_fail:
		Mapper5__Fail	"$5204 (scanline IRQ enable, the MMC5 IRQ isn't emulated yet)"

	// PCM control: ignored like the other audio registers, but the PCM IRQ isn't supported
	Mapper5__Register	5010
		lda	$_IO_temp
		bmi	$+b_fail
		Mapper5__Exit
b_fail:
		Mapper5__Fail	"$5010 (PCM IRQ enable, MMC5 audio isn't emulated)"

	// Other audio registers ($5000-$501F): ignored
	Mapper5__Register	Audio
		Mapper5__Exit

	//	-----------------------------------------------------------------------
