//
// Copyright (c) 2026 Fedor Druzhinin
//
// This file is licensed under the MIT License.
//
// STM32U5 debug support MCU (DBGMCU) registers, RM0456 Rev 7 section 75.12.
// Register layout and reset values: STM32U575.svd (STMicroelectronics).
//
// DBGMCU_IDCODE identifies the device; the STM32CubeU5 HAL reads it, for example to select the
// ADC calibration procedure. The default is the SVD reset value: DEV_ID 0x482 (STM32U575/585),
// REV_ID 0x3001 (revision W; RM0456 lists 0x2001 X, 0x3001 W, 0x3007 U).
// The debug configuration and the peripheral freeze registers are storage only: they take
// effect only while a debugger halts the core, which is not modelled. The debug authentication
// registers are tags.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class STM32U5_DBGMCU : BasicDoubleWordPeripheral, IKnownSize
    {
        public STM32U5_DBGMCU(IMachine machine, uint deviceId = DefaultDeviceId, uint revisionId = DefaultRevisionId) : base(machine)
        {
            this.deviceId = deviceId;
            this.revisionId = revisionId;
            DefineRegisters();
            Reset();
        }

        public long Size => 0x1000;

        private void DefineRegisters()
        {
            // Bits 15:12 are reserved and read 0x6 (SVD reset value 0x3001 6482).
            Registers.IDCODE.Define(this, 0x00006000, name: "DBGMCU_IDCODE")
                .WithValueField(0, 12, FieldMode.Read, valueProviderCallback: _ => deviceId, name: "DEV_ID")
                .WithReservedBits(12, 4)
                .WithValueField(16, 16, FieldMode.Read, valueProviderCallback: _ => revisionId, name: "REV_ID")
            ;

            Registers.CR.Define(this, name: "DBGMCU_CR")
                .WithReservedBits(0, 1)
                .WithFlag(1, name: "DBG_STOP")
                .WithFlag(2, name: "DBG_STANDBY")
                .WithReservedBits(3, 1)
                .WithFlag(4, name: "TRACE_IOEN")
                .WithFlag(5, name: "TRACE_EN")
                .WithValueField(6, 2, name: "TRACE_MODE")
                .WithReservedBits(8, 24)
            ;

            // Peripheral freeze bits: storage only (see the note at the top of this file).
            Registers.APB1LFZR.Define(this, name: "DBGMCU_APB1LFZR")
                .WithValueField(0, 32, name: "DBG_APB1L_STOP")
            ;
            Registers.APB1HFZR.Define(this, name: "DBGMCU_APB1HFZR")
                .WithValueField(0, 32, name: "DBG_APB1H_STOP")
            ;
            Registers.APB2FZR.Define(this, name: "DBGMCU_APB2FZR")
                .WithValueField(0, 32, name: "DBG_APB2_STOP")
            ;
            Registers.APB3FZR.Define(this, name: "DBGMCU_APB3FZR")
                .WithValueField(0, 32, name: "DBG_APB3_STOP")
            ;
            Registers.AHB1FZR.Define(this, name: "DBGMCU_AHB1FZR")
                .WithValueField(0, 32, name: "DBG_AHB1_STOP")
            ;
            Registers.AHB3FZR.Define(this, name: "DBGMCU_AHB3FZR")
                .WithValueField(0, 32, name: "DBG_AHB3_STOP")
            ;

            // SVD reset value 0x0000 0001: access port 0 present, not locked.
            Registers.SR.Define(this, 0x00000001, name: "DBGMCU_SR")
                .WithValueField(0, 8, FieldMode.Read, name: "AP_PRESENT")
                .WithValueField(8, 8, FieldMode.Read, name: "AP_LOCKED")
                .WithReservedBits(16, 16)
            ;

            Registers.DBG_AUTH_HOST.Define(this, name: "DBGMCU_DBG_AUTH_HOST")
                .WithTag("AUTH_KEY", 0, 32)
            ;
            Registers.DBG_AUTH_DEVICE.Define(this, name: "DBGMCU_DBG_AUTH_DEVICE")
                .WithTag("AUTH_ID", 0, 32)
            ;

            // Identification registers, SVD reset values.
            DefineConstant(Registers.PIDR4, 0x00000000, "DBGMCU_PIDR4");
            DefineConstant(Registers.PIDR0, 0x00000000, "DBGMCU_PIDR0");
            DefineConstant(Registers.PIDR1, 0x00000000, "DBGMCU_PIDR1");
            DefineConstant(Registers.PIDR2, 0x0000000A, "DBGMCU_PIDR2");
            DefineConstant(Registers.PIDR3, 0x00000000, "DBGMCU_PIDR3");
            DefineConstant(Registers.CIDR0, 0x0000000D, "DBGMCU_CIDR0");
            DefineConstant(Registers.CIDR1, 0x000000F0, "DBGMCU_CIDR1");
            DefineConstant(Registers.CIDR2, 0x00000005, "DBGMCU_CIDR2");
            DefineConstant(Registers.CIDR3, 0x000000B1, "DBGMCU_CIDR3");
        }

        private void DefineConstant(Registers register, uint value, string name)
        {
            register.Define(this, value, name: name)
                .WithValueField(0, 32, FieldMode.Read, name: name)
            ;
        }

        private readonly uint deviceId;
        private readonly uint revisionId;

        private const uint DefaultDeviceId = 0x482;
        private const uint DefaultRevisionId = 0x3001;

        private enum Registers
        {
            IDCODE = 0x00,
            CR = 0x04,
            APB1LFZR = 0x08,
            APB1HFZR = 0x0C,
            APB2FZR = 0x10,
            APB3FZR = 0x14,
            AHB1FZR = 0x20,
            AHB3FZR = 0x28,
            SR = 0xFC,
            DBG_AUTH_HOST = 0x100,
            DBG_AUTH_DEVICE = 0x104,
            PIDR4 = 0xFD0,
            PIDR0 = 0xFE0,
            PIDR1 = 0xFE4,
            PIDR2 = 0xFE8,
            PIDR3 = 0xFEC,
            CIDR0 = 0xFF0,
            CIDR1 = 0xFF4,
            CIDR2 = 0xFF8,
            CIDR3 = 0xFFC,
        }
    }
}
