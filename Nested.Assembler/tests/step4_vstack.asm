	// Step 4 test: vstack, call/return, parameters.
	// Rebuilds the cases measured in the v1.8 template (README "Vstack rules")
	// as small functions. Bases depend on every caller in the whole tree, so the
	// real byte comparison comes with the full-tree run; this file checks the rules.
	//
	// Run from Nested.Assembler\:
	//  NestedAsm tests\step4_vstack.asm -o step4.smc --vstack
	//
	// Expected --vstack output (template case in brackets):
	//  Frame        base 1E40, no callers, return = rts
	//  CalcCpu      base 1E40 (called before Frame's .local)        [Gui__CalcCpuPercent $40]
	//  GetRamUsed   base 1E42 (after .local + .unlocal _temp)       [Gui__GetRamUsed $42]
	//  GetRamTotal  base 1E46 (allocated: _temp 2 + _used/_total 4, freed ones count)
	//  Dict         base 1E46, params list=1E46 compareString=1E49 length=1E4C,
	//               local firstChar=1E4D                            [Dict__FindElement $85..$8C]
	//  Shared       called from C1 and C2 -> jsl from both, rtl     [Memory__AllocInBank]
	//  Far          in bank C2, called from C1 -> jsl, rtl
	//
	// Expected bytes:
	//  Frame:        sta $.temp = 85 40, sta $.used = 85 40, sta $.total = 85 42,
	//                stx $.Param_list = 86 46, call CalcCpu/GetRamUsed/GetRamTotal/Dict = 20 xx xx,
	//                call Shared = 22 xx xx C1, call Far = 22 xx xx C2, return = 60
	//  GetRamUsed:   sta $.count__ = 85 42, sta $.index__ = 85 46
	//  Dict:         lda $.length = A5 4C, sta $.firstChar = 85 4D

	.mx	0x00
	.addr	0xc10000, 0xc1ffff

	.func	Frame
Frame:
	.vstack	0x1e40

	call	CalcCpu
	.local	_temp
	sta	$.temp
	.unlocal	_temp

	call	GetRamUsed
	.local	_used, _total
	sta	$.used

	call	GetRamTotal
	sta	$.total
	.unlocal	_used, _total

	.precall	Dict		=list, =compareString, .length
	stx	$.Param_list
	call

	call	Shared
	call	Far
	return


	.func	CalcCpu
CalcCpu:
	.local	_low, _high
	sta	$.low
	stz	$.high
	return


	.func	GetRamUsed
GetRamUsed:
	.local	4 count__, _index__
	.local	_temp__
	sta	$.count__
	sta	$.index__
	sta	$.temp__
	return


	.func	GetRamTotal
GetRamTotal:
	lda	#0x0080
	return


	.func	Dict	=list, =compareString, .length
Dict:
	lda	$.length
	.local	.firstChar
	sta	$.firstChar
	return


	.func	Shared
Shared:
	nop
	return


	.addr	0xc20000, 0xc2ffff

	.func	Far
Far:
	call	Shared
	return
