//
// Copyright (c) 2026 Fedor Druzhinin
//
// This file is licensed under the MIT License.
//
// STM32U5 instruction cache (ICACHE) register interface, RM0456 Rev 7 section 8.
// Register layout and bit positions: cmsis-device-u5 v1.4.3 stm32u575xx.h.
//
// The cache itself is functionally transparent and is not simulated. Maintenance operations
// complete immediately:
// - the invalidation started at reset (RM0456 section 8.7.2: ICACHE_SR resets to BUSYF = 1)
//   has finished before software can observe it, so ICACHE_SR reads BSYENDF = 1;
// - CACHEINV, and disabling the cache (which starts an automatic invalidation), set BSYENDF.
// Hit/miss monitors, remapping (CRRx) and error reporting are storage only.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class STM32U5_ICACHE : BasicDoubleWordPeripheral, IKnownSize
    {
        public STM32U5_ICACHE(IMachine machine) : base(machine)
        {
            IRQ = new GPIO();
            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            base.Reset();
            busyEnd.Value = true;
            IRQ.Unset();
        }

        public long Size => 0x400;

        public GPIO IRQ { get; }

        private void DefineRegisters()
        {
            // RM0456 section 8.7.1, reset 0x0000 0004.
            Registers.CR.Define(this, 0x00000004, name: "ICACHE_CR")
                .WithFlag(0, out enable, writeCallback: (oldValue, value) =>
                    {
                        if(oldValue && !value)
                        {
                            CompleteInvalidation();
                        }
                    }, name: "EN")
                .WithFlag(1, FieldMode.Read | FieldMode.Write, valueProviderCallback: _ => false, writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            CompleteInvalidation();
                        }
                    }, name: "CACHEINV")
                .WithFlag(2, out waySelect, name: "WAYSEL")
                .WithReservedBits(3, 13)
                .WithFlag(16, name: "HITMEN")
                .WithFlag(17, name: "MISSMEN")
                .WithFlag(18, FieldMode.Read | FieldMode.Write, valueProviderCallback: _ => false, name: "HITMRST")
                .WithFlag(19, FieldMode.Read | FieldMode.Write, valueProviderCallback: _ => false, name: "MISSMRST")
                .WithReservedBits(20, 12)
                .WithWriteCallback((oldValue, _) =>
                    {
                        // WAYSEL can be written only while the cache is disabled, judged by EN
                        // before this write (RM0456 section 8.7.1).
                        if((oldValue & EnableBit) != 0)
                        {
                            waySelect.Value = (oldValue & WaySelectBit) != 0;
                        }
                    })
            ;

            // RM0456 section 8.7.2, reset 0x0000 0001 (see the note at the top of this file).
            Registers.SR.Define(this, 0x00000000, name: "ICACHE_SR")
                .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => false, name: "BUSYF")
                .WithFlag(1, out busyEnd, FieldMode.Read, name: "BSYENDF")
                .WithFlag(2, out error, FieldMode.Read, name: "ERRF")
                .WithReservedBits(3, 29)
            ;

            // RM0456 section 8.7.3.
            Registers.IER.Define(this, 0x00000000, name: "ICACHE_IER")
                .WithReservedBits(0, 1)
                .WithFlag(1, out busyEndInterruptEnable, name: "BSYENDIE")
                .WithFlag(2, out errorInterruptEnable, name: "ERRIE")
                .WithReservedBits(3, 29)
                .WithChangeCallback((_, __) => UpdateInterrupt())
            ;

            // RM0456 section 8.7.4.
            Registers.FCR.Define(this, 0x00000000, name: "ICACHE_FCR")
                .WithReservedBits(0, 1)
                .WithFlag(1, FieldMode.WriteOneToClear, writeCallback: (_, value) => { if(value) { busyEnd.Value = false; } }, name: "CBSYENDF")
                .WithFlag(2, FieldMode.WriteOneToClear, writeCallback: (_, value) => { if(value) { error.Value = false; } }, name: "CERRF")
                .WithReservedBits(3, 29)
                .WithWriteCallback((_, __) => UpdateInterrupt())
            ;

            // RM0456 sections 8.7.5 and 8.7.6.
            Registers.HMONR.Define(this, 0x00000000, name: "ICACHE_HMONR")
                .WithValueField(0, 32, FieldMode.Read, name: "HITMON")
            ;
            Registers.MMONR.Define(this, 0x00000000, name: "ICACHE_MMONR")
                .WithValueField(0, 16, FieldMode.Read, name: "MISSMON")
                .WithReservedBits(16, 16)
            ;

            // RM0456 section 8.7.7, reset 0x0000 0200 for each region; fields ICACHE_CRRx_*.
            for(var region = 0; region < RegionCount; region++)
            {
                (Registers.CRR0 + region * 4).Define(this, 0x00000200, name: $"ICACHE_CRR{region}")
                    .WithValueField(0, 8, name: "BASEADDR")
                    .WithReservedBits(8, 1)
                    .WithValueField(9, 3, name: "RSIZE")
                    .WithReservedBits(12, 3)
                    .WithFlag(15, name: "REN")
                    .WithValueField(16, 11, name: "REMAPADDR")
                    .WithReservedBits(27, 1)
                    .WithFlag(28, name: "MSTSEL")
                    .WithReservedBits(29, 2)
                    .WithFlag(31, name: "HBURST")
                ;
            }
        }

        private void CompleteInvalidation()
        {
            busyEnd.Value = true;
            UpdateInterrupt();
        }

        private void UpdateInterrupt()
        {
            IRQ.Set((busyEnd.Value && busyEndInterruptEnable.Value) || (error.Value && errorInterruptEnable.Value));
        }

        private IFlagRegisterField enable;
        private IFlagRegisterField waySelect;
        private IFlagRegisterField busyEnd;
        private IFlagRegisterField error;
        private IFlagRegisterField busyEndInterruptEnable;
        private IFlagRegisterField errorInterruptEnable;

        private const int RegionCount = 4;
        private const uint EnableBit = 1u << 0;
        private const uint WaySelectBit = 1u << 2;

        private enum Registers
        {
            CR = 0x00, // ICACHE_CR
            SR = 0x04, // ICACHE_SR
            IER = 0x08, // ICACHE_IER
            FCR = 0x0C, // ICACHE_FCR
            HMONR = 0x10, // ICACHE_HMONR
            MMONR = 0x14, // ICACHE_MMONR
            CRR0 = 0x20, // ICACHE_CRR0
            CRR1 = 0x24, // ICACHE_CRR1
            CRR2 = 0x28, // ICACHE_CRR2
            CRR3 = 0x2C, // ICACHE_CRR3
        }
    }
}
