//
// Copyright (c) 2026 Fedor Druzhinin
// Copyright (c) 2010-2023 Antmicro
//
// This file is licensed under the MIT License.
//
// STM32U5 power control (PWR), RM0456 Rev 7 section 10.
//
// Structure and the CSSF/CWUF handling are adapted from renode-infrastructure
//   src/Emulator/Peripherals/Peripherals/Miscellaneous/STM32WBA_PWR.cs @ 7ea0541f8d9b
// Register layout and bit positions: cmsis-device-u5 v1.4.3 stm32u575xx.h.
// Reset values: RM0456 Rev 7 (variant STM32U575).
//
// Limitations:
// - Voltage scaling (VOSRDY, ACTVOSRDY) and the EPOD booster (BOOSTRDY) complete immediately.
// - Low-power modes, wakeup sources, PVD/PVM and supply-ready flags are not modelled.
// - TrustZone and privilege (RM0456 section 10.8, Table 109): with trustZoneEnabled, the bits of
//   the functions selected by PWR_SECCFGR, PWR_UCPDR while UCPD1 is secure in the GTZC TZSC,
//   VOS and BOOSTEN while RCC_SECCFGR.SYSCLKSEC is set, and the PWR_PUCRx/PDCRx bits of the
//   secure GPIO pins read as 0 and ignore writes for non-secure accesses, without an event. A
//   non-secure PWR_SECCFGR write is reported to the TZIC; with TZEN = 0 PWR_SECCFGR is RAZ/WI.
//   PWR_PRIVCFGR SPRIV/NSPRIV apply the same to unprivileged accesses to the secure/non-secure
//   bits; the status registers and VOSRDY/BOOSTRDY stay readable. The RCC and the GPIO ports
//   are found by their platform names (sysbus.rcc, sysbus.gpioPortA...).
//

using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.GPIOPort;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class STM32U5_PWR : BasicDoubleWordPeripheral, IKnownSize, ISTM32U5_TrustZoneAware
    {
        public STM32U5_PWR(IMachine machine, bool trustZoneEnabled = false, STM32U5_GTZC_TZIC illegalAccessController = null,
            STM32U5_GTZC_TZSC tzsc1 = null) : base(machine)
        {
            sysbus = machine.GetSystemBus(this);
            this.trustZoneEnabled = trustZoneEnabled;
            this.illegalAccessController = illegalAccessController;
            this.tzsc1 = tzsc1;
            DefineRegisters();
            Reset();
        }

        public override uint ReadDoubleWord(long offset)
        {
            var denied = DeniedBits(offset, false);
            return denied == AllBits ? 0 : base.ReadDoubleWord(offset) & ~denied;
        }

        public override void WriteDoubleWord(long offset, uint value)
        {
            var denied = DeniedBits(offset, true);
            if(denied == AllBits)
            {
                return;
            }
            if(denied != 0)
            {
                // CSSF and CWUFx clear on written ones; the other bits keep their value.
                var clearOnWrite = offset == (long)Registers.SR || offset == (long)Registers.WUSCR;
                value = (value & ~denied) | (clearOnWrite ? 0 : base.ReadDoubleWord(offset) & denied);
            }
            base.WriteDoubleWord(offset, value);
        }

        public long Size => 0x400;

        // RM0456 section 10.8.1: the PWR clock and reset bits of the RCC are secure as soon as one
        // PWR function is secure.
        public bool HasSecureFunction => trustZoneEnabled && base.ReadDoubleWord((long)Registers.SECCFGR) != 0;

        // Used by STM32U5_RCC to protect RCC_BDCR.
        public bool BackupDomainWriteEnabled => backupDomainWriteEnable.Value;

        // VOS[1:0]: 0 = range 4 ... 3 = range 1.
        public ulong VoltageScalingRange => voltageScaling.Value;

        private void DefineRegisters()
        {
            // RM p.443, reset 0x00000000
            Registers.CR1.Define(this, 0x00000000, name: "PWR_CR1")
                .WithValueField(0, 3, name: "LPMS")
                .WithReservedBits(3, 2)
                .WithFlag(5, name: "RRSB1")
                .WithFlag(6, name: "RRSB2")
                .WithFlag(7, name: "ULPMEN")
                .WithFlag(8, name: "SRAM1PD")
                .WithFlag(9, name: "SRAM2PD")
                .WithFlag(10, name: "SRAM3PD")
                .WithFlag(11, name: "SRAM4PD")
                .WithReservedBits(12, 20)
            ;

            // RM p.445, reset 0x00000000
            Registers.CR2.Define(this, 0x00000000, name: "PWR_CR2")
                .WithFlag(0, name: "SRAM1PDS1")
                .WithFlag(1, name: "SRAM1PDS2")
                .WithFlag(2, name: "SRAM1PDS3")
                .WithReservedBits(3, 1)
                .WithFlag(4, name: "SRAM2PDS1")
                .WithFlag(5, name: "SRAM2PDS2")
                .WithFlag(6, name: "SRAM4PDS")
                .WithReservedBits(7, 1)
                .WithFlag(8, name: "ICRAMPDS")
                .WithFlag(9, name: "DC1RAMPDS")
                .WithFlag(10, name: "DMA2DRAMPDS")
                .WithFlag(11, name: "PRAMPDS")
                .WithReservedBits(12, 1)
                .WithFlag(13, name: "SRAM4FWU")
                .WithFlag(14, name: "FLASHFWU")
                .WithReservedBits(15, 1)
                .WithFlag(16, name: "SRAM3PDS1")
                .WithFlag(17, name: "SRAM3PDS2")
                .WithFlag(18, name: "SRAM3PDS3")
                .WithFlag(19, name: "SRAM3PDS4")
                .WithFlag(20, name: "SRAM3PDS5")
                .WithFlag(21, name: "SRAM3PDS6")
                .WithFlag(22, name: "SRAM3PDS7")
                .WithFlag(23, name: "SRAM3PDS8")
                .WithReservedBits(24, 7)
                .WithFlag(31, name: "SRDRUN")
            ;

            // RM0456 section 10.10.3, p.449. Power-on reset value 0x0000 0000.
            Registers.CR3.Define(this, 0x00000000, name: "PWR_CR3")
                .WithReservedBits(0, 1)
                .WithFlag(1, out regulatorSelection, name: "REGSEL")
                .WithFlag(2, name: "FSTEN")
                .WithReservedBits(3, 29)
            ;

            // RM0456 section 10.10.4, p.449, reset 0x0000 8000 (range 4, VOSRDY set).
            // Simplification: the new voltage range and the EPOD booster are ready immediately.
            Registers.VOSR.Define(this, 0x00008000, name: "PWR_VOSR")
                .WithReservedBits(0, 14)
                .WithFlag(14, FieldMode.Read, valueProviderCallback: _ => boosterEnable.Value, name: "BOOSTRDY")
                .WithFlag(15, FieldMode.Read, valueProviderCallback: _ => true, name: "VOSRDY")
                .WithValueField(16, 2, out voltageScaling, name: "VOS")
                .WithFlag(18, out boosterEnable, name: "BOOSTEN")
                .WithReservedBits(19, 13)
            ;

            // RM p.451, reset 0x00000000
            Registers.SVMCR.Define(this, 0x00000000, name: "PWR_SVMCR")
                .WithReservedBits(0, 4)
                .WithFlag(4, name: "PVDE")
                .WithValueField(5, 3, name: "PVDLS")
                .WithReservedBits(8, 16)
                .WithFlag(24, name: "UVMEN")
                .WithFlag(25, name: "IO2VMEN")
                .WithFlag(26, name: "AVM1EN")
                .WithFlag(27, name: "AVM2EN")
                .WithFlag(28, name: "USV")
                .WithFlag(29, name: "IO2SV")
                .WithFlag(30, name: "ASV")
                .WithReservedBits(31, 1)
            ;

            // RM p.452, reset 0x00000000
            Registers.WUCR1.Define(this, 0x00000000, name: "PWR_WUCR1")
                .WithFlag(0, name: "WUPEN1")
                .WithFlag(1, name: "WUPEN2")
                .WithFlag(2, name: "WUPEN3")
                .WithFlag(3, name: "WUPEN4")
                .WithFlag(4, name: "WUPEN5")
                .WithFlag(5, name: "WUPEN6")
                .WithFlag(6, name: "WUPEN7")
                .WithFlag(7, name: "WUPEN8")
                .WithReservedBits(8, 24)
            ;

            // RM p.453, reset 0x00000000
            Registers.WUCR2.Define(this, 0x00000000, name: "PWR_WUCR2")
                .WithFlag(0, name: "WUPP1")
                .WithFlag(1, name: "WUPP2")
                .WithFlag(2, name: "WUPP3")
                .WithFlag(3, name: "WUPP4")
                .WithFlag(4, name: "WUPP5")
                .WithFlag(5, name: "WUPP6")
                .WithFlag(6, name: "WUPP7")
                .WithFlag(7, name: "WUPP8")
                .WithReservedBits(8, 24)
            ;

            // RM p.455, reset 0x00000000
            Registers.WUCR3.Define(this, 0x00000000, name: "PWR_WUCR3")
                .WithValueField(0, 2, name: "WUSEL1")
                .WithValueField(2, 2, name: "WUSEL2")
                .WithValueField(4, 2, name: "WUSEL3")
                .WithValueField(6, 2, name: "WUSEL4")
                .WithValueField(8, 2, name: "WUSEL5")
                .WithValueField(10, 2, name: "WUSEL6")
                .WithValueField(12, 2, name: "WUSEL7")
                .WithValueField(14, 2, name: "WUSEL8")
                .WithReservedBits(16, 16)
            ;

            // RM p.456, reset 0x00000000
            Registers.BDCR1.Define(this, 0x00000000, name: "PWR_BDCR1")
                .WithFlag(0, name: "BREN")
                .WithReservedBits(1, 3)
                .WithFlag(4, name: "MONEN")
                .WithReservedBits(5, 27)
            ;

            // RM p.457, reset 0x00000000
            Registers.BDCR2.Define(this, 0x00000000, name: "PWR_BDCR2")
                .WithFlag(0, name: "VBE")
                .WithFlag(1, name: "VBRS")
                .WithReservedBits(2, 30)
            ;

            // RM0456 section 10.10.11. Write access to the backup domain (RCC_BDCR and others).
            Registers.DBPR.Define(this, 0x00000000, name: "PWR_DBPR")
                .WithFlag(0, out backupDomainWriteEnable, name: "DBP")
                .WithReservedBits(1, 31)
            ;

            // RM p.458, reset 0x00000000
            Registers.UCPDR.Define(this, 0x00000000, name: "PWR_UCPDR")
                .WithFlag(0, name: "UCPD_DBDIS")
                .WithFlag(1, name: "UCPD_STDBY")
                .WithReservedBits(2, 30)
            ;

            // RM p.459, reset 0x00000000
            Registers.SECCFGR.Define(this, 0x00000000, name: "PWR_SECCFGR")
                .WithFlag(0, name: "WUP1SEC")
                .WithFlag(1, name: "WUP2SEC")
                .WithFlag(2, name: "WUP3SEC")
                .WithFlag(3, name: "WUP4SEC")
                .WithFlag(4, name: "WUP5SEC")
                .WithFlag(5, name: "WUP6SEC")
                .WithFlag(6, name: "WUP7SEC")
                .WithFlag(7, name: "WUP8SEC")
                .WithReservedBits(8, 4)
                .WithFlag(12, name: "LPMSEC")
                .WithFlag(13, name: "VDMSEC")
                .WithFlag(14, name: "VBSEC")
                .WithFlag(15, name: "APCSEC")
                .WithReservedBits(16, 16)
            ;

            // RM p.461, reset 0x00000000
            Registers.PRIVCFGR.Define(this, 0x00000000, name: "PWR_PRIVCFGR")
                .WithFlag(0, out securePrivileged, name: "SPRIV")
                .WithFlag(1, out nonSecurePrivileged, name: "NSPRIV")
                .WithReservedBits(2, 30)
            ;

            // RM0456 section 10.10.15. CSSF clears STOPF and SBF (as in STM32WBA_PWR).
            Registers.SR.Define(this, 0x00000000, name: "PWR_SR")
                .WithFlag(0, FieldMode.WriteOneToClear, name: "CSSF",
                    writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            standbyFlag.Value = false;
                            stopFlag.Value = false;
                        }
                    })
                .WithFlag(1, out stopFlag, FieldMode.Read, name: "STOPF")
                .WithFlag(2, out standbyFlag, FieldMode.Read, name: "SBF")
                .WithReservedBits(3, 29)
            ;

            // RM0456 section 10.10.16, reset 0x0000 8000. ACTVOS follows VOS immediately (see VOSR).
            // Supply-ready flags are not modelled and read as 0.
            Registers.SVMSR.Define(this, 0x00008000, name: "PWR_SVMSR")
                .WithReservedBits(0, 1)
                .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => regulatorSelection.Value, name: "REGS")
                .WithReservedBits(2, 2)
                .WithFlag(4, FieldMode.Read, valueProviderCallback: _ => false, name: "PVDO")
                .WithReservedBits(5, 10)
                .WithFlag(15, FieldMode.Read, valueProviderCallback: _ => true, name: "ACTVOSRDY")
                .WithValueField(16, 2, FieldMode.Read, valueProviderCallback: _ => voltageScaling.Value, name: "ACTVOS")
                .WithReservedBits(18, 6)
                .WithFlag(24, FieldMode.Read, valueProviderCallback: _ => false, name: "VDDUSBRDY")
                .WithFlag(25, FieldMode.Read, valueProviderCallback: _ => false, name: "VDDIO2RDY")
                .WithFlag(26, FieldMode.Read, valueProviderCallback: _ => false, name: "VDDA1RDY")
                .WithFlag(27, FieldMode.Read, valueProviderCallback: _ => false, name: "VDDA2RDY")
                .WithReservedBits(28, 4)
            ;

            // RM p.463, reset 0x00000000
            Registers.BDSR.Define(this, 0x00000000, name: "PWR_BDSR")
                .WithReservedBits(0, 1)
                .WithFlag(1, FieldMode.Read, name: "VBATH")
                .WithFlag(2, FieldMode.Read, name: "TEMPL")
                .WithFlag(3, FieldMode.Read, name: "TEMPH")
                .WithReservedBits(4, 28)
            ;

            // RM0456 section 10.10.18.
            Registers.WUSR.Define(this, 0x00000000, name: "PWR_WUSR")
                .WithFlags(0, 8, out wakeupFlags, FieldMode.Read, name: "WUF")
                .WithReservedBits(8, 24)
            ;

            // RM0456 section 10.10.19. Clearing as in STM32WBA_PWR.
            Registers.WUSCR.Define(this, 0x00000000, name: "PWR_WUSCR")
                .WithFlags(0, 8, FieldMode.WriteOneToClear, name: "CWUF",
                    writeCallback: (index, _, value) =>
                    {
                        if(value)
                        {
                            wakeupFlags[index].Value = false;
                        }
                    })
                .WithReservedBits(8, 24)
            ;

            // RM p.466, reset 0x00000000
            Registers.APCR.Define(this, 0x00000000, name: "PWR_APCR")
                .WithFlag(0, name: "APC")
                .WithReservedBits(1, 31)
            ;

            // RM p.466, reset 0x00000000
            Registers.PUCRA.Define(this, 0x00000000, name: "PWR_PUCRA")
                .WithFlag(0, name: "PU0")
                .WithFlag(1, name: "PU1")
                .WithFlag(2, name: "PU2")
                .WithFlag(3, name: "PU3")
                .WithFlag(4, name: "PU4")
                .WithFlag(5, name: "PU5")
                .WithFlag(6, name: "PU6")
                .WithFlag(7, name: "PU7")
                .WithFlag(8, name: "PU8")
                .WithFlag(9, name: "PU9")
                .WithFlag(10, name: "PU10")
                .WithFlag(11, name: "PU11")
                .WithFlag(12, name: "PU12")
                .WithFlag(13, name: "PU13")
                .WithReservedBits(14, 1)
                .WithFlag(15, name: "PU15")
                .WithReservedBits(16, 16)
            ;

            // RM p.467, reset 0x00000000
            Registers.PDCRA.Define(this, 0x00000000, name: "PWR_PDCRA")
                .WithFlag(0, name: "PD0")
                .WithFlag(1, name: "PD1")
                .WithFlag(2, name: "PD2")
                .WithFlag(3, name: "PD3")
                .WithFlag(4, name: "PD4")
                .WithFlag(5, name: "PD5")
                .WithFlag(6, name: "PD6")
                .WithFlag(7, name: "PD7")
                .WithFlag(8, name: "PD8")
                .WithFlag(9, name: "PD9")
                .WithFlag(10, name: "PD10")
                .WithFlag(11, name: "PD11")
                .WithFlag(12, name: "PD12")
                .WithReservedBits(13, 1)
                .WithFlag(14, name: "PD14")
                .WithReservedBits(15, 17)
            ;

            // RM p.468, reset 0x00000000
            Registers.PUCRB.Define(this, 0x00000000, name: "PWR_PUCRB")
                .WithFlag(0, name: "PU0")
                .WithFlag(1, name: "PU1")
                .WithFlag(2, name: "PU2")
                .WithFlag(3, name: "PU3")
                .WithFlag(4, name: "PU4")
                .WithFlag(5, name: "PU5")
                .WithFlag(6, name: "PU6")
                .WithFlag(7, name: "PU7")
                .WithFlag(8, name: "PU8")
                .WithFlag(9, name: "PU9")
                .WithFlag(10, name: "PU10")
                .WithFlag(11, name: "PU11")
                .WithFlag(12, name: "PU12")
                .WithFlag(13, name: "PU13")
                .WithFlag(14, name: "PU14")
                .WithFlag(15, name: "PU15")
                .WithReservedBits(16, 16)
            ;

            // RM p.468, reset 0x00000000
            Registers.PDCRB.Define(this, 0x00000000, name: "PWR_PDCRB")
                .WithFlag(0, name: "PD0")
                .WithFlag(1, name: "PD1")
                .WithFlag(2, name: "PD2")
                .WithFlag(3, name: "PD3")
                .WithReservedBits(4, 1)
                .WithFlag(5, name: "PD5")
                .WithFlag(6, name: "PD6")
                .WithFlag(7, name: "PD7")
                .WithFlag(8, name: "PD8")
                .WithFlag(9, name: "PD9")
                .WithFlag(10, name: "PD10")
                .WithFlag(11, name: "PD11")
                .WithFlag(12, name: "PD12")
                .WithFlag(13, name: "PD13")
                .WithFlag(14, name: "PD14")
                .WithFlag(15, name: "PD15")
                .WithReservedBits(16, 16)
            ;

            // RM p.469, reset 0x00000000
            Registers.PUCRC.Define(this, 0x00000000, name: "PWR_PUCRC")
                .WithFlag(0, name: "PU0")
                .WithFlag(1, name: "PU1")
                .WithFlag(2, name: "PU2")
                .WithFlag(3, name: "PU3")
                .WithFlag(4, name: "PU4")
                .WithFlag(5, name: "PU5")
                .WithFlag(6, name: "PU6")
                .WithFlag(7, name: "PU7")
                .WithFlag(8, name: "PU8")
                .WithFlag(9, name: "PU9")
                .WithFlag(10, name: "PU10")
                .WithFlag(11, name: "PU11")
                .WithFlag(12, name: "PU12")
                .WithFlag(13, name: "PU13")
                .WithFlag(14, name: "PU14")
                .WithFlag(15, name: "PU15")
                .WithReservedBits(16, 16)
            ;

            // RM p.469, reset 0x00000000
            Registers.PDCRC.Define(this, 0x00000000, name: "PWR_PDCRC")
                .WithFlag(0, name: "PD0")
                .WithFlag(1, name: "PD1")
                .WithFlag(2, name: "PD2")
                .WithFlag(3, name: "PD3")
                .WithFlag(4, name: "PD4")
                .WithFlag(5, name: "PD5")
                .WithFlag(6, name: "PD6")
                .WithFlag(7, name: "PD7")
                .WithFlag(8, name: "PD8")
                .WithFlag(9, name: "PD9")
                .WithFlag(10, name: "PD10")
                .WithFlag(11, name: "PD11")
                .WithFlag(12, name: "PD12")
                .WithFlag(13, name: "PD13")
                .WithFlag(14, name: "PD14")
                .WithFlag(15, name: "PD15")
                .WithReservedBits(16, 16)
            ;

            // RM p.470, reset 0x00000000
            Registers.PUCRD.Define(this, 0x00000000, name: "PWR_PUCRD")
                .WithFlag(0, name: "PU0")
                .WithFlag(1, name: "PU1")
                .WithFlag(2, name: "PU2")
                .WithFlag(3, name: "PU3")
                .WithFlag(4, name: "PU4")
                .WithFlag(5, name: "PU5")
                .WithFlag(6, name: "PU6")
                .WithFlag(7, name: "PU7")
                .WithFlag(8, name: "PU8")
                .WithFlag(9, name: "PU9")
                .WithFlag(10, name: "PU10")
                .WithFlag(11, name: "PU11")
                .WithFlag(12, name: "PU12")
                .WithFlag(13, name: "PU13")
                .WithFlag(14, name: "PU14")
                .WithFlag(15, name: "PU15")
                .WithReservedBits(16, 16)
            ;

            // RM p.470, reset 0x00000000
            Registers.PDCRD.Define(this, 0x00000000, name: "PWR_PDCRD")
                .WithFlag(0, name: "PD0")
                .WithFlag(1, name: "PD1")
                .WithFlag(2, name: "PD2")
                .WithFlag(3, name: "PD3")
                .WithFlag(4, name: "PD4")
                .WithFlag(5, name: "PD5")
                .WithFlag(6, name: "PD6")
                .WithFlag(7, name: "PD7")
                .WithFlag(8, name: "PD8")
                .WithFlag(9, name: "PD9")
                .WithFlag(10, name: "PD10")
                .WithFlag(11, name: "PD11")
                .WithFlag(12, name: "PD12")
                .WithFlag(13, name: "PD13")
                .WithFlag(14, name: "PD14")
                .WithFlag(15, name: "PD15")
                .WithReservedBits(16, 16)
            ;

            // RM p.471, reset 0x00000000
            Registers.PUCRE.Define(this, 0x00000000, name: "PWR_PUCRE")
                .WithFlag(0, name: "PU0")
                .WithFlag(1, name: "PU1")
                .WithFlag(2, name: "PU2")
                .WithFlag(3, name: "PU3")
                .WithFlag(4, name: "PU4")
                .WithFlag(5, name: "PU5")
                .WithFlag(6, name: "PU6")
                .WithFlag(7, name: "PU7")
                .WithFlag(8, name: "PU8")
                .WithFlag(9, name: "PU9")
                .WithFlag(10, name: "PU10")
                .WithFlag(11, name: "PU11")
                .WithFlag(12, name: "PU12")
                .WithFlag(13, name: "PU13")
                .WithFlag(14, name: "PU14")
                .WithFlag(15, name: "PU15")
                .WithReservedBits(16, 16)
            ;

            // RM p.471, reset 0x00000000
            Registers.PDCRE.Define(this, 0x00000000, name: "PWR_PDCRE")
                .WithFlag(0, name: "PD0")
                .WithFlag(1, name: "PD1")
                .WithFlag(2, name: "PD2")
                .WithFlag(3, name: "PD3")
                .WithFlag(4, name: "PD4")
                .WithFlag(5, name: "PD5")
                .WithFlag(6, name: "PD6")
                .WithFlag(7, name: "PD7")
                .WithFlag(8, name: "PD8")
                .WithFlag(9, name: "PD9")
                .WithFlag(10, name: "PD10")
                .WithFlag(11, name: "PD11")
                .WithFlag(12, name: "PD12")
                .WithFlag(13, name: "PD13")
                .WithFlag(14, name: "PD14")
                .WithFlag(15, name: "PD15")
                .WithReservedBits(16, 16)
            ;

            // RM p.472, reset 0x00000000
            Registers.PUCRF.Define(this, 0x00000000, name: "PWR_PUCRF")
                .WithFlag(0, name: "PU0")
                .WithFlag(1, name: "PU1")
                .WithFlag(2, name: "PU2")
                .WithFlag(3, name: "PU3")
                .WithFlag(4, name: "PU4")
                .WithFlag(5, name: "PU5")
                .WithFlag(6, name: "PU6")
                .WithFlag(7, name: "PU7")
                .WithFlag(8, name: "PU8")
                .WithFlag(9, name: "PU9")
                .WithFlag(10, name: "PU10")
                .WithFlag(11, name: "PU11")
                .WithFlag(12, name: "PU12")
                .WithFlag(13, name: "PU13")
                .WithFlag(14, name: "PU14")
                .WithFlag(15, name: "PU15")
                .WithReservedBits(16, 16)
            ;

            // RM p.472, reset 0x00000000
            Registers.PDCRF.Define(this, 0x00000000, name: "PWR_PDCRF")
                .WithFlag(0, name: "PD0")
                .WithFlag(1, name: "PD1")
                .WithFlag(2, name: "PD2")
                .WithFlag(3, name: "PD3")
                .WithFlag(4, name: "PD4")
                .WithFlag(5, name: "PD5")
                .WithFlag(6, name: "PD6")
                .WithFlag(7, name: "PD7")
                .WithFlag(8, name: "PD8")
                .WithFlag(9, name: "PD9")
                .WithFlag(10, name: "PD10")
                .WithFlag(11, name: "PD11")
                .WithFlag(12, name: "PD12")
                .WithFlag(13, name: "PD13")
                .WithFlag(14, name: "PD14")
                .WithFlag(15, name: "PD15")
                .WithReservedBits(16, 16)
            ;

            // RM p.473, reset 0x00000000
            Registers.PUCRG.Define(this, 0x00000000, name: "PWR_PUCRG")
                .WithFlag(0, name: "PU0")
                .WithFlag(1, name: "PU1")
                .WithFlag(2, name: "PU2")
                .WithFlag(3, name: "PU3")
                .WithFlag(4, name: "PU4")
                .WithFlag(5, name: "PU5")
                .WithFlag(6, name: "PU6")
                .WithFlag(7, name: "PU7")
                .WithFlag(8, name: "PU8")
                .WithFlag(9, name: "PU9")
                .WithFlag(10, name: "PU10")
                .WithFlag(11, name: "PU11")
                .WithFlag(12, name: "PU12")
                .WithFlag(13, name: "PU13")
                .WithFlag(14, name: "PU14")
                .WithFlag(15, name: "PU15")
                .WithReservedBits(16, 16)
            ;

            // RM p.473, reset 0x00000000
            Registers.PDCRG.Define(this, 0x00000000, name: "PWR_PDCRG")
                .WithFlag(0, name: "PD0")
                .WithFlag(1, name: "PD1")
                .WithFlag(2, name: "PD2")
                .WithFlag(3, name: "PD3")
                .WithFlag(4, name: "PD4")
                .WithFlag(5, name: "PD5")
                .WithFlag(6, name: "PD6")
                .WithFlag(7, name: "PD7")
                .WithFlag(8, name: "PD8")
                .WithFlag(9, name: "PD9")
                .WithFlag(10, name: "PD10")
                .WithFlag(11, name: "PD11")
                .WithFlag(12, name: "PD12")
                .WithFlag(13, name: "PD13")
                .WithFlag(14, name: "PD14")
                .WithFlag(15, name: "PD15")
                .WithReservedBits(16, 16)
            ;

            // RM p.474, reset 0x00000000
            Registers.PUCRH.Define(this, 0x00000000, name: "PWR_PUCRH")
                .WithFlag(0, name: "PU0")
                .WithFlag(1, name: "PU1")
                .WithFlag(2, name: "PU2")
                .WithFlag(3, name: "PU3")
                .WithFlag(4, name: "PU4")
                .WithFlag(5, name: "PU5")
                .WithFlag(6, name: "PU6")
                .WithFlag(7, name: "PU7")
                .WithFlag(8, name: "PU8")
                .WithFlag(9, name: "PU9")
                .WithFlag(10, name: "PU10")
                .WithFlag(11, name: "PU11")
                .WithFlag(12, name: "PU12")
                .WithFlag(13, name: "PU13")
                .WithFlag(14, name: "PU14")
                .WithFlag(15, name: "PU15")
                .WithReservedBits(16, 16)
            ;

            // RM p.474, reset 0x00000000
            Registers.PDCRH.Define(this, 0x00000000, name: "PWR_PDCRH")
                .WithFlag(0, name: "PD0")
                .WithFlag(1, name: "PD1")
                .WithFlag(2, name: "PD2")
                .WithFlag(3, name: "PD3")
                .WithFlag(4, name: "PD4")
                .WithFlag(5, name: "PD5")
                .WithFlag(6, name: "PD6")
                .WithFlag(7, name: "PD7")
                .WithFlag(8, name: "PD8")
                .WithFlag(9, name: "PD9")
                .WithFlag(10, name: "PD10")
                .WithFlag(11, name: "PD11")
                .WithFlag(12, name: "PD12")
                .WithFlag(13, name: "PD13")
                .WithFlag(14, name: "PD14")
                .WithFlag(15, name: "PD15")
                .WithReservedBits(16, 16)
            ;

            // RM p.475, reset 0x00000000
            Registers.PUCRI.Define(this, 0x00000000, name: "PWR_PUCRI")
                .WithFlag(0, name: "PU0")
                .WithFlag(1, name: "PU1")
                .WithFlag(2, name: "PU2")
                .WithFlag(3, name: "PU3")
                .WithFlag(4, name: "PU4")
                .WithFlag(5, name: "PU5")
                .WithFlag(6, name: "PU6")
                .WithFlag(7, name: "PU7")
                .WithReservedBits(8, 24)
            ;

            // RM p.475, reset 0x00000000
            Registers.PDCRI.Define(this, 0x00000000, name: "PWR_PDCRI")
                .WithFlag(0, name: "PD0")
                .WithFlag(1, name: "PD1")
                .WithFlag(2, name: "PD2")
                .WithFlag(3, name: "PD3")
                .WithFlag(4, name: "PD4")
                .WithFlag(5, name: "PD5")
                .WithFlag(6, name: "PD6")
                .WithFlag(7, name: "PD7")
                .WithReservedBits(8, 24)
            ;
        }

        // RM0456 section 10.8: the bits of the register at offset denied to the current access.
        private uint DeniedBits(long offset, bool write)
        {
            STM32U5_TrustZoneContext.TryGetTransaction(sysbus, out var secure, out var privileged);
            secure &= trustZoneEnabled;
            if(offset == (long)Registers.SECCFGR)
            {
                if(!trustZoneEnabled)
                {
                    return AllBits;
                }
                if(!write)
                {
                    return 0;
                }
                if(!secure)
                {
                    this.Log(LogLevel.Debug, "Non-secure write of PWR_SECCFGR denied");
                    illegalAccessController?.ReportIllegalAccess(IllegalAccessRegister, IllegalAccessBit);
                    return AllBits;
                }
                return !privileged && securePrivileged.Value ? AllBits : 0;
            }
            if(offset == (long)Registers.PRIVCFGR)
            {
                if(!write)
                {
                    return 0;
                }
                // Written by privileged accesses only; SPRIV by secure ones only.
                return !privileged ? AllBits : secure ? 0 : SecurePrivilegeBit;
            }
            var secureBits = trustZoneEnabled ? SecureBits(offset) : 0;
            var denied = secure ? 0 : secureBits;
            if(!privileged)
            {
                denied |= securePrivileged.Value ? secureBits : 0;
                if(nonSecurePrivileged.Value)
                {
                    denied |= ~secureBits & ~(write ? 0 : UnprivilegedReadableBits(offset));
                }
            }
            return denied;
        }

        // RM0456 Table 109.
        private uint SecureBits(long offset)
        {
            var configuration = base.ReadDoubleWord((long)Registers.SECCFGR);
            var lowPower = (configuration & LowPowerModeProtection) != 0;
            var wakeupPins = configuration & WakeupPinProtections;
            switch((Registers)offset)
            {
            case Registers.CR1:
            case Registers.CR2:
                return lowPower ? AllBits : 0;
            case Registers.SR:
                return lowPower ? ClearStopStandbyFlagBit : 0;
            case Registers.CR3:
            case Registers.SVMCR:
                return (configuration & VoltageDetectionProtection) != 0 ? AllBits : 0;
            case Registers.BDCR1:
            case Registers.BDCR2:
            case Registers.DBPR:
                return (configuration & BackupDomainProtection) != 0 ? AllBits : 0;
            case Registers.APCR:
                return (configuration & PullConfigurationProtection) != 0 ? AllBits : 0;
            case Registers.WUCR1:
            case Registers.WUCR2:
            case Registers.WUSCR:
                return wakeupPins;
            case Registers.WUCR3:
            {
                // WUSELx: two bits per pin.
                var bits = 0u;
                for(var pin = 0; pin < 8; pin++)
                {
                    bits |= (wakeupPins & (1u << pin)) != 0 ? 3u << (2 * pin) : 0;
                }
                return bits;
            }
            case Registers.UCPDR:
                return tzsc1 != null && tzsc1.IsSecure(UcpdConfigurationRegister, UcpdBit) ? AllBits : 0;
            case Registers.VOSR:
                return TryGetPeripheral<STM32U5_RCC>("rcc", out var rcc) && rcc.SystemClockSecure ? VoltageScalingBits : 0;
            }
            if(offset >= (long)Registers.PUCRA && offset <= (long)Registers.PDCRI)
            {
                // PWR_PUCRx at 0x50 + 8 * x, PWR_PDCRx at 0x54 + 8 * x: one bit per pin.
                var port = (char)('A' + (offset - (long)Registers.PUCRA) / 8);
                return TryGetPeripheral<STM32U5_GPIOPort>("gpioPort" + port, out var gpio) ? gpio.SecurePins : 0;
            }
            return 0;
        }

        // NSPRIV: the status registers and VOSRDY/BOOSTRDY stay readable by unprivileged accesses.
        private static uint UnprivilegedReadableBits(long offset)
        {
            switch((Registers)offset)
            {
            case Registers.SR:
            case Registers.SVMSR:
            case Registers.BDSR:
            case Registers.WUSR:
                return AllBits;
            case Registers.VOSR:
                return VoltageScalingReadyBits;
            default:
                return 0;
            }
        }

        // Peripherals are found by their platform name (sysbus.<name>) on first use; a missing
        // peripheral is looked up again later.
        private bool TryGetPeripheral<T>(string name, out T peripheral) where T : class, IPeripheral
        {
            if(peripherals.TryGetValue(name, out var found))
            {
                peripheral = found as T;
                return peripheral != null;
            }
            if(!machine.TryGetByName<IPeripheral>("sysbus." + name, out found))
            {
                peripheral = null;
                return false;
            }
            peripherals[name] = found;
            peripheral = found as T;
            return peripheral != null;
        }

        private readonly IBusController sysbus;
        private readonly bool trustZoneEnabled;
        private readonly STM32U5_GTZC_TZIC illegalAccessController;
        private readonly STM32U5_GTZC_TZSC tzsc1;
        private readonly Dictionary<string, IPeripheral> peripherals = new Dictionary<string, IPeripheral>();
        private IFlagRegisterField securePrivileged;
        private IFlagRegisterField nonSecurePrivileged;

        private const uint AllBits = 0xFFFFFFFF;
        private const uint SecurePrivilegeBit = 1u << 0;
        // PWR_SECCFGR (stm32u575xx.h PWR_SECCFGR_*_Pos).
        private const uint WakeupPinProtections = 0xFF;
        private const uint LowPowerModeProtection = 1u << 12;
        private const uint VoltageDetectionProtection = 1u << 13;
        private const uint BackupDomainProtection = 1u << 14;
        private const uint PullConfigurationProtection = 1u << 15;
        private const uint ClearStopStandbyFlagBit = 1u << 0;
        // PWR_VOSR: VOS[1:0] and BOOSTEN; BOOSTRDY and VOSRDY.
        private const uint VoltageScalingBits = 0x7u << 16;
        private const uint VoltageScalingReadyBits = 0x3u << 14;
        // GTZC1 TZSC SECCFGR1.UCPD1 (stm32u575xx.h GTZC_CFGR1_UCPD1_Pos).
        private const int UcpdConfigurationRegister = 1;
        private const int UcpdBit = 19;
        // GTZC2_TZIC_IER2.PWRIE (stm32u575xx.h GTZC_CFGR2_PWR_Pos).
        private const int IllegalAccessRegister = 2;
        private const int IllegalAccessBit = 3;

        private IFlagRegisterField regulatorSelection;
        private IFlagRegisterField boosterEnable;
        private IValueRegisterField voltageScaling;
        private IFlagRegisterField backupDomainWriteEnable;
        private IFlagRegisterField standbyFlag;
        private IFlagRegisterField stopFlag;
        private IFlagRegisterField[] wakeupFlags;

        private enum Registers
        {
            CR1 = 0x000, // PWR_CR1
            CR2 = 0x004, // PWR_CR2
            CR3 = 0x008, // PWR_CR3
            VOSR = 0x00C, // PWR_VOSR
            SVMCR = 0x010, // PWR_SVMCR
            WUCR1 = 0x014, // PWR_WUCR1
            WUCR2 = 0x018, // PWR_WUCR2
            WUCR3 = 0x01C, // PWR_WUCR3
            BDCR1 = 0x020, // PWR_BDCR1
            BDCR2 = 0x024, // PWR_BDCR2
            DBPR = 0x028, // PWR_DBPR
            UCPDR = 0x02C, // PWR_UCPDR
            SECCFGR = 0x030, // PWR_SECCFGR
            PRIVCFGR = 0x034, // PWR_PRIVCFGR
            SR = 0x038, // PWR_SR
            SVMSR = 0x03C, // PWR_SVMSR
            BDSR = 0x040, // PWR_BDSR
            WUSR = 0x044, // PWR_WUSR
            WUSCR = 0x048, // PWR_WUSCR
            APCR = 0x04C, // PWR_APCR
            PUCRA = 0x050, // PWR_PUCRA
            PDCRA = 0x054, // PWR_PDCRA
            PUCRB = 0x058, // PWR_PUCRB
            PDCRB = 0x05C, // PWR_PDCRB
            PUCRC = 0x060, // PWR_PUCRC
            PDCRC = 0x064, // PWR_PDCRC
            PUCRD = 0x068, // PWR_PUCRD
            PDCRD = 0x06C, // PWR_PDCRD
            PUCRE = 0x070, // PWR_PUCRE
            PDCRE = 0x074, // PWR_PDCRE
            PUCRF = 0x078, // PWR_PUCRF
            PDCRF = 0x07C, // PWR_PDCRF
            PUCRG = 0x080, // PWR_PUCRG
            PDCRG = 0x084, // PWR_PDCRG
            PUCRH = 0x088, // PWR_PUCRH
            PDCRH = 0x08C, // PWR_PDCRH
            PUCRI = 0x090, // PWR_PUCRI
            PDCRI = 0x094, // PWR_PDCRI
        }
    }
}
