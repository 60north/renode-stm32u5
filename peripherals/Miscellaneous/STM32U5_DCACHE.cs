//
// Copyright (c) 2026 Fedor Druzhinin
//
// This file is licensed under the MIT License.
//
// STM32U5 data cache (DCACHE1) register interface, RM0456 Rev 7 section 9.
// Register layout and bit positions: cmsis-device-u5 v1.4.3 stm32u575xx.h.
//
// As STM32U5_ICACHE: the cache is functionally transparent (the bus masters see the memory
// directly) and is not simulated. Maintenance operations complete immediately:
// - the invalidation started at reset (DCACHE_SR resets to BUSYF = 1) has finished before
//   software can observe it, so DCACHE_SR reads BSYENDF = 1;
// - CACHEINV and disabling the cache (automatic invalidation) set BSYENDF;
// - a range command (STARTCMD with CACHECMD clean, invalidate or clean and invalidate) sets
//   CMDENDF; there is nothing to write back.
// Hit/miss monitors read 0; the command range registers are storage only.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class STM32U5_DCACHE : BasicDoubleWordPeripheral, IKnownSize
    {
        public STM32U5_DCACHE(IMachine machine) : base(machine)
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
            // RM0456 section 9.6.1, reset 0x0000 0000.
            Registers.CR.Define(this, 0x00000000, name: "DCACHE_CR")
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
                .WithReservedBits(2, 6)
                .WithValueField(8, 3, out command, name: "CACHECMD")
                .WithFlag(11, FieldMode.Read | FieldMode.Write, valueProviderCallback: _ => false, writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            CompleteCommand();
                        }
                    }, name: "STARTCMD")
                .WithReservedBits(12, 4)
                .WithFlag(16, name: "RHITMEN")
                .WithFlag(17, name: "RMISSMEN")
                .WithFlag(18, FieldMode.Read | FieldMode.Write, valueProviderCallback: _ => false, name: "RHITMRST")
                .WithFlag(19, FieldMode.Read | FieldMode.Write, valueProviderCallback: _ => false, name: "RMISSMRST")
                .WithFlag(20, name: "WHITMEN")
                .WithFlag(21, name: "WMISSMEN")
                .WithFlag(22, FieldMode.Read | FieldMode.Write, valueProviderCallback: _ => false, name: "WHITMRST")
                .WithFlag(23, FieldMode.Read | FieldMode.Write, valueProviderCallback: _ => false, name: "WMISSMRST")
                .WithReservedBits(24, 7)
                .WithFlag(31, name: "HBURST")
            ;

            // RM0456 section 9.6.2, reset 0x0000 0001 (see the note at the top of this file).
            Registers.SR.Define(this, 0x00000000, name: "DCACHE_SR")
                .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => false, name: "BUSYF")
                .WithFlag(1, out busyEnd, FieldMode.Read, name: "BSYENDF")
                .WithFlag(2, out error, FieldMode.Read, name: "ERRF")
                .WithFlag(3, FieldMode.Read, valueProviderCallback: _ => false, name: "BUSYCMDF")
                .WithFlag(4, out commandEnd, FieldMode.Read, name: "CMDENDF")
                .WithReservedBits(5, 27)
            ;

            // RM0456 section 9.6.3.
            Registers.IER.Define(this, 0x00000000, name: "DCACHE_IER")
                .WithReservedBits(0, 1)
                .WithFlag(1, out busyEndInterruptEnable, name: "BSYENDIE")
                .WithFlag(2, out errorInterruptEnable, name: "ERRIE")
                .WithReservedBits(3, 1)
                .WithFlag(4, out commandEndInterruptEnable, name: "CMDENDIE")
                .WithReservedBits(5, 27)
                .WithChangeCallback((_, __) => UpdateInterrupt())
            ;

            // RM0456 section 9.6.4.
            Registers.FCR.Define(this, 0x00000000, name: "DCACHE_FCR")
                .WithReservedBits(0, 1)
                .WithFlag(1, FieldMode.WriteOneToClear, writeCallback: (_, value) => { if(value) { busyEnd.Value = false; } }, name: "CBSYENDF")
                .WithFlag(2, FieldMode.WriteOneToClear, writeCallback: (_, value) => { if(value) { error.Value = false; } }, name: "CERRF")
                .WithReservedBits(3, 1)
                .WithFlag(4, FieldMode.WriteOneToClear, writeCallback: (_, value) => { if(value) { commandEnd.Value = false; } }, name: "CCMDENDF")
                .WithReservedBits(5, 27)
                .WithWriteCallback((_, __) => UpdateInterrupt())
            ;

            // RM0456 sections 9.6.5 to 9.6.8.
            Registers.RHMONR.Define(this, 0x00000000, name: "DCACHE_RHMONR")
                .WithValueField(0, 32, FieldMode.Read, name: "RHITMON")
            ;
            Registers.RMMONR.Define(this, 0x00000000, name: "DCACHE_RMMONR")
                .WithValueField(0, 16, FieldMode.Read, name: "RMISSMON")
                .WithReservedBits(16, 16)
            ;
            Registers.WHMONR.Define(this, 0x00000000, name: "DCACHE_WHMONR")
                .WithValueField(0, 32, FieldMode.Read, name: "WHITMON")
            ;
            Registers.WMMONR.Define(this, 0x00000000, name: "DCACHE_WMMONR")
                .WithValueField(0, 16, FieldMode.Read, name: "WMISSMON")
                .WithReservedBits(16, 16)
            ;

            // RM0456 sections 9.6.9 and 9.6.10: 16-byte aligned addresses.
            Registers.CMDRSADDRR.Define(this, 0x00000000, name: "DCACHE_CMDRSADDRR")
                .WithReservedBits(0, 4)
                .WithValueField(4, 28, name: "CMDSTARTADDR")
            ;
            Registers.CMDREADDRR.Define(this, 0x00000000, name: "DCACHE_CMDREADDRR")
                .WithReservedBits(0, 4)
                .WithValueField(4, 28, name: "CMDENDADDR")
            ;
        }

        private void CompleteInvalidation()
        {
            busyEnd.Value = true;
            UpdateInterrupt();
        }

        private void CompleteCommand()
        {
            if(command.Value == 0)
            {
                return;
            }
            commandEnd.Value = true;
            UpdateInterrupt();
        }

        private void UpdateInterrupt()
        {
            IRQ.Set((busyEnd.Value && busyEndInterruptEnable.Value) || (error.Value && errorInterruptEnable.Value)
                || (commandEnd.Value && commandEndInterruptEnable.Value));
        }

        private IFlagRegisterField enable;
        private IValueRegisterField command;
        private IFlagRegisterField busyEnd;
        private IFlagRegisterField error;
        private IFlagRegisterField commandEnd;
        private IFlagRegisterField busyEndInterruptEnable;
        private IFlagRegisterField errorInterruptEnable;
        private IFlagRegisterField commandEndInterruptEnable;

        private enum Registers
        {
            CR = 0x00, // DCACHE_CR
            SR = 0x04, // DCACHE_SR
            IER = 0x08, // DCACHE_IER
            FCR = 0x0C, // DCACHE_FCR
            RHMONR = 0x10, // DCACHE_RHMONR
            RMMONR = 0x14, // DCACHE_RMMONR
            WHMONR = 0x20, // DCACHE_WHMONR
            WMMONR = 0x24, // DCACHE_WMMONR
            CMDRSADDRR = 0x28, // DCACHE_CMDRSADDRR
            CMDREADDRR = 0x2C, // DCACHE_CMDREADDRR
        }
    }
}
