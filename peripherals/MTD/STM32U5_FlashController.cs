//
// Copyright (c) 2026 Fedor Druzhinin
// Copyright (c) 2010-2023 Antmicro
//
// This file is licensed under the MIT License.
//
// STM32U5 embedded flash memory interface (FLASH), RM0456 Rev 7 section 7.
//
// Key-based locking (LockRegister), the write filter for the locked control register and the
// erase sequence are adapted from renode-infrastructure
//   src/Emulator/Peripherals/Peripherals/MTD/STM32WBA_FlashController.cs @ 7ea0541f8d9b
// and extended to the two banks of STM32U5 (BKER, MER1, MER2) and to the secure interface.
// Register layout and bit positions: cmsis-device-u5 v1.4.3 stm32u575xx.h.
// Reset values: RM0456 Rev 7 (variant STM32U575); option byte registers hold the documented
// "ST production value".
//
// Limitations:
// - Programming: while PG is set, the CPU writes to the flash memory are checked (quad-word or
//   burst alignment, 32-bit accesses, target erased or programmed to zero) and PROGERR, SIZERR,
//   PGAERR, PGSERR and EOP are set as in RM0456 section 7.3.9. The data is written to the memory
//   also when an error is reported, and writes while PG is cleared are neither checked nor
//   blocked. WRPERR is reported only for the TrustZone rules below (no WRP areas).
// - Erase completes immediately (BSY is always 0). Option byte modification, OBL_LAUNCH,
//   bank swapping, HDP areas, the block-based privilege of pages, ECC and power-down are not
//   modelled.
// - FLASH_OPTR.TZEN is a constructor parameter: TrustZone is a property of the platform
//   description (stm32u575-trustzone.repl), it cannot change at run time. The secure
//   watermarks (FLASH_SECWMxR1, option bytes) are constructor parameters too; page security
//   follows them, not later writes to FLASH_SECWMxR1. The boot address, watermark and WRP
//   option registers ignore writes while OPTLOCK is set.
// - TrustZone (RM0456 sections 7.5, 7.7): two interfaces, FLASH_NSKEYR/NSSR/NSCR and
//   FLASH_SECKEYR/SECSR/SECCR, with their own lock, flags and interrupt (IRQ: FLASH_IRQn,
//   SecureIRQ: FLASH_S_IRQn). A page is secure inside the watermark area of its bank or when
//   its FLASH_SECBBxRy bit is set. An erase or a programming of a page of the other security
//   through an interface is ignored and sets WRPERR of that interface; a programming and a bank
//   or mass erase also report a flash illegal access to the TZIC. A programming write is
//   secure when it goes through the secure alias (0x0C00 0000). Data reads and fetches are not
//   checked: the flash memory is MappedMemory, and the data of a rejected programming write
//   reaches the memory; the other words of a rejected quad-word add PGSERR (not checked on
//   hardware). Secure registers are RAZ/WI for non-secure accesses (a flash register
//   illegal access is reported to the TZIC; FLASH_SECBBxRy stay readable) and for every access
//   with TZEN = 0. FLASH_PRIVCFGR SPRIV/NSPRIV make the secure/non-secure registers RAZ/WI for
//   unprivileged accesses.
//

using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Logging.Profiling;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Miscellaneous;

namespace Antmicro.Renode.Peripherals.MTD
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class STM32U5_FlashController : STM32_FlashController, IKnownSize, ISTM32U5_TrustZoneAware
    {
        public STM32U5_FlashController(IMachine machine, MappedMemory flash, bool trustZoneEnabled = false,
            STM32U5_GTZC_TZIC illegalAccessController = null,
            uint secureWatermark1 = SecureWatermarkProductionValue, uint secureWatermark2 = SecureWatermarkProductionValue)
            : base(machine)
        {
            memory = flash;
            sysbus = machine.GetSystemBus(this);
            this.trustZoneEnabled = trustZoneEnabled;
            this.illegalAccessController = illegalAccessController;
            this.secureWatermark1 = secureWatermark1;
            this.secureWatermark2 = secureWatermark2;
            // Erased flash reads as 0xFF.
            memory.ResetByte = ErasedByte;
            IRQ = new GPIO();
            SecureIRQ = new GPIO();
            nonSecure = new FlashInterface(false, new LockRegister(this, "controlLock", ControlKeys), IRQ);
            secure = new FlashInterface(true, new LockRegister(this, "secureControlLock", ControlKeys), SecureIRQ);
            optionLock = new LockRegister(this, nameof(optionLock), OptionKeys);
            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            nonSecure.Lock.Reset();
            secure.Lock.Reset();
            optionLock.Reset();
            base.Reset();
            IRQ.Unset();
            SecureIRQ.Unset();
            nonSecure.ProgramBufferCount = 0;
            secure.ProgramBufferCount = 0;
            // PG is cleared by the reset without its change callback
            if(programmingHookSet)
            {
                UpdateProgrammingHook();
            }
        }

        public override uint ReadDoubleWord(long offset)
        {
            return CheckRegisterAccess(offset, false, out _) ? base.ReadDoubleWord(offset) : 0;
        }

        public override void WriteDoubleWord(long offset, uint value)
        {
            if(!CheckRegisterAccess(offset, true, out var keptBits))
            {
                return;
            }
            // FLASH_NSCR and FLASH_SECCR are write-protected while their LOCK is set (RM0456
            // sections 7.9.9 and 7.9.10); writing 1 to LOCK itself is always possible.
            var flashInterface = offset == (long)Registers.NSCR ? nonSecure : offset == (long)Registers.SECCR ? secure : null;
            if(flashInterface != null && flashInterface.Lock.IsLocked && (value & LockBit) == 0)
            {
                this.Log(LogLevel.Warning, "Write to {0} ignored: register is locked", (Registers)offset);
                return;
            }
            // RM0456 section 7.9: the boot address, watermark and WRP option registers
            // cannot be written while OPTLOCK is set.
            if(Array.IndexOf(OptionLockedRegisters, (Registers)offset) >= 0 && optionLock.IsLocked)
            {
                this.Log(LogLevel.Warning, "Write to {0} ignored: OPTLOCK is set", (Registers)offset);
                return;
            }
            if(keptBits != 0)
            {
                value = (value & ~keptBits) | (base.ReadDoubleWord(offset) & keptBits);
            }
            base.WriteDoubleWord(offset, value);
        }

        public long Size => 0x400;

        // RM0456 section 7.5.5 Table 62: the flash memory is secure when a page is secure (watermark
        // or block-based), unless FLASH_SECCR.INV inverts it; FLASHEN and FLASHSMEN in the RCC
        // follow.
        public bool HasSecureFunction
        {
            get
            {
                if(!trustZoneEnabled)
                {
                    return false;
                }
                var securePage = false;
                for(var bank = 0; bank < 2 && !securePage; bank++)
                {
                    securePage = SecurePages(bank) > 0;
                }
                return securePage != invertSecurity.Value;
            }
        }

        // FLASH_IRQn: the non-secure interface.
        public GPIO IRQ { get; }

        // FLASH_S_IRQn: the secure interface (TZEN = 1).
        public GPIO SecureIRQ { get; }

        private void DefineRegisters()
        {
            // RM p.329, reset 0x00000000
            Registers.ACR.Define(this, 0x00000000, name: "FLASH_ACR")
                .WithValueField(0, 4, name: "LATENCY")
                .WithReservedBits(4, 4)
                .WithFlag(8, name: "PRFTEN")
                .WithReservedBits(9, 2)
                .WithFlag(11, name: "LPM")
                .WithFlag(12, name: "PDREQ1")
                .WithFlag(13, name: "PDREQ2")
                .WithFlag(14, name: "SLEEP_PD")
                .WithReservedBits(15, 17)
            ;

            // RM0456 sections 7.9.2 and 7.9.3: the same keys for both interfaces.
            Registers.NSKEYR.Define(this, 0x00000000, name: "FLASH_NSKEYR")
                .WithValueField(0, 32, FieldMode.Write, writeCallback: (_, value) => nonSecure.Lock.ConsumeValue((uint)value), name: "NSKEY")
            ;

            Registers.SECKEYR.Define(this, 0x00000000, name: "FLASH_SECKEYR")
                .WithValueField(0, 32, FieldMode.Write, writeCallback: (_, value) => secure.Lock.ConsumeValue((uint)value), name: "SECKEY")
            ;

            // RM0456 section 7.9.4. Accepted only while FLASH_NSCR is unlocked.
            Registers.OPTKEYR.Define(this, 0x00000000, name: "FLASH_OPTKEYR")
                .WithValueField(0, 32, FieldMode.Write, writeCallback: (_, value) =>
                    {
                        if(!nonSecure.Lock.IsLocked)
                        {
                            optionLock.ConsumeValue((uint)value);
                        }
                    }, name: "OPTKEY")
            ;

            // RM p.332, reset 0x00000000
            Registers.PDKEY1R.Define(this, 0x00000000, name: "FLASH_PDKEY1R")
                .WithReservedBits(0, 32)
            ;

            // RM p.333, reset 0x00000000
            Registers.PDKEY2R.Define(this, 0x00000000, name: "FLASH_PDKEY2R")
                .WithReservedBits(0, 32)
            ;

            // RM0456 sections 7.9.7 and 7.9.8. EOP and OPERR are set only with EOPIE/ERRIE; flags
            // clear on 1.
            DefineStatusRegister(Registers.NSSR, nonSecure, "FLASH_NSSR")
                .WithReservedBits(8, 5)
                .WithFlag(13, FieldMode.Read | FieldMode.WriteOneToClear, name: "OPTWERR")
                .WithReservedBits(14, 2)
                .WithFlag(18, FieldMode.Read, name: "OEM1LOCK")
                .WithFlag(19, FieldMode.Read, name: "OEM2LOCK")
                .WithFlag(20, FieldMode.Read, name: "PD1")
                .WithFlag(21, FieldMode.Read, name: "PD2")
                .WithReservedBits(22, 10)
            ;

            DefineStatusRegister(Registers.SECSR, secure, "FLASH_SECSR")
                .WithReservedBits(8, 8)
                .WithReservedBits(18, 14)
            ;

            // RM0456 sections 7.9.9 and 7.9.10, reset 0xC000 0000 and 0x8000 0000 (LOCK, OPTLOCK).
            DefineControlRegister(Registers.NSCR, nonSecure, "FLASH_NSCR")
                .WithFlag(17, FieldMode.Read | FieldMode.Write, valueProviderCallback: _ => false, writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            this.Log(LogLevel.Warning, "Option byte modification (OPTSTRT) is not supported");
                        }
                    }, name: "OPTSTRT")
                .WithReservedBits(18, 6)
                .WithReservedBits(26, 1)
                .WithFlag(27, FieldMode.Read | FieldMode.Write, valueProviderCallback: _ => false, writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            this.Log(LogLevel.Warning, "Option byte loading (OBL_LAUNCH) is not supported");
                        }
                    }, name: "OBL_LAUNCH")
                .WithReservedBits(28, 2)
                .WithFlag(30, valueProviderCallback: _ => optionLock.IsLocked, writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            optionLock.Lock();
                        }
                    }, name: "OPTLOCK")
                .WithFlag(31, valueProviderCallback: _ => nonSecure.Lock.IsLocked, writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            optionLock.Lock();
                            nonSecure.Lock.Lock();
                        }
                    }, name: "LOCK")
            ;

            DefineControlRegister(Registers.SECCR, secure, "FLASH_SECCR")
                .WithReservedBits(17, 7)
                .WithReservedBits(26, 3)
                // Inverts the flash security state seen by the RCC (HasSecureFunction).
                .WithFlag(29, out invertSecurity, name: "INV")
                .WithReservedBits(30, 1)
                .WithFlag(31, valueProviderCallback: _ => secure.Lock.IsLocked, writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            secure.Lock.Lock();
                        }
                    }, name: "LOCK")
            ;

            // RM p.340, reset 0x00000000
            Registers.ECCR.Define(this, 0x00000000, name: "FLASH_ECCR")
                .WithValueField(0, 20, FieldMode.Read, name: "ADDR_ECC")
                .WithReservedBits(20, 1)
                .WithFlag(21, FieldMode.Read, name: "BK_ECC")
                .WithFlag(22, FieldMode.Read, name: "SYSF_ECC")
                .WithReservedBits(23, 1)
                .WithFlag(24, name: "ECCIE")
                .WithReservedBits(25, 5)
                .WithFlag(30, name: "ECCC")
                .WithFlag(31, name: "ECCD")
            ;

            // RM p.341, reset 0xX0XXXXXX (X = factory programmed, modelled as 0)
            Registers.OPSR.Define(this, 0x00000000, name: "FLASH_OPSR")
                .WithValueField(0, 20, FieldMode.Read, name: "ADDR_OP")
                .WithReservedBits(20, 1)
                .WithFlag(21, FieldMode.Read, name: "BK_OP")
                .WithFlag(22, FieldMode.Read, name: "SYSF_OP")
                .WithReservedBits(23, 6)
                .WithValueField(29, 3, FieldMode.Read, name: "CODE_OP")
            ;

            // RM p.342, reset 0xXXXXXXXX, ST production value 0x1FEFF8AA; TZEN as the platform sets it
            Registers.OPTR.Define(this, OptionRegisterProductionValue | (trustZoneEnabled ? TrustZoneEnableBit : 0u),
                    name: "FLASH_OPTR")
                .WithValueField(0, 8, name: "RDP")
                .WithValueField(8, 3, name: "BOR_LEV")
                .WithReservedBits(11, 1)
                .WithFlag(12, name: "nRST_STOP")
                .WithFlag(13, name: "nRST_STDBY")
                .WithFlag(14, name: "nRST_SHDW")
                .WithFlag(15, name: "SRAM_RST")
                .WithFlag(16, name: "IWDG_SW")
                .WithFlag(17, name: "IWDG_STOP")
                .WithFlag(18, name: "IWDG_STDBY")
                .WithFlag(19, name: "WWDG_SW")
                .WithFlag(20, name: "SWAP_BANK")
                .WithFlag(21, name: "DUALBANK")
                .WithFlag(22, name: "BKPRAM_ECC")
                .WithFlag(23, name: "SRAM3_ECC")
                .WithFlag(24, name: "SRAM2_ECC")
                .WithFlag(25, name: "SRAM2_RST")
                .WithFlag(26, name: "nSWBOOT0")
                .WithFlag(27, name: "nBOOT0")
                .WithFlag(28, name: "PA15_PUPEN")
                .WithFlag(29, name: "IO_VDD_HSLV")
                .WithFlag(30, name: "IO_VDDIO2_HSLV")
                .WithFlag(31, name: "TZEN")
            ;

            // RM p.344, reset 0xXXXXXXXX, ST production value 0x0800007F
            Registers.NSBOOTADD0R.Define(this, 0x0800007F, name: "FLASH_NSBOOTADD0R")
                .WithReservedBits(0, 7)
                .WithValueField(7, 25, name: "NSBOOTADD0")
            ;

            // RM p.345, reset 0xXXXXXXXX, ST production value 0x0BF9007F
            Registers.NSBOOTADD1R.Define(this, 0x0BF9007F, name: "FLASH_NSBOOTADD1R")
                .WithReservedBits(0, 7)
                .WithValueField(7, 25, name: "NSBOOTADD1")
            ;

            // RM p.346, reset 0xXXXXXXXX, ST production value 0x0C00007C
            Registers.SECBOOTADD0R.Define(this, 0x0C00007C, name: "FLASH_SECBOOTADD0R")
                .WithFlag(0, name: "BOOT_LOCK")
                .WithReservedBits(1, 6)
                .WithValueField(7, 25, name: "SECBOOTADD0")
            ;

            // RM p.347, reset 0xXXXXXXXX, ST production value 0xFFFFFF80
            Registers.SECWM1R1.Define(this, secureWatermark1, name: "FLASH_SECWM1R1")
                .WithValueField(0, 7, name: "SECWM1_PSTRT")
                .WithReservedBits(7, 9)
                .WithValueField(16, 7, name: "SECWM1_PEND")
                .WithReservedBits(23, 9)
            ;

            // RM p.348, reset 0xXXXXXXXX, ST production value 0x7F807F80
            Registers.SECWM1R2.Define(this, 0x7F807F80, name: "FLASH_SECWM1R2")
                .WithReservedBits(0, 16)
                .WithValueField(16, 7, name: "HDP1_PEND")
                .WithReservedBits(23, 8)
                .WithFlag(31, name: "HDP1EN")
            ;

            // RM p.349, reset 0xXXXXXXXX, ST production value 0xFF80FFFF
            Registers.WRP1AR.Define(this, 0xFF80FFFF, name: "FLASH_WRP1AR")
                .WithValueField(0, 7, name: "WRP1A_PSTRT")
                .WithReservedBits(7, 9)
                .WithValueField(16, 7, name: "WRP1A_PEND")
                .WithReservedBits(23, 8)
                .WithFlag(31, name: "UNLOCK")
            ;

            // RM p.350, reset 0xXXXXXXXX, ST production value 0xFF80FFFF
            Registers.WRP1BR.Define(this, 0xFF80FFFF, name: "FLASH_WRP1BR")
                .WithValueField(0, 7, name: "WRP1B_PSTRT")
                .WithReservedBits(7, 9)
                .WithValueField(16, 7, name: "WRP1B_PEND")
                .WithReservedBits(23, 8)
                .WithFlag(31, name: "UNLOCK")
            ;

            // RM p.351, reset 0xXXXXXXXX, ST production value 0xFFFFFF80
            Registers.SECWM2R1.Define(this, secureWatermark2, name: "FLASH_SECWM2R1")
                .WithValueField(0, 7, name: "SECWM2_PSTRT")
                .WithReservedBits(7, 9)
                .WithValueField(16, 7, name: "SECWM2_PEND")
                .WithReservedBits(23, 9)
            ;

            // RM p.352, reset 0xXXXXXXXX, ST production value 0x7F807F80
            Registers.SECWM2R2.Define(this, 0x7F807F80, name: "FLASH_SECWM2R2")
                .WithReservedBits(0, 16)
                .WithValueField(16, 7, name: "HDP2_PEND")
                .WithReservedBits(23, 8)
                .WithFlag(31, name: "HDP2EN")
            ;

            // RM p.353, reset 0xXXXXXXXX, ST production value 0xFF80FFFF
            Registers.WRP2AR.Define(this, 0xFF80FFFF, name: "FLASH_WRP2AR")
                .WithValueField(0, 7, name: "WRP2A_PSTRT")
                .WithReservedBits(7, 9)
                .WithValueField(16, 7, name: "WRP2A_PEND")
                .WithReservedBits(23, 8)
                .WithFlag(31, name: "UNLOCK")
            ;

            // RM p.354, reset 0xXXXXXXXX, ST production value 0xFF80FFFF
            Registers.WRP2BR.Define(this, 0xFF80FFFF, name: "FLASH_WRP2BR")
                .WithValueField(0, 7, name: "WRP2B_PSTRT")
                .WithReservedBits(7, 9)
                .WithValueField(16, 7, name: "WRP2B_PEND")
                .WithReservedBits(23, 8)
                .WithFlag(31, name: "UNLOCK")
            ;

            // RM p.355, reset 0x00000000
            Registers.OEM1KEYR1.Define(this, 0x00000000, name: "FLASH_OEM1KEYR1")
                .WithReservedBits(0, 32)
            ;

            // RM p.355, reset 0x00000000
            Registers.OEM1KEYR2.Define(this, 0x00000000, name: "FLASH_OEM1KEYR2")
                .WithReservedBits(0, 32)
            ;

            // RM p.356, reset 0x00000000
            Registers.OEM2KEYR1.Define(this, 0x00000000, name: "FLASH_OEM2KEYR1")
                .WithReservedBits(0, 32)
            ;

            // RM p.356, reset 0x00000000
            Registers.OEM2KEYR2.Define(this, 0x00000000, name: "FLASH_OEM2KEYR2")
                .WithReservedBits(0, 32)
            ;

            // RM p.357, reset 0x00000000
            Registers.SECBB1R1.Define(this, 0x00000000, name: "FLASH_SECBB1R1")
                .WithValueField(0, 32, name: "SECBB1i")
            ;

            // RM p.357, reset 0x00000000
            Registers.SECBB1R2.Define(this, 0x00000000, name: "FLASH_SECBB1R2")
                .WithValueField(0, 32, name: "SECBB1i")
            ;

            // RM p.357, reset 0x00000000
            Registers.SECBB1R3.Define(this, 0x00000000, name: "FLASH_SECBB1R3")
                .WithValueField(0, 32, name: "SECBB1i")
            ;

            // RM p.357, reset 0x00000000
            Registers.SECBB1R4.Define(this, 0x00000000, name: "FLASH_SECBB1R4")
                .WithValueField(0, 32, name: "SECBB1i")
            ;

            // RM p.357, reset 0x00000000
            Registers.SECBB2R1.Define(this, 0x00000000, name: "FLASH_SECBB2R1")
                .WithValueField(0, 32, name: "SECBB2i")
            ;

            // RM p.357, reset 0x00000000
            Registers.SECBB2R2.Define(this, 0x00000000, name: "FLASH_SECBB2R2")
                .WithValueField(0, 32, name: "SECBB2i")
            ;

            // RM p.357, reset 0x00000000
            Registers.SECBB2R3.Define(this, 0x00000000, name: "FLASH_SECBB2R3")
                .WithValueField(0, 32, name: "SECBB2i")
            ;

            // RM p.357, reset 0x00000000
            Registers.SECBB2R4.Define(this, 0x00000000, name: "FLASH_SECBB2R4")
                .WithValueField(0, 32, name: "SECBB2i")
            ;

            // RM p.358, reset 0x00000000
            Registers.SECHDPCR.Define(this, 0x00000000, name: "FLASH_SECHDPCR")
                .WithFlag(0, name: "HDP1_ACCDIS")
                .WithFlag(1, name: "HDP2_ACCDIS")
                .WithReservedBits(2, 30)
            ;

            // RM p.358, reset 0x00000000
            Registers.PRIVCFGR.Define(this, 0x00000000, name: "FLASH_PRIVCFGR")
                .WithFlag(0, out securePrivileged, name: "SPRIV")
                .WithFlag(1, out nonSecurePrivileged, name: "NSPRIV")
                .WithReservedBits(2, 30)
            ;

            // RM p.359, reset 0x00000000
            Registers.PRIVBB1R1.Define(this, 0x00000000, name: "FLASH_PRIVBB1R1")
                .WithValueField(0, 32, name: "PRIVBB1i")
            ;

            // RM p.359, reset 0x00000000
            Registers.PRIVBB1R2.Define(this, 0x00000000, name: "FLASH_PRIVBB1R2")
                .WithValueField(0, 32, name: "PRIVBB1i")
            ;

            // RM p.359, reset 0x00000000
            Registers.PRIVBB1R3.Define(this, 0x00000000, name: "FLASH_PRIVBB1R3")
                .WithValueField(0, 32, name: "PRIVBB1i")
            ;

            // RM p.359, reset 0x00000000
            Registers.PRIVBB1R4.Define(this, 0x00000000, name: "FLASH_PRIVBB1R4")
                .WithValueField(0, 32, name: "PRIVBB1i")
            ;

            // RM p.360, reset 0x00000000
            Registers.PRIVBB2R1.Define(this, 0x00000000, name: "FLASH_PRIVBB2R1")
                .WithValueField(0, 32, name: "PRIVBB2i")
            ;

            // RM p.360, reset 0x00000000
            Registers.PRIVBB2R2.Define(this, 0x00000000, name: "FLASH_PRIVBB2R2")
                .WithValueField(0, 32, name: "PRIVBB2i")
            ;

            // RM p.360, reset 0x00000000
            Registers.PRIVBB2R3.Define(this, 0x00000000, name: "FLASH_PRIVBB2R3")
                .WithValueField(0, 32, name: "PRIVBB2i")
            ;

            // RM p.360, reset 0x00000000
            Registers.PRIVBB2R4.Define(this, 0x00000000, name: "FLASH_PRIVBB2R4")
                .WithValueField(0, 32, name: "PRIVBB2i")
            ;
        }

        // The bits common to FLASH_NSSR and FLASH_SECSR.
        private DoubleWordRegister DefineStatusRegister(Registers register, FlashInterface flash, string name)
        {
            return register.Define(this, 0x00000000, name: name)
                .WithFlag(0, out flash.EndOfOperation, FieldMode.Read | FieldMode.WriteOneToClear, name: "EOP")
                .WithFlag(1, out flash.OperationError, FieldMode.Read | FieldMode.WriteOneToClear, name: "OPERR")
                .WithReservedBits(2, 1)
                .WithFlag(3, out flash.ProgrammingError, FieldMode.Read | FieldMode.WriteOneToClear, name: "PROGERR")
                .WithFlag(4, out flash.WriteProtectionError, FieldMode.Read | FieldMode.WriteOneToClear, name: "WRPERR")
                .WithFlag(5, out flash.AlignmentError, FieldMode.Read | FieldMode.WriteOneToClear, name: "PGAERR")
                .WithFlag(6, out flash.SizeError, FieldMode.Read | FieldMode.WriteOneToClear, name: "SIZERR")
                .WithFlag(7, out flash.ProgrammingSequenceError, FieldMode.Read | FieldMode.WriteOneToClear, name: "PGSERR")
                .WithFlag(16, FieldMode.Read, valueProviderCallback: _ => false, name: "BSY")
                .WithFlag(17, FieldMode.Read, valueProviderCallback: _ => false, name: "WDW")
                .WithWriteCallback((_, __) => UpdateInterrupt(flash));
        }

        // The bits common to FLASH_NSCR and FLASH_SECCR.
        private DoubleWordRegister DefineControlRegister(Registers register, FlashInterface flash, string name)
        {
            return register.Define(this, 0x00000000, name: name)
                .WithFlag(0, out flash.Programming, changeCallback: (_, __) => UpdateProgrammingHook(), name: "PG")
                .WithFlag(1, out flash.PageErase, name: "PER")
                .WithFlag(2, out flash.MassEraseBank1, name: "MER1")
                .WithValueField(3, 7, out flash.PageNumber, name: "PNB")
                .WithReservedBits(10, 1)
                .WithFlag(11, out flash.BankSelect, name: "BKER")
                .WithReservedBits(12, 2)
                .WithFlag(14, out flash.BurstProgramming, name: "BWR")
                .WithFlag(15, out flash.MassEraseBank2, name: "MER2")
                .WithFlag(16, out flash.EraseStart, name: "STRT")
                .WithFlag(24, out flash.EndOfOperationInterruptEnable, name: "EOPIE")
                .WithFlag(25, out flash.ErrorInterruptEnable, name: "ERRIE")
                .WithWriteCallback((_, __) =>
                    {
                        if(flash.EraseStart.Value)
                        {
                            StartErase(flash);
                        }
                        UpdateInterrupt(flash);
                    });
        }

        // RM0456 section 7.7, Table 72, Tables 75-77. keptBits: bits that the write must not change.
        private bool CheckRegisterAccess(long offset, bool write, out uint keptBits)
        {
            keptBits = 0;
            var register = (Registers)offset;
            var secureRegister = Array.IndexOf(SecureRegisters, register) >= 0;
            var secureBlockRegister = IsBlockRegister(register, Registers.SECBB1R1, Registers.SECBB2R1);
            var privilegeBlockRegister = IsBlockRegister(register, Registers.PRIVBB1R1, Registers.PRIVBB2R1);
            if(!trustZoneEnabled && (secureRegister || secureBlockRegister))
            {
                return false;
            }
            STM32U5_TrustZoneContext.TryGetTransaction(sysbus, out var secureAccess, out var privileged);
            secureAccess &= trustZoneEnabled;
            if(trustZoneEnabled && !secureAccess && (secureRegister || (secureBlockRegister && write)))
            {
                this.Log(LogLevel.Debug, "Non-secure {0} of {1} denied", write ? "write" : "read", register);
                illegalAccessController?.ReportIllegalAccess(IllegalAccessRegister, RegisterIllegalAccessBit);
                return false;
            }
            if(secureBlockRegister || privilegeBlockRegister)
            {
                if(!write)
                {
                    return true;
                }
                if(!privileged && privilegeBlockRegister)
                {
                    return false;
                }
                // Unprivileged secure writes keep the bits of privileged pages; non-secure
                // writes keep the privilege bits of secure pages.
                var bank = register >= (privilegeBlockRegister ? Registers.PRIVBB2R1 : Registers.SECBB2R1) ? 1 : 0;
                var firstPage = (int)(offset - (long)(privilegeBlockRegister
                    ? (bank == 0 ? Registers.PRIVBB1R1 : Registers.PRIVBB2R1)
                    : (bank == 0 ? Registers.SECBB1R1 : Registers.SECBB2R1))) / 4 * 32;
                for(var bit = 0; bit < 32; bit++)
                {
                    var kept = privilegeBlockRegister ? !secureAccess && IsSecurePage(bank, firstPage + bit) : !privileged && IsPrivilegedPage(bank, firstPage + bit);
                    keptBits |= kept ? 1u << bit : 0;
                }
                return true;
            }
            if(register == Registers.PRIVCFGR)
            {
                if(write && !privileged)
                {
                    return false;
                }
                // SPRIV is written by secure accesses only.
                keptBits = write && !secureAccess ? 1u : 0;
                return true;
            }
            if(!privileged && (secureRegister ? securePrivileged.Value : nonSecurePrivileged.Value))
            {
                return false;
            }
            return true;
        }

        private static bool IsBlockRegister(Registers register, Registers bank1First, Registers bank2First)
        {
            return (register >= bank1First && register < bank1First + BlockRegisters * 4)
                || (register >= bank2First && register < bank2First + BlockRegisters * 4);
        }

        // RM0456 sections 7.5.2 and 7.5.4: the watermark area of the bank or the block-based bit.
        // The watermarks are the option bytes loaded at reset (the constructor values): a write
        // to FLASH_SECWMxR1 takes effect only after an option byte loading, not modelled.
        private bool IsSecurePage(int bank, int page)
        {
            if(!trustZoneEnabled)
            {
                return false;
            }
            var watermark = bank == 0 ? secureWatermark1 : secureWatermark2;
            var start = watermark & 0x7F;
            var end = (watermark >> 16) & 0x7F;
            if(start <= page && page <= end)
            {
                return true;
            }
            return IsBlockBitSet(bank == 0 ? Registers.SECBB1R1 : Registers.SECBB2R1, page);
        }

        private bool IsPrivilegedPage(int bank, int page)
        {
            return IsBlockBitSet(bank == 0 ? Registers.PRIVBB1R1 : Registers.PRIVBB2R1, page);
        }

        private bool IsBlockBitSet(Registers firstRegister, int page)
        {
            var bits = base.ReadDoubleWord((long)firstRegister + page / 32 * 4);
            return (bits & (1u << (page % 32))) != 0;
        }

        // The number of secure pages of the bank.
        private int SecurePages(int bank)
        {
            var count = 0;
            for(var page = 0; page < PagesPerBank; page++)
            {
                count += IsSecurePage(bank, page) ? 1 : 0;
            }
            return count;
        }

        private void StartErase(FlashInterface flash)
        {
            flash.EraseStart.Value = false;
            if(!flash.PageErase.Value && !flash.MassEraseBank1.Value && !flash.MassEraseBank2.Value)
            {
                this.Log(LogLevel.Warning, "Erase started with none of PER, MER1 and MER2 set");
                ReportOperationError(flash, flash.ProgrammingSequenceError);
                return;
            }

            var bankSize = memory.Size / 2;
            if(flash.MassEraseBank1.Value || flash.MassEraseBank2.Value)
            {
                // RM0456 Table 70: a bank of the other security, or with pages of both, is not
                // erased.
                for(var bank = 0; bank < 2; bank++)
                {
                    var selected = bank == 0 ? flash.MassEraseBank1.Value : flash.MassEraseBank2.Value;
                    var securePages = SecurePages(bank);
                    if(selected && securePages != (flash.Secure ? PagesPerBank : 0))
                    {
                        this.Log(LogLevel.Warning, "{0} erase of bank {1} with {2} secure pages denied", flash.Name, bank + 1, securePages);
                        illegalAccessController?.ReportIllegalAccess(IllegalAccessRegister, MemoryIllegalAccessBit);
                        ReportOperationError(flash, flash.WriteProtectionError);
                        return;
                    }
                }
                if(flash.MassEraseBank1.Value)
                {
                    this.DebugLog("Erasing bank 1");
                    memory.ZeroRange(0, bankSize);
                    ForgetProgrammedQuadWords(0, bankSize);
                }
                if(flash.MassEraseBank2.Value)
                {
                    this.DebugLog("Erasing bank 2");
                    memory.ZeroRange(bankSize, bankSize);
                    ForgetProgrammedQuadWords(bankSize, bankSize);
                }
            }
            else
            {
                var bank = flash.BankSelect.Value ? 1 : 0;
                var page = (int)flash.PageNumber.Value;
                var offset = bank * bankSize + (long)page * PageSize;
                if(offset + PageSize > memory.Size)
                {
                    this.Log(LogLevel.Warning, "Page {0} of bank {1} is outside the flash memory", page, bank + 1);
                    ReportOperationError(flash, flash.ProgrammingSequenceError);
                    return;
                }
                // RM0456 Table 68: a page of the other security is not erased.
                if(IsSecurePage(bank, page) != flash.Secure)
                {
                    this.Log(LogLevel.Warning, "{0} erase of page {1} of bank {2} denied: the page is {3}", flash.Name, page, bank + 1, flash.Secure ? "non-secure" : "secure");
                    ReportOperationError(flash, flash.WriteProtectionError);
                    return;
                }
                this.DebugLog("Erasing page {0} of bank {1}", page, bank + 1);
                memory.ZeroRange(offset, PageSize);
                ForgetProgrammedQuadWords(offset, PageSize);
            }

            if(flash.EndOfOperationInterruptEnable.Value)
            {
                flash.EndOfOperation.Value = true;
            }
            UpdateInterrupt(flash);
        }

        // Programming (RM0456 section 7.3.7): while PG is set, the writes of the CPU to the flash
        // memory are observed through a memory access hook, as in STM32H7_FlashController. The
        // hook runs after the write, so the data reaches the memory in any case; the hook checks
        // the sequence and sets the error flags of RM0456 section 7.3.9. Writes while PG is
        // cleared are not observed.
        private void UpdateProgrammingHook()
        {
            var value = nonSecure.Programming.Value || secure.Programming.Value;
            var cpus = machine.GetSystemBus(this).GetCPUs().OfType<ICPUWithMemoryAccessHooks>();
            foreach(var cpu in cpus)
            {
                cpu.SetHookAtMemoryAccess(value ? (MemoryAccessHook)OnMemoryProgramWrite : null);
            }
            programmingHookSet = value;
            nonSecure.ProgramBufferCount = 0;
            secure.ProgramBufferCount = 0;
        }

        private void OnMemoryProgramWrite(ulong _, MemoryOperation operation, ulong __, ulong physicalAddress, uint width, ulong value)
        {
            if(operation != MemoryOperation.MemoryWrite)
            {
                return;
            }
            var bus = machine.GetSystemBus(this);
            if(bus.WhatIsAt(physicalAddress)?.Peripheral != memory)
            {
                return;
            }
            var alias = bus.GetRegistrationPoints(memory).First(x => x.Range.Contains(physicalAddress)).Range.StartAddress;
            // The secure alias carries the secure writes (the core faults non-secure ones).
            var flash = trustZoneEnabled && alias == SecureAliasAddress ? secure : nonSecure;
            if(!flash.Programming.Value)
            {
                return;
            }
            HandleProgramWrite(flash, (long)(physicalAddress - alias), width, (uint)value);
        }

        private void HandleProgramWrite(FlashInterface flash, long offset, uint width, uint value)
        {
            // A write after a programming error is a sequence error
            if(flash.ProgrammingError.Value || flash.AlignmentError.Value || flash.SizeError.Value
                || flash.ProgrammingSequenceError.Value || flash.WriteProtectionError.Value)
            {
                ReportProgrammingError(flash, flash.ProgrammingSequenceError, "write after a previous programming error");
                return;
            }
            // RM0456 Table 68: a page of the other security is not programmed.
            var bankSize = memory.Size / 2;
            var bank = (int)(offset / bankSize);
            var page = (int)(offset % bankSize / PageSize);
            if(IsSecurePage(bank, page) != flash.Secure)
            {
                illegalAccessController?.ReportIllegalAccess(IllegalAccessRegister, MemoryIllegalAccessBit);
                ReportProgrammingError(flash, flash.WriteProtectionError, "{0} write to page {1} of bank {2}", flash.Name, page, bank + 1);
                return;
            }
            // Only 32-bit data can be written
            if(width != 4)
            {
                ReportProgrammingError(flash, flash.SizeError, "{0}-byte write", width);
                return;
            }
            // Quad-word programming, or 8 quad-words in burst mode
            var groupSize = flash.BurstProgramming.Value ? BurstSize : QuadWordSize;
            if(flash.ProgramBufferCount == 0)
            {
                if(offset % groupSize != 0)
                {
                    ReportProgrammingError(flash, flash.AlignmentError, "first word at 0x{0:X} not aligned to {1} bytes", offset, groupSize);
                    return;
                }
                flash.ProgramBufferStart = offset;
            }
            else if(offset != flash.ProgramBufferStart + flash.ProgramBufferCount)
            {
                ReportProgrammingError(flash, flash.AlignmentError, "word at 0x{0:X} does not follow the previous one", offset);
                return;
            }

            var quadWord = offset & ~(long)(QuadWordSize - 1);
            if(offset == quadWord)
            {
                // The other words of the quad-word are not written yet
                flash.QuadWordWasErased = IsErased(quadWord);
                flash.QuadWordWasZero = IsZero(quadWord);
                flash.QuadWordIsZero = true;
            }
            flash.QuadWordIsZero &= value == 0;
            flash.ProgramBufferCount += (int)width;

            if(offset + width == quadWord + QuadWordSize)
            {
                // A quad-word that is not erased can only be programmed to all zeros, once
                if(!flash.QuadWordWasErased && (!flash.QuadWordIsZero || flash.QuadWordWasZero))
                {
                    ReportProgrammingError(flash, flash.ProgrammingError, "quad-word at 0x{0:X} is not erased", quadWord);
                    return;
                }
                programmedQuadWords[quadWord] = flash.QuadWordIsZero;
                this.DebugLog("Programmed quad-word at offset 0x{0:X} of the flash memory", quadWord);
            }
            if(flash.ProgramBufferCount == groupSize)
            {
                flash.ProgramBufferCount = 0;
                if(flash.EndOfOperationInterruptEnable.Value)
                {
                    flash.EndOfOperation.Value = true;
                }
                UpdateInterrupt(flash);
            }
        }

        // The state of a quad-word before the current write: known if it was programmed since the
        // last erase, otherwise taken from the words that are not written yet (the first word is
        // assumed to match them).
        private bool IsErased(long quadWord)
        {
            if(programmedQuadWords.ContainsKey(quadWord))
            {
                return false;
            }
            for(var i = 4; i < QuadWordSize; i += 4)
            {
                if(memory.ReadDoubleWord(quadWord + i) != ErasedWord)
                {
                    return false;
                }
            }
            return true;
        }

        private bool IsZero(long quadWord)
        {
            if(programmedQuadWords.TryGetValue(quadWord, out var zero))
            {
                return zero;
            }
            for(var i = 4; i < QuadWordSize; i += 4)
            {
                if(memory.ReadDoubleWord(quadWord + i) != 0)
                {
                    return false;
                }
            }
            return true;
        }

        private void ReportProgrammingError(FlashInterface flash, IFlagRegisterField flag, string reason, params object[] arguments)
        {
            this.Log(LogLevel.Warning, "Flash programming error: " + reason, arguments);
            flash.ProgramBufferCount = 0;
            ReportOperationError(flash, flag);
        }

        private void ReportOperationError(FlashInterface flash, IFlagRegisterField flag)
        {
            flag.Value = true;
            if(flash.ErrorInterruptEnable.Value)
            {
                flash.OperationError.Value = true;
            }
            UpdateInterrupt(flash);
        }

        private void ForgetProgrammedQuadWords(long start, long size)
        {
            foreach(var quadWord in programmedQuadWords.Keys.Where(x => x >= start && x < start + size).ToList())
            {
                programmedQuadWords.Remove(quadWord);
            }
        }

        private void UpdateInterrupt(FlashInterface flash)
        {
            var pending = (flash.EndOfOperationInterruptEnable.Value && flash.EndOfOperation.Value)
                || (flash.ErrorInterruptEnable.Value && flash.OperationError.Value);
            flash.Interrupt.Set(pending);
        }

        private bool programmingHookSet;
        private IFlagRegisterField invertSecurity;
        private IFlagRegisterField securePrivileged;
        private IFlagRegisterField nonSecurePrivileged;

        // Quad-words programmed since the last erase; the value tells whether all bits are zero
        private readonly Dictionary<long, bool> programmedQuadWords = new Dictionary<long, bool>();

        private readonly MappedMemory memory;
        private readonly IBusController sysbus;
        private readonly bool trustZoneEnabled;
        private readonly STM32U5_GTZC_TZIC illegalAccessController;
        private readonly uint secureWatermark1;
        private readonly uint secureWatermark2;
        private readonly FlashInterface nonSecure;
        private readonly FlashInterface secure;
        private readonly LockRegister optionLock;

        private const byte ErasedByte = 0xFF;
        private const uint ErasedWord = 0xFFFFFFFF;
        private const int QuadWordSize = 16;
        private const int BurstSize = 8 * QuadWordSize;
        // 8 Kbytes (stm32u575xx.h FLASH_PAGE_SIZE), 128 pages per bank (FLASH_PAGE_NB).
        private const long PageSize = 0x2000;
        private const int PagesPerBank = 128;
        private const int BlockRegisters = 4;
        private const uint LockBit = 1u << 31;
        private const uint OptionRegisterProductionValue = 0x1FEFF8AA;
        private const uint TrustZoneEnableBit = 1u << 31;
        // ST production value of FLASH_SECWMxR1: SECWMx_PSTRT = 0, SECWMx_PEND = 0x7F.
        private const uint SecureWatermarkProductionValue = 0xFFFFFF80;
        private const ulong SecureAliasAddress = 0x0C000000;
        // GTZC1_TZIC_IER4 FLASH_REGIE and FLASHIE (stm32u575xx.h GTZC_CFGR4_FLASH_REG_Pos,
        // GTZC_CFGR4_FLASH_Pos).
        private const int IllegalAccessRegister = 4;
        private const int RegisterIllegalAccessBit = 1;
        private const int MemoryIllegalAccessBit = 2;
        // FLASH_KEY1/2 and FLASH_OPTKEY1/2 (RM0456 sections 7.9.2 to 7.9.4).
        private static readonly uint[] ControlKeys = { 0x45670123, 0xCDEF89AB };
        private static readonly uint[] OptionKeys = { 0x08192A3B, 0x4C5D6E7F };

        // RM0456 section 7.9: "This register can not be written if OPTLOCK bit is set".
        private static readonly Registers[] OptionLockedRegisters =
        {
            Registers.NSBOOTADD0R, Registers.NSBOOTADD1R, Registers.SECBOOTADD0R,
            Registers.SECWM1R1, Registers.SECWM1R2, Registers.WRP1AR, Registers.WRP1BR,
            Registers.SECWM2R1, Registers.SECWM2R2, Registers.WRP2AR, Registers.WRP2BR
        };

        // RM0456 section 7.9: "This register is secure" (FLASH_SECBBxRy separately).
        private static readonly Registers[] SecureRegisters =
        {
            Registers.SECKEYR, Registers.SECSR, Registers.SECCR, Registers.SECBOOTADD0R,
            Registers.SECWM1R1, Registers.SECWM1R2, Registers.SECWM2R1, Registers.SECWM2R2,
            Registers.SECHDPCR
        };

        // One erase and programming interface: FLASH_NSKEYR/NSSR/NSCR or FLASH_SECKEYR/SECSR/SECCR.
        private class FlashInterface
        {
            public FlashInterface(bool secure, LockRegister lockRegister, GPIO interrupt)
            {
                Secure = secure;
                Lock = lockRegister;
                Interrupt = interrupt;
            }

            public string Name => Secure ? "Secure" : "Non-secure";

            public readonly bool Secure;
            public readonly LockRegister Lock;
            public readonly GPIO Interrupt;

            public IFlagRegisterField EndOfOperation;
            public IFlagRegisterField OperationError;
            public IFlagRegisterField ProgrammingError;
            public IFlagRegisterField WriteProtectionError;
            public IFlagRegisterField AlignmentError;
            public IFlagRegisterField SizeError;
            public IFlagRegisterField ProgrammingSequenceError;
            public IFlagRegisterField Programming;
            public IFlagRegisterField PageErase;
            public IFlagRegisterField MassEraseBank1;
            public IFlagRegisterField MassEraseBank2;
            public IValueRegisterField PageNumber;
            public IFlagRegisterField BankSelect;
            public IFlagRegisterField BurstProgramming;
            public IFlagRegisterField EraseStart;
            public IFlagRegisterField EndOfOperationInterruptEnable;
            public IFlagRegisterField ErrorInterruptEnable;

            public int ProgramBufferCount;
            public long ProgramBufferStart;
            public bool QuadWordWasErased;
            public bool QuadWordWasZero;
            public bool QuadWordIsZero;
        }

        private enum Registers
        {
            ACR = 0x000, // FLASH_ACR
            NSKEYR = 0x008, // FLASH_NSKEYR
            SECKEYR = 0x00C, // FLASH_SECKEYR
            OPTKEYR = 0x010, // FLASH_OPTKEYR
            PDKEY1R = 0x018, // FLASH_PDKEY1R
            PDKEY2R = 0x01C, // FLASH_PDKEY2R
            NSSR = 0x020, // FLASH_NSSR
            SECSR = 0x024, // FLASH_SECSR
            NSCR = 0x028, // FLASH_NSCR
            SECCR = 0x02C, // FLASH_SECCR
            ECCR = 0x030, // FLASH_ECCR
            OPSR = 0x034, // FLASH_OPSR
            OPTR = 0x040, // FLASH_OPTR
            NSBOOTADD0R = 0x044, // FLASH_NSBOOTADD0R
            NSBOOTADD1R = 0x048, // FLASH_NSBOOTADD1R
            SECBOOTADD0R = 0x04C, // FLASH_SECBOOTADD0R
            SECWM1R1 = 0x050, // FLASH_SECWM1R1
            SECWM1R2 = 0x054, // FLASH_SECWM1R2
            WRP1AR = 0x058, // FLASH_WRP1AR
            WRP1BR = 0x05C, // FLASH_WRP1BR
            SECWM2R1 = 0x060, // FLASH_SECWM2R1
            SECWM2R2 = 0x064, // FLASH_SECWM2R2
            WRP2AR = 0x068, // FLASH_WRP2AR
            WRP2BR = 0x06C, // FLASH_WRP2BR
            OEM1KEYR1 = 0x070, // FLASH_OEM1KEYR1
            OEM1KEYR2 = 0x074, // FLASH_OEM1KEYR2
            OEM2KEYR1 = 0x078, // FLASH_OEM2KEYR1
            OEM2KEYR2 = 0x07C, // FLASH_OEM2KEYR2
            SECBB1R1 = 0x080, // FLASH_SECBB1R1
            SECBB1R2 = 0x084, // FLASH_SECBB1R2
            SECBB1R3 = 0x088, // FLASH_SECBB1R3
            SECBB1R4 = 0x08C, // FLASH_SECBB1R4
            SECBB2R1 = 0x0A0, // FLASH_SECBB2R1
            SECBB2R2 = 0x0A4, // FLASH_SECBB2R2
            SECBB2R3 = 0x0A8, // FLASH_SECBB2R3
            SECBB2R4 = 0x0AC, // FLASH_SECBB2R4
            SECHDPCR = 0x0C0, // FLASH_SECHDPCR
            PRIVCFGR = 0x0C4, // FLASH_PRIVCFGR
            PRIVBB1R1 = 0x0D0, // FLASH_PRIVBB1R1
            PRIVBB1R2 = 0x0D4, // FLASH_PRIVBB1R2
            PRIVBB1R3 = 0x0D8, // FLASH_PRIVBB1R3
            PRIVBB1R4 = 0x0DC, // FLASH_PRIVBB1R4
            PRIVBB2R1 = 0x0F0, // FLASH_PRIVBB2R1
            PRIVBB2R2 = 0x0F4, // FLASH_PRIVBB2R2
            PRIVBB2R3 = 0x0F8, // FLASH_PRIVBB2R3
            PRIVBB2R4 = 0x0FC, // FLASH_PRIVBB2R4
        }
    }
}
