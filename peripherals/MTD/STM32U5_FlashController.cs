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
// and extended to the two banks of STM32U5 (BKER, MER1, MER2).
// Register layout and bit positions: cmsis-device-u5 v1.4.3 stm32u575xx.h.
// Reset values: RM0456 Rev 7 (variant STM32U575); option byte registers hold the documented
// "ST production value".
//
// Limitations:
// - Programming: while PG is set, the CPU writes to the flash memory are checked (quad-word or
//   burst alignment, 32-bit accesses, target erased or programmed to zero) and PROGERR, SIZERR,
//   PGAERR, PGSERR and EOP are set as in RM0456 section 7.3.9. The data is written to the memory
//   also when an error is reported, and writes while PG is cleared are neither checked nor
//   blocked. WRPERR (write protection) is not reported.
// - Erase completes immediately (BSY is always 0). Option byte modification, OBL_LAUNCH,
//   bank swapping, secure and privileged access control, ECC and power-down are not modelled.
//

using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Logging.Profiling;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Memory;

namespace Antmicro.Renode.Peripherals.MTD
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class STM32U5_FlashController : STM32_FlashController, IKnownSize
    {
        public STM32U5_FlashController(IMachine machine, MappedMemory flash) : base(machine)
        {
            memory = flash;
            // Erased flash reads as 0xFF.
            memory.ResetByte = ErasedByte;
            controlLock = new LockRegister(this, nameof(controlLock), NonSecureKeys);
            optionLock = new LockRegister(this, nameof(optionLock), OptionKeys);
            IRQ = new GPIO();
            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            controlLock.Reset();
            optionLock.Reset();
            base.Reset();
            IRQ.Unset();
            // PG is cleared by the reset without its change callback
            if(programmingHookSet)
            {
                HandleProgrammingEnableChange(false);
            }
        }

        public override void WriteDoubleWord(long offset, uint value)
        {
            // FLASH_NSCR is write-protected while LOCK is set (RM0456 section 7.9.9); writing 1 to
            // LOCK itself is always possible.
            if(offset == (long)Registers.NSCR && controlLock.IsLocked && (value & LockBit) == 0)
            {
                this.Log(LogLevel.Warning, "Write to FLASH_NSCR ignored: register is locked");
                return;
            }
            base.WriteDoubleWord(offset, value);
        }

        public long Size => 0x400;

        public GPIO IRQ { get; }

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

            // RM0456 section 7.9.2.
            Registers.NSKEYR.Define(this, 0x00000000, name: "FLASH_NSKEYR")
                .WithValueField(0, 32, FieldMode.Write, writeCallback: (_, value) => controlLock.ConsumeValue((uint)value), name: "NSKEY")
            ;

            // RM p.331, reset 0x00000000
            Registers.SECKEYR.Define(this, 0x00000000, name: "FLASH_SECKEYR")
                .WithReservedBits(0, 32)
            ;

            // RM0456 section 7.9.4. Accepted only while FLASH_NSCR is unlocked.
            Registers.OPTKEYR.Define(this, 0x00000000, name: "FLASH_OPTKEYR")
                .WithValueField(0, 32, FieldMode.Write, writeCallback: (_, value) =>
                    {
                        if(!controlLock.IsLocked)
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

            // RM0456 section 7.9.7. EOP and OPERR are set only with EOPIE/ERRIE; flags clear on 1.
            Registers.NSSR.Define(this, 0x00000000, name: "FLASH_NSSR")
                .WithFlag(0, out endOfOperation, FieldMode.Read | FieldMode.WriteOneToClear, name: "EOP")
                .WithFlag(1, out operationError, FieldMode.Read | FieldMode.WriteOneToClear, name: "OPERR")
                .WithReservedBits(2, 1)
                .WithFlag(3, out programmingError, FieldMode.Read | FieldMode.WriteOneToClear, name: "PROGERR")
                .WithFlag(4, FieldMode.Read | FieldMode.WriteOneToClear, name: "WRPERR")
                .WithFlag(5, out alignmentError, FieldMode.Read | FieldMode.WriteOneToClear, name: "PGAERR")
                .WithFlag(6, out sizeError, FieldMode.Read | FieldMode.WriteOneToClear, name: "SIZERR")
                .WithFlag(7, out programmingSequenceError, FieldMode.Read | FieldMode.WriteOneToClear, name: "PGSERR")
                .WithReservedBits(8, 5)
                .WithFlag(13, FieldMode.Read | FieldMode.WriteOneToClear, name: "OPTWERR")
                .WithReservedBits(14, 2)
                .WithFlag(16, FieldMode.Read, valueProviderCallback: _ => false, name: "BSY")
                .WithFlag(17, FieldMode.Read, valueProviderCallback: _ => false, name: "WDW")
                .WithFlag(18, FieldMode.Read, name: "OEM1LOCK")
                .WithFlag(19, FieldMode.Read, name: "OEM2LOCK")
                .WithFlag(20, FieldMode.Read, name: "PD1")
                .WithFlag(21, FieldMode.Read, name: "PD2")
                .WithReservedBits(22, 10)
                .WithWriteCallback((_, __) => UpdateInterrupt())
            ;

            // RM p.335, reset 0x00000000
            Registers.SECSR.Define(this, 0x00000000, name: "FLASH_SECSR")
                .WithFlag(0, name: "EOP")
                .WithFlag(1, name: "OPERR")
                .WithReservedBits(2, 1)
                .WithFlag(3, name: "PROGERR")
                .WithFlag(4, name: "WRPERR")
                .WithFlag(5, name: "PGAERR")
                .WithFlag(6, name: "SIZERR")
                .WithFlag(7, name: "PGSERR")
                .WithReservedBits(8, 8)
                .WithFlag(16, FieldMode.Read, name: "BSY")
                .WithFlag(17, FieldMode.Read, name: "WDW")
                .WithReservedBits(18, 14)
            ;

            // RM0456 section 7.9.9, reset 0xC000 0000 (LOCK and OPTLOCK set).
            Registers.NSCR.Define(this, 0x00000000, name: "FLASH_NSCR")
                .WithFlag(0, out programmingEnabled, changeCallback: (_, value) => HandleProgrammingEnableChange(value), name: "PG")
                .WithFlag(1, out pageErase, name: "PER")
                .WithFlag(2, out massEraseBank1, name: "MER1")
                .WithValueField(3, 7, out pageNumber, name: "PNB")
                .WithReservedBits(10, 1)
                .WithFlag(11, out bankSelect, name: "BKER")
                .WithReservedBits(12, 2)
                .WithFlag(14, out burstProgramming, name: "BWR")
                .WithFlag(15, out massEraseBank2, name: "MER2")
                .WithFlag(16, out eraseStart, name: "STRT")
                .WithFlag(17, FieldMode.Read | FieldMode.Write, valueProviderCallback: _ => false, writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            this.Log(LogLevel.Warning, "Option byte modification (OPTSTRT) is not supported");
                        }
                    }, name: "OPTSTRT")
                .WithReservedBits(18, 6)
                .WithFlag(24, out endOfOperationInterruptEnable, name: "EOPIE")
                .WithFlag(25, out errorInterruptEnable, name: "ERRIE")
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
                .WithFlag(31, valueProviderCallback: _ => controlLock.IsLocked, writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            optionLock.Lock();
                            controlLock.Lock();
                        }
                    }, name: "LOCK")
                .WithWriteCallback((_, __) =>
                    {
                        if(eraseStart.Value)
                        {
                            StartErase();
                        }
                        UpdateInterrupt();
                    })
            ;

            // RM p.338, reset 0x80000000
            Registers.SECCR.Define(this, 0x80000000, name: "FLASH_SECCR")
                .WithFlag(0, name: "PG")
                .WithFlag(1, name: "PER")
                .WithFlag(2, name: "MER1")
                .WithValueField(3, 7, name: "PNB")
                .WithReservedBits(10, 1)
                .WithFlag(11, name: "BKER")
                .WithReservedBits(12, 2)
                .WithFlag(14, name: "BWR")
                .WithFlag(15, name: "MER2")
                .WithFlag(16, name: "STRT")
                .WithReservedBits(17, 7)
                .WithFlag(24, name: "EOPIE")
                .WithFlag(25, name: "ERRIE")
                .WithReservedBits(26, 3)
                .WithFlag(29, name: "INV")
                .WithReservedBits(30, 1)
                .WithFlag(31, name: "LOCK")
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

            // RM p.342, reset 0xXXXXXXXX, ST production value 0x1FEFF8AA
            Registers.OPTR.Define(this, 0x1FEFF8AA, name: "FLASH_OPTR")
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
            Registers.SECWM1R1.Define(this, 0xFFFFFF80, name: "FLASH_SECWM1R1")
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
            Registers.SECWM2R1.Define(this, 0xFFFFFF80, name: "FLASH_SECWM2R1")
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
                .WithReservedBits(0, 32)
            ;

            // RM p.357, reset 0x00000000
            Registers.SECBB1R2.Define(this, 0x00000000, name: "FLASH_SECBB1R2")
                .WithReservedBits(0, 32)
            ;

            // RM p.357, reset 0x00000000
            Registers.SECBB1R3.Define(this, 0x00000000, name: "FLASH_SECBB1R3")
                .WithReservedBits(0, 32)
            ;

            // RM p.357, reset 0x00000000
            Registers.SECBB1R4.Define(this, 0x00000000, name: "FLASH_SECBB1R4")
                .WithReservedBits(0, 32)
            ;

            // RM p.357, reset 0x00000000
            Registers.SECBB2R1.Define(this, 0x00000000, name: "FLASH_SECBB2R1")
                .WithReservedBits(0, 32)
            ;

            // RM p.357, reset 0x00000000
            Registers.SECBB2R2.Define(this, 0x00000000, name: "FLASH_SECBB2R2")
                .WithReservedBits(0, 32)
            ;

            // RM p.357, reset 0x00000000
            Registers.SECBB2R3.Define(this, 0x00000000, name: "FLASH_SECBB2R3")
                .WithReservedBits(0, 32)
            ;

            // RM p.357, reset 0x00000000
            Registers.SECBB2R4.Define(this, 0x00000000, name: "FLASH_SECBB2R4")
                .WithReservedBits(0, 32)
            ;

            // RM p.358, reset 0x00000000
            Registers.SECHDPCR.Define(this, 0x00000000, name: "FLASH_SECHDPCR")
                .WithFlag(0, name: "HDP1_ACCDIS")
                .WithFlag(1, name: "HDP2_ACCDIS")
                .WithReservedBits(2, 30)
            ;

            // RM p.358, reset 0x00000000
            Registers.PRIVCFGR.Define(this, 0x00000000, name: "FLASH_PRIVCFGR")
                .WithFlag(0, name: "SPRIV")
                .WithFlag(1, name: "NSPRIV")
                .WithReservedBits(2, 30)
            ;

            // RM p.359, reset 0x00000000
            Registers.PRIVBB1R1.Define(this, 0x00000000, name: "FLASH_PRIVBB1R1")
                .WithReservedBits(0, 32)
            ;

            // RM p.359, reset 0x00000000
            Registers.PRIVBB1R2.Define(this, 0x00000000, name: "FLASH_PRIVBB1R2")
                .WithReservedBits(0, 32)
            ;

            // RM p.359, reset 0x00000000
            Registers.PRIVBB1R3.Define(this, 0x00000000, name: "FLASH_PRIVBB1R3")
                .WithReservedBits(0, 32)
            ;

            // RM p.359, reset 0x00000000
            Registers.PRIVBB1R4.Define(this, 0x00000000, name: "FLASH_PRIVBB1R4")
                .WithReservedBits(0, 32)
            ;

            // RM p.360, reset 0x00000000
            Registers.PRIVBB2R1.Define(this, 0x00000000, name: "FLASH_PRIVBB2R1")
                .WithReservedBits(0, 32)
            ;

            // RM p.360, reset 0x00000000
            Registers.PRIVBB2R2.Define(this, 0x00000000, name: "FLASH_PRIVBB2R2")
                .WithReservedBits(0, 32)
            ;

            // RM p.360, reset 0x00000000
            Registers.PRIVBB2R3.Define(this, 0x00000000, name: "FLASH_PRIVBB2R3")
                .WithReservedBits(0, 32)
            ;

            // RM p.360, reset 0x00000000
            Registers.PRIVBB2R4.Define(this, 0x00000000, name: "FLASH_PRIVBB2R4")
                .WithReservedBits(0, 32)
            ;
        }

        private void StartErase()
        {
            eraseStart.Value = false;
            if(!pageErase.Value && !massEraseBank1.Value && !massEraseBank2.Value)
            {
                this.Log(LogLevel.Warning, "Erase started with none of PER, MER1 and MER2 set");
                programmingSequenceError.Value = true;
                if(errorInterruptEnable.Value)
                {
                    operationError.Value = true;
                }
                UpdateInterrupt();
                return;
            }

            var bankSize = memory.Size / 2;
            if(massEraseBank1.Value)
            {
                this.DebugLog("Erasing bank 1");
                memory.ZeroRange(0, bankSize);
                ForgetProgrammedQuadWords(0, bankSize);
            }
            if(massEraseBank2.Value)
            {
                this.DebugLog("Erasing bank 2");
                memory.ZeroRange(bankSize, bankSize);
                ForgetProgrammedQuadWords(bankSize, bankSize);
            }
            if(pageErase.Value && !massEraseBank1.Value && !massEraseBank2.Value)
            {
                var offset = (bankSelect.Value ? bankSize : 0) + (long)pageNumber.Value * PageSize;
                if(offset + PageSize > memory.Size)
                {
                    this.Log(LogLevel.Warning, "Page {0} of bank {1} is outside the flash memory", pageNumber.Value, bankSelect.Value ? 2 : 1);
                    programmingSequenceError.Value = true;
                    UpdateInterrupt();
                    return;
                }
                this.DebugLog("Erasing page {0} of bank {1}", pageNumber.Value, bankSelect.Value ? 2 : 1);
                memory.ZeroRange(offset, PageSize);
                ForgetProgrammedQuadWords(offset, PageSize);
            }

            if(endOfOperationInterruptEnable.Value)
            {
                endOfOperation.Value = true;
            }
            UpdateInterrupt();
        }

        // Programming (RM0456 section 7.3.7): while PG is set, the writes of the CPU to the flash
        // memory are observed through a memory access hook, as in STM32H7_FlashController. The
        // hook runs after the write, so the data reaches the memory in any case; the hook checks
        // the sequence and sets the error flags of RM0456 section 7.3.9. Writes while PG is
        // cleared are not observed.
        private void HandleProgrammingEnableChange(bool value)
        {
            var cpus = machine.GetSystemBus(this).GetCPUs().OfType<ICPUWithMemoryAccessHooks>();
            foreach(var cpu in cpus)
            {
                cpu.SetHookAtMemoryAccess(value ? (MemoryAccessHook)OnMemoryProgramWrite : null);
            }
            programmingHookSet = value;
            programBufferCount = 0;
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
            var offset = (long)(physicalAddress - bus.GetRegistrationPoints(memory).First().Range.StartAddress);
            HandleProgramWrite(offset, width, (uint)value);
        }

        private void HandleProgramWrite(long offset, uint width, uint value)
        {
            // A write after a programming error is a sequence error
            if(programmingError.Value || alignmentError.Value || sizeError.Value || programmingSequenceError.Value)
            {
                ReportProgrammingError(programmingSequenceError, "write after a previous programming error");
                return;
            }
            // Only 32-bit data can be written
            if(width != 4)
            {
                ReportProgrammingError(sizeError, "{0}-byte write", width);
                return;
            }
            // Quad-word programming, or 8 quad-words in burst mode
            var groupSize = burstProgramming.Value ? BurstSize : QuadWordSize;
            if(programBufferCount == 0)
            {
                if(offset % groupSize != 0)
                {
                    ReportProgrammingError(alignmentError, "first word at 0x{0:X} not aligned to {1} bytes", offset, groupSize);
                    return;
                }
                programBufferStart = offset;
            }
            else if(offset != programBufferStart + programBufferCount)
            {
                ReportProgrammingError(alignmentError, "word at 0x{0:X} does not follow the previous one", offset);
                return;
            }

            var quadWord = offset & ~(long)(QuadWordSize - 1);
            if(offset == quadWord)
            {
                // The other words of the quad-word are not written yet
                quadWordWasErased = IsErased(quadWord);
                quadWordWasZero = IsZero(quadWord);
                quadWordIsZero = true;
            }
            quadWordIsZero &= value == 0;
            programBufferCount += (int)width;

            if(offset + width == quadWord + QuadWordSize)
            {
                // A quad-word that is not erased can only be programmed to all zeros, once
                if(!quadWordWasErased && (!quadWordIsZero || quadWordWasZero))
                {
                    ReportProgrammingError(programmingError, "quad-word at 0x{0:X} is not erased", quadWord);
                    return;
                }
                programmedQuadWords[quadWord] = quadWordIsZero;
                this.DebugLog("Programmed quad-word at offset 0x{0:X} of the flash memory", quadWord);
            }
            if(programBufferCount == groupSize)
            {
                programBufferCount = 0;
                if(endOfOperationInterruptEnable.Value)
                {
                    endOfOperation.Value = true;
                }
                UpdateInterrupt();
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

        private void ReportProgrammingError(IFlagRegisterField flag, string reason, params object[] arguments)
        {
            this.Log(LogLevel.Warning, "Flash programming error: " + reason, arguments);
            flag.Value = true;
            programBufferCount = 0;
            if(errorInterruptEnable.Value)
            {
                operationError.Value = true;
            }
            UpdateInterrupt();
        }

        private void ForgetProgrammedQuadWords(long start, long size)
        {
            foreach(var quadWord in programmedQuadWords.Keys.Where(x => x >= start && x < start + size).ToList())
            {
                programmedQuadWords.Remove(quadWord);
            }
        }

        private void UpdateInterrupt()
        {
            var pending = (endOfOperationInterruptEnable.Value && endOfOperation.Value)
                || (errorInterruptEnable.Value && operationError.Value);
            IRQ.Set(pending);
        }

        private IFlagRegisterField endOfOperation;
        private IFlagRegisterField operationError;
        private IFlagRegisterField programmingSequenceError;
        private IFlagRegisterField programmingError;
        private IFlagRegisterField alignmentError;
        private IFlagRegisterField sizeError;
        private IFlagRegisterField programmingEnabled;
        private IFlagRegisterField burstProgramming;
        private int programBufferCount;
        private bool programmingHookSet;
        private long programBufferStart;
        private bool quadWordWasErased;
        private bool quadWordWasZero;
        private bool quadWordIsZero;

        // Quad-words programmed since the last erase; the value tells whether all bits are zero
        private readonly Dictionary<long, bool> programmedQuadWords = new Dictionary<long, bool>();
        private IFlagRegisterField pageErase;
        private IFlagRegisterField massEraseBank1;
        private IFlagRegisterField massEraseBank2;
        private IValueRegisterField pageNumber;
        private IFlagRegisterField bankSelect;
        private IFlagRegisterField eraseStart;
        private IFlagRegisterField endOfOperationInterruptEnable;
        private IFlagRegisterField errorInterruptEnable;

        private readonly MappedMemory memory;
        private readonly LockRegister controlLock;
        private readonly LockRegister optionLock;

        private const byte ErasedByte = 0xFF;
        private const uint ErasedWord = 0xFFFFFFFF;
        private const int QuadWordSize = 16;
        private const int BurstSize = 8 * QuadWordSize;
        // 8 Kbytes (stm32u575xx.h FLASH_PAGE_SIZE).
        private const long PageSize = 0x2000;
        private const uint LockBit = 1u << 31;
        // FLASH_KEY1/2 and FLASH_OPTKEY1/2 (RM0456 sections 7.9.2 and 7.9.4).
        private static readonly uint[] NonSecureKeys = { 0x45670123, 0xCDEF89AB };
        private static readonly uint[] OptionKeys = { 0x08192A3B, 0x4C5D6E7F };

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
