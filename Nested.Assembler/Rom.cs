namespace Nested.Assembler
{
    // Output image in fast HiROM layout (header byte 0x31)
    sealed class Rom
    {
        public const int MaxSize = 0x400000;

        public readonly byte[] Data = new byte[MaxSize];
        // SourceLine.Id of the last statement that wrote each byte (0 = never written)
        public readonly int[] Origin = new int[MaxSize];
        public int HighWater;

        // SNES address -> file offset, or -1 when the address isn't ROM
        // (WRAM, I/O, SRAM, low halves of LoROM banks). Writes there are dropped,
        // which is how Variables.asm can lay out RAM with labels and .fill.
        public static long Map(long address)
        {
            int bank = (int)(address >> 16) & 0xff;
            int offset = (int)(address & 0xffff);

            if (bank == 0x7e || bank == 0x7f)
                return -1;
            if ((bank & 0x40) != 0)                 // 40-7d, c0-ff: whole bank
                return ((long)(bank & 0x3f) << 16) | (long)offset;
            if (offset >= 0x8000)                   // 00-3f, 80-bf: upper half
                return ((long)(bank & 0x3f) << 16) | (long)offset;
            return -1;
        }

        public void Write(long fileOffset, byte value, int origin)
        {
            Data[fileOffset] = value;
            Origin[fileOffset] = origin;
            if (fileOffset + 1 > HighWater)
                HighWater = (int)fileOffset + 1;
        }
    }
}
