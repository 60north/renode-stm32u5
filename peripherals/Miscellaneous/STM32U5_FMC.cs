//
// Copyright (c) 2026 Fedor Druzhinin
//
// This file is licensed under the MIT License.
//
// STM32U5 flexible static memory controller (FMC) registers, RM0456 Rev 7 section 26.
// The external memories are described by the board as memories at the bank addresses
// (NOR/PSRAM/SRAM bank 1 0x6000 0000, NAND bank 3 0x8000 0000); the controller timing has no
// effect in the emulation. The registers keep their values with the RM0456 reset values for
// STM32Cube HAL_SRAM_Init/HAL_NOR_Init/HAL_NAND_Init. FMC_SR FEMPT reads 1 (FIFO empty).
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class STM32U5_FMC : BasicDoubleWordPeripheral, IKnownSize
    {
        public STM32U5_FMC(IMachine machine) : base(machine)
        {
            for(var bank = 0; bank < NumberOfBanks; bank++)
            {
                RegistersCollection.DefineRegister((long)Registers.ChipSelectControl1 + bank * 8, bank == 0 ? ChipSelectControl1ResetValue : ChipSelectControlResetValue)
                    .WithValueField(0, 32, name: $"FMC_BCR{bank + 1}");
                RegistersCollection.DefineRegister((long)Registers.ChipSelectTiming1 + bank * 8, TimingResetValue)
                    .WithValueField(0, 32, name: $"FMC_BTR{bank + 1}");
                RegistersCollection.DefineRegister((long)Registers.WriteTiming1 + bank * 8, TimingResetValue)
                    .WithValueField(0, 32, name: $"FMC_BWTR{bank + 1}");
            }
            Registers.PsramCounter.Define(this)
                .WithValueField(0, 32, name: "FMC_PCSCNTR");
            Registers.NandControl.Define(this, NandControlResetValue)
                .WithValueField(0, 32, name: "FMC_PCR");
            Registers.NandStatus.Define(this, NandStatusResetValue)
                .WithFlag(0, name: "IRS")
                .WithFlag(1, name: "ILS")
                .WithFlag(2, name: "IFS")
                .WithFlag(3, name: "IREN")
                .WithFlag(4, name: "ILEN")
                .WithFlag(5, name: "IFEN")
                .WithFlag(6, FieldMode.Read, valueProviderCallback: _ => true, name: "FEMPT")
                .WithReservedBits(7, 25);
            Registers.NandCommonSpaceTiming.Define(this, NandSpaceTimingResetValue)
                .WithValueField(0, 32, name: "FMC_PMEM");
            Registers.NandAttributeSpaceTiming.Define(this, NandSpaceTimingResetValue)
                .WithValueField(0, 32, name: "FMC_PATT");
            Registers.NandEcc.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "FMC_ECCR");
        }

        public long Size => 0x400;

        private const int NumberOfBanks = 4;
        private const uint ChipSelectControl1ResetValue = 0x000030DB;
        private const uint ChipSelectControlResetValue = 0x000030D2;
        private const uint TimingResetValue = 0x0FFFFFFF;
        private const uint NandControlResetValue = 0x00000018;
        private const uint NandStatusResetValue = 0x00000040;
        private const uint NandSpaceTimingResetValue = 0xFCFCFCFC;

        private enum Registers
        {
            ChipSelectControl1 = 0x000,
            ChipSelectTiming1 = 0x004,
            PsramCounter = 0x020,
            NandControl = 0x080,
            NandStatus = 0x084,
            NandCommonSpaceTiming = 0x088,
            NandAttributeSpaceTiming = 0x08C,
            NandEcc = 0x094,
            WriteTiming1 = 0x104,
        }
    }
}
