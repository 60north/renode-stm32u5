//
// Copyright (c) 2026 Fedor Druzhinin
//
// This file is licensed under the MIT License.
//
// STM32U5 system configuration controller (SYSCFG), RM0456 Rev 7 section 15. New model: the
// upstream STM32_SYSCFG is the EXTI multiplexer of other series (on the U5 it is in EXTI).
//
// The registers keep their values; the effects on other blocks are not modelled: the CPU
// locks of SYSCFG_CNSLCKR/CSLCKR (VTOR, MPU, SAU, AIRCR), the class B locks of SYSCFG_CFGR2,
// the FPU interrupt mask, Fast-mode Plus and analog switch settings. The I/O compensation
// cells are ready (RDYx) as soon as they are enabled (ENx). SYSCFG_CCVR reads 0.
//
// TrustZone (RM0456 Table 131, section 15.3.1), with trustZoneEnabled (TZEN = 1):
// - SYSCFG_SECCFGR: read by any access, written by secure accesses only (FPUSEC: secure
//   privileged); with TZEN = 0 it is RAZ/WI.
// - SYSCFG_CSLCKR: secure privileged; with TZEN = 0 RAZ/WI. SYSCFG_CNSLCKR and SYSCFG_FPUIMR:
//   privileged.
// - FPUSEC protects SYSCFG_FPUIMR, CLASSBSEC SYSCFG_CFGR2, SYSCFGSEC SYSCFG_MESR, CCCSR, CCVR,
//   CCCR and RSSCMDR: a non-secure access to a protected register is RAZ/WI.
// - A non-secure access refused for security raises the SYSCFG illegal access event (GTZC2
//   TZIC_SR2 bit 0); an unprivileged access refused for privilege is silent.
// - SYSCFG_CFGR1: the per-bit security inherited from GPIOB and the ADCs is not modelled.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class STM32U5_SYSCFG : BasicDoubleWordPeripheral, IKnownSize, ISTM32U5_TrustZoneAware
    {
        public STM32U5_SYSCFG(IMachine machine, bool trustZoneEnabled = false, STM32U5_GTZC_TZIC illegalAccessController = null)
            : base(machine)
        {
            sysbus = machine.GetSystemBus(this);
            this.trustZoneEnabled = trustZoneEnabled;
            this.illegalAccessController = illegalAccessController;
            DefineRegisters();
            Reset();
        }

        public override uint ReadDoubleWord(long offset)
        {
            return AccessAllowed(offset, false) ? base.ReadDoubleWord(offset) : 0;
        }

        public override void WriteDoubleWord(long offset, uint value)
        {
            if(!AccessAllowed(offset, true))
            {
                return;
            }
            if(offset == (long)Registers.SecureConfiguration)
            {
                // FPUSEC needs a privileged access.
                STM32U5_TrustZoneContext.TryGetTransaction(sysbus, out var _, out var privileged);
                if(!privileged)
                {
                    value = (value & ~FpuSecure) | (base.ReadDoubleWord(offset) & FpuSecure);
                }
            }
            base.WriteDoubleWord(offset, value);
        }

        public long Size => 0x400;

        public bool HasSecureFunction => trustZoneEnabled && base.ReadDoubleWord((long)Registers.SecureConfiguration) != 0;

        private bool AccessAllowed(long offset, bool write)
        {
            var register = (Registers)offset;
            // TZEN = 0: SYSCFG_SECCFGR and SYSCFG_CSLCKR are RAZ/WI for any access.
            if(!trustZoneEnabled && (register == Registers.SecureConfiguration || register == Registers.CpuSecureLock))
            {
                return false;
            }
            if(!STM32U5_TrustZoneContext.TryGetTransaction(sysbus, out var secure, out var privileged))
            {
                return true;
            }
            if(!trustZoneEnabled)
            {
                return privileged || !PrivilegedOnly(register);
            }
            bool secureOnly;
            switch(register)
            {
            case Registers.SecureConfiguration:
                secureOnly = write;
                break;
            case Registers.CpuSecureLock:
                secureOnly = true;
                break;
            case Registers.FpuInterruptMask:
                secureOnly = (base.ReadDoubleWord((long)Registers.SecureConfiguration) & FpuSecure) != 0;
                break;
            case Registers.Configuration2:
                secureOnly = (base.ReadDoubleWord((long)Registers.SecureConfiguration) & ClassBSecure) != 0;
                break;
            case Registers.MemoryEraseStatus:
            case Registers.CompensationCellControl:
            case Registers.CompensationCellValue:
            case Registers.CompensationCellCode:
            case Registers.RssCommand:
                secureOnly = (base.ReadDoubleWord((long)Registers.SecureConfiguration) & SyscfgSecure) != 0;
                break;
            default:
                secureOnly = false;
                break;
            }
            if(secureOnly && !secure)
            {
                illegalAccessController?.ReportIllegalAccess(IllegalAccessRegister, IllegalAccessBit);
                return false;
            }
            return privileged || !PrivilegedOnly(register);
        }

        private static bool PrivilegedOnly(Registers register)
        {
            return register == Registers.FpuInterruptMask || register == Registers.CpuNonSecureLock || register == Registers.CpuSecureLock;
        }

        private void DefineRegisters()
        {
            Registers.SecureConfiguration.Define(this)
                .WithFlag(0, name: "SYSCFGSEC")
                .WithFlag(1, name: "CLASSBSEC")
                .WithReservedBits(2, 1)
                .WithFlag(3, name: "FPUSEC")
                .WithReservedBits(4, 28);

            Registers.Configuration1.Define(this)
                .WithReservedBits(0, 8)
                .WithFlag(8, name: "BOOSTEN")
                .WithFlag(9, name: "ANASWVDD")
                .WithReservedBits(10, 6)
                .WithFlag(16, name: "PB6_FMP")
                .WithFlag(17, name: "PB7_FMP")
                .WithFlag(18, name: "PB8_FMP")
                .WithFlag(19, name: "PB9_FMP")
                .WithReservedBits(20, 12);

            Registers.FpuInterruptMask.Define(this, FpuInterruptMaskResetValue)
                .WithValueField(0, 6, name: "FPU_IE")
                .WithReservedBits(6, 26);

            Registers.CpuNonSecureLock.Define(this)
                .WithFlag(0, FieldMode.Read | FieldMode.Set, name: "LOCKNSVTOR")
                .WithFlag(1, FieldMode.Read | FieldMode.Set, name: "LOCKNSMPU")
                .WithReservedBits(2, 30);

            Registers.CpuSecureLock.Define(this)
                .WithFlag(0, FieldMode.Read | FieldMode.Set, name: "LOCKSVTAIRCR")
                .WithFlag(1, FieldMode.Read | FieldMode.Set, name: "LOCKSMPU")
                .WithFlag(2, FieldMode.Read | FieldMode.Set, name: "LOCKSAU")
                .WithReservedBits(3, 29);

            Registers.Configuration2.Define(this)
                .WithFlag(0, FieldMode.Read | FieldMode.Set, name: "CLL")
                .WithFlag(1, FieldMode.Read | FieldMode.Set, name: "SPL")
                .WithFlag(2, FieldMode.Read | FieldMode.Set, name: "PVDL")
                .WithFlag(3, FieldMode.Read | FieldMode.Set, name: "ECCL")
                .WithReservedBits(4, 28);

            Registers.MemoryEraseStatus.Define(this)
                .WithFlag(0, FieldMode.Read | FieldMode.WriteOneToClear, name: "MCLR")
                .WithReservedBits(1, 15)
                .WithFlag(16, FieldMode.Read | FieldMode.WriteOneToClear, name: "IPMEE")
                .WithReservedBits(17, 15);

            Registers.CompensationCellControl.Define(this, CompensationCellControlResetValue)
                .WithFlag(0, out compensationCell1Enabled, name: "EN1")
                .WithFlag(1, name: "CS1")
                .WithFlag(2, out compensationCell2Enabled, name: "EN2")
                .WithFlag(3, name: "CS2")
                .WithReservedBits(4, 4)
                .WithFlag(8, FieldMode.Read, name: "RDY1", valueProviderCallback: _ => compensationCell1Enabled.Value)
                .WithFlag(9, FieldMode.Read, name: "RDY2", valueProviderCallback: _ => compensationCell2Enabled.Value)
                .WithReservedBits(10, 22);

            Registers.CompensationCellValue.Define(this)
                .WithValueField(0, 4, FieldMode.Read, name: "NCV1")
                .WithValueField(4, 4, FieldMode.Read, name: "PCV1")
                .WithValueField(8, 4, FieldMode.Read, name: "NCV2")
                .WithValueField(12, 4, FieldMode.Read, name: "PCV2")
                .WithReservedBits(16, 16);

            Registers.CompensationCellCode.Define(this, CompensationCellCodeResetValue)
                .WithValueField(0, 4, name: "NCC1")
                .WithValueField(4, 4, name: "PCC1")
                .WithValueField(8, 4, name: "NCC2")
                .WithValueField(12, 4, name: "PCC2")
                .WithReservedBits(16, 16);

            Registers.RssCommand.Define(this)
                .WithValueField(0, 16, name: "RSSCMD")
                .WithReservedBits(16, 16);
        }

        private IFlagRegisterField compensationCell1Enabled;
        private IFlagRegisterField compensationCell2Enabled;

        private readonly IBusController sysbus;
        private readonly bool trustZoneEnabled;
        private readonly STM32U5_GTZC_TZIC illegalAccessController;

        private const uint SyscfgSecure = 1u << 0;
        private const uint ClassBSecure = 1u << 1;
        private const uint FpuSecure = 1u << 3;
        // RM0456 section 15.3: STM32U575/585 reset values.
        private const uint FpuInterruptMaskResetValue = 0x0000001F;
        private const uint CompensationCellControlResetValue = 0x0000000A;
        private const uint CompensationCellCodeResetValue = 0x00007878;
        // GTZC2 TZIC_IER2/SR2 SYSCFG (cmsis-device-u5 GTZC_CFGR2_SYSCFG_Pos).
        private const int IllegalAccessRegister = 2;
        private const int IllegalAccessBit = 0;

        private enum Registers
        {
            SecureConfiguration = 0x00,
            Configuration1 = 0x04,
            FpuInterruptMask = 0x08,
            CpuNonSecureLock = 0x0C,
            CpuSecureLock = 0x10,
            Configuration2 = 0x14,
            MemoryEraseStatus = 0x18,
            CompensationCellControl = 0x1C,
            CompensationCellValue = 0x20,
            CompensationCellCode = 0x24,
            RssCommand = 0x2C,
        }
    }
}
