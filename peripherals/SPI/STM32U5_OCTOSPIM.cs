//
// Copyright (c) 2026 Fedor Druzhinin
//
// This file is licensed under the MIT License.
//
// STM32U5 Octo-SPI I/O manager (OCTOSPIM), RM0456 Rev 7 section 29. The pin multiplexing has
// no effect in the emulation: each OCTOSPI drives the memory registered to it. The registers
// keep their values (OCTOSPIM_CR reset 0x0000 0000, OCTOSPIM_P1CR 0x0301 0111,
// OCTOSPIM_P2CR 0x0705 0333) for STM32Cube HAL_OSPIM_Config.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.SPI
{
    public class STM32U5_OCTOSPIM : BasicDoubleWordPeripheral, IKnownSize
    {
        public STM32U5_OCTOSPIM(IMachine machine) : base(machine)
        {
            Registers.Control.Define(this)
                .WithFlag(0, name: "MUXEN")
                .WithReservedBits(1, 15)
                .WithValueField(16, 8, name: "REQ2ACK_TIME")
                .WithReservedBits(24, 8);
            Registers.Port1Configuration.Define(this, Port1ConfigurationResetValue)
                .WithValueField(0, 32, name: "P1CR");
            Registers.Port2Configuration.Define(this, Port2ConfigurationResetValue)
                .WithValueField(0, 32, name: "P2CR");
        }

        public long Size => 0x400;

        private const uint Port1ConfigurationResetValue = 0x03010111;
        private const uint Port2ConfigurationResetValue = 0x07050333;

        private enum Registers
        {
            Control = 0x0,
            Port1Configuration = 0x4,
            Port2Configuration = 0x8,
        }
    }
}
