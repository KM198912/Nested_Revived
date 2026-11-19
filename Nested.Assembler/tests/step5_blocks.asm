	// Step 5 test: blocks, conditions, Game, .nowrite, .addrlow.
	//
	// Run from Nested.Assembler\:
	//  NestedAsm tests\step5_blocks.asm -o step5.smc --vstack
	//
	// Expected: 0 errors, a 19-byte file:
	//  C0:0000  11 44 66 77 A8 58 99 9A          conditions
	//  C0:0008  0B 00 B1  0E 00 B2  11 00 A1     linked list: List, then the entry
	//                                            written before List comes last
	//  C0:0011  00                               terminator from .addrlow
	//  C0:0012  CC                               code after the list continues here
	// --vstack lists no functions (HiddenFunc sits in a .false block).

	.mx		0x30
	.addr	0xc00000, 0xc0ffff

	// .if / .else
	.if 1
	{
		.db	0x11
	}
	.else
	{
		.db	0x22
	}

	.if 0
	{
		.db	0x33
	}
	.else
	{
		.db	0x44
	}

	// Nested, with braces inside a skipped block
	.if 1
	{
		.if 0
		{
			.if 1
			{
				.db	0x55
			}
		}
		.db	0x66
	}

	// Comparisons bind weaker than &: (0x30&0x10) == 0x10
	.if 0x30&0x10 == 0x10
	{
		.db	0x77
	}
	.else
	{
		.db	0x88
	}

	// .ifnamecontains, unquoted and quoted, case-sensitive
	.macro	Use		flags
		.ifnamecontains	{0}, "A8"
		{
			.db	0xa8
		}
		.ifnamecontains	"{0}", "X"
		{
			.db	0x58
		}
	.endm
	Use		"A8nzx"
	Use		"A16XY"

	// .false: nothing inside is defined
.false
{
	.macro	Hidden
		.db	0xee
	.endm

	.func	HiddenFunc
HiddenFunc:
	.db		0xee
}

	// Game: Nested builds as "Empty", 0, 0
	Game "Empty                ", 0x00, 0x00
	.else
	{
		.nowrite
	}

	Game "Other                ", 0x00, 0x00
	{
		.db	0xee
	}
	.else
	{
		.db	0x99
	}

	Game "Empty                ", 0x00, 0x00
	{
		.db	0x9a
	}

	// Linked list built like the Exception list (Macros.asm ExceptionStart/End)
	.macro	Entry	value
		.pushaddr
		.addr	List_WritePointer
		.data16	0
		.data8	{0}
this__:
		.addr	List_WritePointer
		.data16	this__
		.def	List_WritePointer	this__
		.pulladdr
	.endm

	// Before List (like Header.asm before Gui.asm): lands at the end of the list
	Entry	0xa1

List:
	.addrlow	List_WritePointer
	.data8		0
	.def		List_WritePointer		List

	Entry	0xb1
	Entry	0xb2

	.db		0xcc

	// .nowrite: the PC moves on, nothing is written
	.nowrite
	.db		0xee
