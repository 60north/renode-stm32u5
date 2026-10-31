//
// Copyright (c) 2026 Fedor Druzhinin
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
//
// STM32U5 true random number generator (RNG), RM0456 Rev 7 section 48.
//
// Adapted from renode-infrastructure
//   src/Emulator/Peripherals/Peripherals/Miscellaneous/STM32_RNG.cs @ 066a7f13 (Renode 1.17.0),
// with its L5 register layout. Changes: no series parameter; RNG_CR reset value 0x0080 0D00
// and ARDIS; the configuration fields stored instead of tagged and frozen by CONFIGLOCK;
// RNG_NSCR stored; RNG_HTCR reset value 0x0000 72AC. Random data come from the emulation's
// random generator; the health tests, clock and seed errors are not modelled.
//
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class STM32U5_RNG : IDoubleWordPeripheral, IKnownSize
    {
        public STM32U5_RNG()
        {
            IRQ = new GPIO();

            var registerMap = new Dictionary<long, DoubleWordRegister>
            {
                {(long)Registers.Control, new DoubleWordRegister(this, ControlResetValue)
                    .WithReservedBits(0, 2)
                    .WithFlag(2, out enable, changeCallback: (_, value) => Update(), name: "RNGEN")
                    .WithFlag(3, out interruptEnable, changeCallback: (_, value) => Update(), name: "IE")
                    .WithReservedBits(4, 1)
                    .WithFlag(5, name: "CED")
                    .WithReservedBits(6, 1)
                    .WithFlag(7, name: "ARDIS")
                    .WithValueField(8, 4, name: "RNG_CONFIG3")
                    .WithFlag(12, name: "NISTC")
                    .WithValueField(13, 3, name: "RNG_CONFIG2")
                    .WithValueField(16, 4, name: "CLKDIV")
                    .WithValueField(20, 6, name: "RNG_CONFIG1")
                    .WithReservedBits(26, 4)
                    .WithFlag(30, name: "CONDRST") //Nothing to do for the emulation
                    .WithFlag(31, out configurationLocked, FieldMode.Read | FieldMode.Set, name: "CONFIGLOCK")
                },
                {(long)Registers.Status, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => enable.Value, name: "DRDY")
                    .WithTag("CECS", 1, 1)
                    .WithTag("SECS", 2, 1)
                    .WithReservedBits(3, 2)
                    .WithTag("CEIS", 5, 1)
                    .WithTag("SEIS", 6, 1)
                    .WithReservedBits(7, 25)
                },
                {(long)Registers.Data, new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Read, valueProviderCallback: _ =>
                {
                    if(enable.Value)
                    {
                        return unchecked((uint)rng.Next());
                    }
                    else
                    {
                        return 0;
                    }
                }, name: "RNDATA")
                },
            };
            registerMap.Add((long)Registers.NoiseSourceControl, new DoubleWordRegister(this, NoiseSourceControlResetValue)
                .WithValueField(0, 18, name: "EN_OSC")
                .WithReservedBits(18, 14));
            {
                registerMap.Add((long)Registers.HealthTestControl, new DoubleWordRegister(this, HealthTestControlResetValue)
                        .WithValueField(0, 32, writeCallback: (previous_value, new_value) =>
                        {
                            if(previous_value != HealthTestControlMagic && new_value != HealthTestControlMagic)
                            {
                                this.Log(LogLevel.Warning, "Magic value 0x{0:X} not written before 0x{1:X}", HealthTestControlMagic, new_value);
                            }
                        }
                        ));
            }
            registers = new DoubleWordRegisterCollection(this, registerMap);
        }

        public void Reset()
        {
            registers.Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            // RM0456 48.7.1: with CONFIGLOCK set, writes to the configuration fields of RNG_CR,
            // RNG_NSCR and RNG_HTCR are ignored until reset.
            if(configurationLocked.Value)
            {
                if(offset == (long)Registers.NoiseSourceControl || offset == (long)Registers.HealthTestControl)
                {
                    this.Log(LogLevel.Warning, "Write to {0} ignored: CONFIGLOCK is set", (Registers)offset);
                    return;
                }
                if(offset == (long)Registers.Control)
                {
                    value = (value & ~ConfigurationBits) | (registers.Read(offset) & ConfigurationBits);
                }
            }
            registers.Write(offset, value);
        }

        public long Size => 0x400;

        public GPIO IRQ { get; private set; }

        private void Update()
        {
            IRQ.Set(enable.Value && interruptEnable.Value);
        }

        private readonly DoubleWordRegisterCollection registers;
        private readonly PseudorandomNumberGenerator rng = EmulationManager.Instance.CurrentEmulation.RandomGenerator;
        private readonly IFlagRegisterField enable;
        private readonly IFlagRegisterField interruptEnable;
        private readonly IFlagRegisterField configurationLocked;

        private const uint HealthTestControlMagic = 0x17590ABC;
        // RM0456 48.7: reset values of RNG_CR, RNG_NSCR and RNG_HTCR.
        private const uint ControlResetValue = 0x00800D00;
        private const uint NoiseSourceControlResetValue = 0x0003FFFF;
        private const uint HealthTestControlResetValue = 0x000072AC;
        // CED, ARDIS, RNG_CONFIG3, NISTC, RNG_CONFIG2, CLKDIV, RNG_CONFIG1.
        private const uint ConfigurationBits = 0x03FFFFA0;

        private enum Registers
        {
            Control = 0x0,
            Status = 0x4,
            Data = 0x8,
            NoiseSourceControl = 0xC,
            HealthTestControl = 0x10,
        }
    }
}
