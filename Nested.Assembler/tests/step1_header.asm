	// Step 1 test: Header.asm (v1.8) with the parts that need later steps written out by hand:
	//  call X       -> jsr $=X       (all callers here are in another bank, so jsl)
	//  unlock       -> cli
	//  trap         -> bra to itself
	//  Exception    -> left out (writes into the exception list, not inline)
	//  .func/.vstack -> left out (no bytes)
	// Symbols from other files are .def'd with their addresses in the v1.8 template.
	//
	// Run from Nested.Assembler\:
	//  NestedAsm tests\step1_header.asm -o step1.smc --compare "..\Project Nested\Project_Nested.smc" --range ff00-ff6f,ffc0-ffd5,ffd7-ffd8,ffdb,ffe4-ffff
	// Expected: MATCH

	.def	RomInfo_Description		0xc006e4
	.def	Main					0x818002
	.def	StaticRec__Init			0x81e78d
	.def	StaticRec__Main			0x81e8a7
	.def	RomInfo_DebugCalls		0xc00613
	.def	Start__Irq_Fast			0x818612
	.def	INIT_STACK_TOP			0x09ff
	.def	VSTACK_PAGE				0x1e00

	// ROM title
	.addr	0x80ffc0
Rom_Title:
	.def	Rom_Title	Rom_Title&0x7fffff
	.string	"Nested Emulator      "

	// ROM mapper: fast HiROM
	[80ffd5] = 0x31

	// ROM size 8mb
	[80ffd7] = 0x0d

	// SRAM size 256kb
	[80ffd8] = 0x08

	// Version (RomInfo.asm), mixed-case bare hex address
	[80ffdB] = 0x08

	// CPU execution vectors
	.addr	0x80ffe4
	.data16	_Start__Cop
	.data16	_Start__Brk
	.data16	_Start__Abort
	.data16	_Start__Nmi
	.data16	_Start__UnusedReset
	.data16	_Start__Irq
	.data16	0, 0
	.data16	_Start__Lock, _Start__Lock, _Start__Lock, _Start__Lock
	.data16 _Start__Reset
	.data16	_Start__Lock

	.addr	0x80ff00, 0x80ffbf

	.data24		=RomInfo_Description

Start__Abort:
Start__Brk:
Start__Cop:
Start__Lock:
	jmp	$=Start__Lock_1
Start__Lock_1:
	rep	#0x30
	.mx	0x00

	tsc
	and	#0xe000
	beq	$+b_1
		lda	#0x01ff
		tcs
b_1:

	cli

	lda	$2,s
	tax
	dex
	dex
	lda	$4,s

trap__:
	bra	$-trap__


Start__Reset:
	clc
	xce
	sep	#0x35
	rep	#0x18
	.mx	0x20

	ldx	#_INIT_STACK_TOP
	txs

	phk
	plb

	rol	$0x420d

	lda	$_Start__Reset_DummyWrite+1
	inc	$_Start__Reset_DummyWrite+1
Start__Reset_DummyWrite:
	eor	#0
	bne	$+Start__UnusedReset

	jmp	$=Main


Start__UnusedReset:
	clc
	xce
	rep	#0x38
	.mx	0x00

	ldx	#_INIT_STACK_TOP
	txs

	lda	#_VSTACK_PAGE
	tcd

	bcc	$+Start__UnusedReset_Live
		jsr	$=StaticRec__Init
		stp
Start__UnusedReset_Live:
	jsr	$=StaticRec__Init
	jsr	$=StaticRec__Main
	sep	#0x20
	.mx	0x20
	lda	#0
	sta	$=RomInfo_DebugCalls

	ldx	#_INIT_STACK_TOP
	txs
	jmp	$=Main


Start__Nmi:
	jmp	$=Start__Nmi_1
Start__Nmi_1:
	cli
trap__:
	bra	$-trap__

Start__Irq:
	jmp	$=Start__Irq_Fast

	.finalsize	0x050000
