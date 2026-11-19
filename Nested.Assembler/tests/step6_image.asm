	// Step 6 test: .palette / .image with the real Gui.asm lines (v1.8).
	// Gui__Init_CHR sits at C0:40E6 in the template, 1024 bytes.
	//
	// Run from Nested.Assembler\:
	//  NestedAsm tests\step6_image.asm --base reference\v1.8\Assembly -o step6.smc --compare "..\Project Nested\Project_Nested.smc" --range 40e6-44e5
	// Expected: MATCH (1024 bytes)

	.addr	0xc040e6, 0xc0ffff

Gui__Init_CHR:
	.palette	0x800080, 0xffffff, 0x000000, 0x0000ff
	.image   "Project/Font.png", 1, byte
Gui__Init_CHR_end:
