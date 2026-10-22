//
// Copyright (c) 2026 Fedor Druzhinin
// Copyright (c) 2010-2024 Antmicro
//
// This file is licensed under the MIT License.
//
// STM32U5 reset and clock control (RCC), RM0456 Rev 7 section 11.
//
// Structure (ready flags derived from enable bits, clock tree computed on register change,
// system clock pushed to the NVIC through IHasFrequency, APB/AHB prescaler decoding) is adapted
// from renode-infrastructure
//   src/Emulator/Peripherals/Peripherals/Miscellaneous/STM32WBA_RCC.cs @ 7ea0541f8d9b
// Register layout and bit positions: cmsis-device-u5 v1.4.3 stm32u575xx.h.
// Reset values: RM0456 Rev 7 (variant STM32U575).
//
// Limitations:
// - Oscillators and PLLs become ready immediately when enabled and their input is available.
//   HSE and LSE are available only when hseFrequency/lseFrequency are non-zero (board option).
// - Clock security systems, MSI PLL mode, Stop/Standby behaviour and peripheral resets through
//   the *RSTR registers are not modelled. Enable and sleep-mode enable registers are storage.
// - The SysTick always counts the processor clock (HCLK); RCC_CCIPR1.SYSTICKSEL is storage.
//

using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class STM32U5_RCC : BasicDoubleWordPeripheral, IKnownSize
    {
        public STM32U5_RCC(IMachine machine, IHasFrequency nvic = null, STM32U5_PWR pwr = null,
            ulong hseFrequency = 0, ulong lseFrequency = 0, ulong lsiFrequency = DefaultLsiFrequency,
            IHasFrequency usart1 = null, IHasFrequency usart2 = null, IHasFrequency usart3 = null,
            IHasFrequency uart4 = null, IHasFrequency uart5 = null, IHasFrequency lpuart1 = null) : base(machine)
        {
            this.nvic = nvic;
            this.pwr = pwr;
            this.hseFrequency = hseFrequency;
            this.lseFrequency = lseFrequency;
            this.lsiFrequency = lsiFrequency;
            serialPorts = new[] { usart1, usart2, usart3, uart4, uart5 };
            this.lpuart1 = lpuart1;
            IRQ = new GPIO();
            pllOn = new IFlagRegisterField[PllCount];
            pllSource = new IValueRegisterField[PllCount];
            pllPrescaler = new IValueRegisterField[PllCount];
            pllFractionalEnable = new IFlagRegisterField[PllCount];
            pllROutputEnable = new IFlagRegisterField[PllCount];
            pllMultiplier = new IValueRegisterField[PllCount];
            pllDividerP = new IValueRegisterField[PllCount];
            pllDividerQ = new IValueRegisterField[PllCount];
            pllDividerR = new IValueRegisterField[PllCount];
            pllFraction = new IValueRegisterField[PllCount];
            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            base.Reset();
            systemClockStatus = SystemClockSource.Msis;
            readyState = 0;
            IRQ.Unset();
            UpdateClocks();
        }

        public override void WriteDoubleWord(long offset, uint value)
        {
            // RCC_BDCR is in the backup domain: writes need PWR_DBPR.DBP (RM0456 section 11.8.49).
            if(offset == (long)Registers.BDCR && pwr != null && !pwr.BackupDomainWriteEnabled)
            {
                this.Log(LogLevel.Warning, "Write to RCC_BDCR ignored: backup domain is write-protected (PWR_DBPR.DBP = 0)");
                return;
            }
            base.WriteDoubleWord(offset, value);
        }

        public long Size => 0x400;

        public GPIO IRQ { get; }

        public ulong SystemClockFrequency => SystemClock;

        public ulong AhbFrequency => AhbClock;

        public ulong Apb1Frequency => PrescaleApb(AhbClock, apb1Prescaler.Value);

        public ulong Apb2Frequency => PrescaleApb(AhbClock, apb2Prescaler.Value);

        public ulong Apb3Frequency => PrescaleApb(AhbClock, apb3Prescaler.Value);

        public ulong MsisFrequency => MsiRangeFrequency(msiRangeSelect.Value ? msisRange.Value : msisStandbyRange.Value);

        public ulong MsikFrequency => MsiRangeFrequency(msiRangeSelect.Value ? msikRange.Value : msikStandbyRange.Value);

        private void DefineRegisters()
        {
            // RM0456 section 11.8.1, p.512, reset 0x0000 0035.
            Registers.CR.Define(this, 0x00000035, name: "RCC_CR")
                .WithFlag(0, out msisOn, name: "MSISON")
                .WithFlag(1, out msikKernelOn, name: "MSIKERON")
                .WithFlag(2, FieldMode.Read, valueProviderCallback: _ => MsisReady, name: "MSISRDY")
                .WithFlag(3, name: "MSIPLLEN")
                .WithFlag(4, out msikOn, name: "MSIKON")
                .WithFlag(5, FieldMode.Read, valueProviderCallback: _ => MsikReady, name: "MSIKRDY")
                .WithFlag(6, name: "MSIPLLSEL")
                .WithFlag(7, name: "MSIPLLFAST")
                .WithFlag(8, out hsiOn, name: "HSION")
                .WithFlag(9, out hsiKernelOn, name: "HSIKERON")
                .WithFlag(10, FieldMode.Read, valueProviderCallback: _ => HsiReady, name: "HSIRDY")
                .WithReservedBits(11, 1)
                .WithFlag(12, out hsi48On, name: "HSI48ON")
                .WithFlag(13, FieldMode.Read, valueProviderCallback: _ => Hsi48Ready, name: "HSI48RDY")
                .WithFlag(14, out shsiOn, name: "SHSION")
                .WithFlag(15, FieldMode.Read, valueProviderCallback: _ => ShsiReady, name: "SHSIRDY")
                .WithFlag(16, out hseOn, name: "HSEON")
                .WithFlag(17, FieldMode.Read, valueProviderCallback: _ => HseReady, name: "HSERDY")
                .WithFlag(18, name: "HSEBYP")
                .WithFlag(19, name: "CSSON")
                .WithFlag(20, name: "HSEEXT")
                .WithReservedBits(21, 3)
                .WithFlag(24, out pllOn[0], name: "PLL1ON")
                .WithFlag(25, FieldMode.Read, valueProviderCallback: _ => PllReady(0), name: "PLL1RDY")
                .WithFlag(26, out pllOn[1], name: "PLL2ON")
                .WithFlag(27, FieldMode.Read, valueProviderCallback: _ => PllReady(1), name: "PLL2RDY")
                .WithFlag(28, out pllOn[2], name: "PLL3ON")
                .WithFlag(29, FieldMode.Read, valueProviderCallback: _ => PllReady(2), name: "PLL3RDY")
                .WithReservedBits(30, 2)
                .WithChangeCallback((_, __) => OnClockControlChanged())
            ;

            // RM0456 section 11.8.2, p.516, reset 0x440X XXXX. The factory calibration fields
            // (X) read as 0.
            Registers.ICSCR1.Define(this, 0x44000000, name: "RCC_ICSCR1")
                .WithValueField(0, 5, FieldMode.Read, name: "MSICAL3")
                .WithValueField(5, 5, FieldMode.Read, name: "MSICAL2")
                .WithValueField(10, 5, FieldMode.Read, name: "MSICAL1")
                .WithValueField(15, 5, FieldMode.Read, name: "MSICAL0")
                .WithReservedBits(20, 2)
                .WithFlag(22, name: "MSIBIAS")
                .WithFlag(23, out msiRangeSelect, name: "MSIRGSEL")
                .WithValueField(24, 4, out msikRange, name: "MSIKRANGE")
                .WithValueField(28, 4, out msisRange, name: "MSISRANGE")
                .WithChangeCallback((_, __) => UpdateClocks())
            ;

            // RM p.518, reset 0x00084210
            Registers.ICSCR2.Define(this, 0x00084210, name: "RCC_ICSCR2")
                .WithValueField(0, 5, name: "MSITRIM3")
                .WithValueField(5, 5, name: "MSITRIM2")
                .WithValueField(10, 5, name: "MSITRIM1")
                .WithValueField(15, 5, name: "MSITRIM0")
                .WithReservedBits(20, 12)
            ;

            // RM p.519, reset 0x00100XXX (X = factory programmed, modelled as 0)
            Registers.ICSCR3.Define(this, 0x00100000, name: "RCC_ICSCR3")
                .WithValueField(0, 12, FieldMode.Read, name: "HSICAL")
                .WithReservedBits(12, 4)
                .WithValueField(16, 5, name: "HSITRIM")
                .WithReservedBits(21, 11)
            ;

            // RM p.520, reset 0x00000XXX (X = factory programmed, modelled as 0)
            Registers.CRRCR.Define(this, 0x00000000, name: "RCC_CRRCR")
                .WithValueField(0, 9, FieldMode.Read, name: "HSI48CAL")
                .WithReservedBits(9, 23)
            ;

            // RM0456 section 11.8.6. SWS follows SW once the selected source is ready.
            Registers.CFGR1.Define(this, 0x00000000, name: "RCC_CFGR1")
                .WithValueField(0, 2, out systemClockSwitch, name: "SW")
                .WithValueField(2, 2, FieldMode.Read, valueProviderCallback: _ => (ulong)systemClockStatus, name: "SWS")
                .WithFlag(4, name: "STOPWUCK")
                .WithFlag(5, name: "STOPKERWUCK")
                .WithReservedBits(6, 18)
                .WithValueField(24, 4, name: "MCOSEL")
                .WithValueField(28, 3, name: "MCOPRE")
                .WithReservedBits(31, 1)
                .WithChangeCallback((_, __) => OnClockControlChanged())
            ;

            // RM0456 section 11.8.7, reset 0x0000 0000 for STM32U575.
            Registers.CFGR2.Define(this, 0x00000000, name: "RCC_CFGR2")
                .WithValueField(0, 4, out ahbPrescaler, name: "HPRE")
                .WithValueField(4, 3, out apb1Prescaler, name: "PPRE1")
                .WithReservedBits(7, 1)
                .WithValueField(8, 3, out apb2Prescaler, name: "PPRE2")
                .WithReservedBits(11, 5)
                .WithFlag(16, name: "AHB1DIS")
                .WithFlag(17, name: "AHB2DIS1")
                .WithFlag(18, name: "AHB2DIS2")
                .WithFlag(19, name: "APB1DIS")
                .WithFlag(20, name: "APB2DIS")
                .WithReservedBits(21, 11)
                .WithChangeCallback((_, __) => UpdateClocks())
            ;

            // RM0456 section 11.8.8.
            Registers.CFGR3.Define(this, 0x00000000, name: "RCC_CFGR3")
                .WithReservedBits(0, 4)
                .WithValueField(4, 3, out apb3Prescaler, name: "PPRE3")
                .WithReservedBits(7, 9)
                .WithFlag(16, name: "AHB3DIS")
                .WithFlag(17, name: "APB3DIS")
                .WithReservedBits(18, 14)
                .WithChangeCallback((_, __) => UpdateClocks())
            ;

            // RM0456 sections 11.8.9-11.8.17; PLLxDIVR reset 0x0101 0280 (N = 129, P = Q = R = 2).
            DefinePll(0, Registers.PLL1CFGR, Registers.PLL1DIVR, Registers.PLL1FRACR, 0x00000000, 0x01010280, "PLL1", true);
            DefinePll(1, Registers.PLL2CFGR, Registers.PLL2DIVR, Registers.PLL2FRACR, 0x00000000, 0x01010280, "PLL2", false);
            DefinePll(2, Registers.PLL3CFGR, Registers.PLL3DIVR, Registers.PLL3FRACR, 0x00000000, 0x01010280, "PLL3", false);

            // RM0456 section 11.8.18.
            Registers.CIER.Define(this, 0x00000000, name: "RCC_CIER")
                .WithFlag(0, out readyInterruptEnable[0], name: "LSIRDYIE")
                .WithFlag(1, out readyInterruptEnable[1], name: "LSERDYIE")
                .WithFlag(2, out readyInterruptEnable[2], name: "MSISRDYIE")
                .WithFlag(3, out readyInterruptEnable[3], name: "HSIRDYIE")
                .WithFlag(4, out readyInterruptEnable[4], name: "HSERDYIE")
                .WithFlag(5, out readyInterruptEnable[5], name: "HSI48RDYIE")
                .WithFlag(6, out readyInterruptEnable[6], name: "PLL1RDYIE")
                .WithFlag(7, out readyInterruptEnable[7], name: "PLL2RDYIE")
                .WithFlag(8, out readyInterruptEnable[8], name: "PLL3RDYIE")
                .WithReservedBits(9, 2)
                .WithFlag(11, out readyInterruptEnable[11], name: "MSIKRDYIE")
                .WithFlag(12, out readyInterruptEnable[12], name: "SHSIRDYIE")
                .WithReservedBits(13, 19)
                .WithChangeCallback((_, __) => UpdateInterrupt())
            ;

            // RM0456 section 11.8.19. Clock security system failures are not modelled.
            Registers.CIFR.Define(this, 0x00000000, name: "RCC_CIFR")
                .WithFlag(0, out readyInterruptFlag[0], FieldMode.Read, name: "LSIRDYF")
                .WithFlag(1, out readyInterruptFlag[1], FieldMode.Read, name: "LSERDYF")
                .WithFlag(2, out readyInterruptFlag[2], FieldMode.Read, name: "MSISRDYF")
                .WithFlag(3, out readyInterruptFlag[3], FieldMode.Read, name: "HSIRDYF")
                .WithFlag(4, out readyInterruptFlag[4], FieldMode.Read, name: "HSERDYF")
                .WithFlag(5, out readyInterruptFlag[5], FieldMode.Read, name: "HSI48RDYF")
                .WithFlag(6, out readyInterruptFlag[6], FieldMode.Read, name: "PLL1RDYF")
                .WithFlag(7, out readyInterruptFlag[7], FieldMode.Read, name: "PLL2RDYF")
                .WithFlag(8, out readyInterruptFlag[8], FieldMode.Read, name: "PLL3RDYF")
                .WithReservedBits(9, 1)
                .WithFlag(10, FieldMode.Read, valueProviderCallback: _ => false, name: "CSSF")
                .WithFlag(11, out readyInterruptFlag[11], FieldMode.Read, name: "MSIKRDYF")
                .WithFlag(12, out readyInterruptFlag[12], FieldMode.Read, name: "SHSIRDYF")
                .WithReservedBits(13, 19)
            ;

            // RM0456 section 11.8.20.
            Registers.CICR.Define(this, 0x00000000, name: "RCC_CICR")
                .WithFlag(0, FieldMode.WriteOneToClear, writeCallback: (_, value) => { if(value) { readyInterruptFlag[0].Value = false; } }, name: "LSIRDYC")
                .WithFlag(1, FieldMode.WriteOneToClear, writeCallback: (_, value) => { if(value) { readyInterruptFlag[1].Value = false; } }, name: "LSERDYC")
                .WithFlag(2, FieldMode.WriteOneToClear, writeCallback: (_, value) => { if(value) { readyInterruptFlag[2].Value = false; } }, name: "MSISRDYC")
                .WithFlag(3, FieldMode.WriteOneToClear, writeCallback: (_, value) => { if(value) { readyInterruptFlag[3].Value = false; } }, name: "HSIRDYC")
                .WithFlag(4, FieldMode.WriteOneToClear, writeCallback: (_, value) => { if(value) { readyInterruptFlag[4].Value = false; } }, name: "HSERDYC")
                .WithFlag(5, FieldMode.WriteOneToClear, writeCallback: (_, value) => { if(value) { readyInterruptFlag[5].Value = false; } }, name: "HSI48RDYC")
                .WithFlag(6, FieldMode.WriteOneToClear, writeCallback: (_, value) => { if(value) { readyInterruptFlag[6].Value = false; } }, name: "PLL1RDYC")
                .WithFlag(7, FieldMode.WriteOneToClear, writeCallback: (_, value) => { if(value) { readyInterruptFlag[7].Value = false; } }, name: "PLL2RDYC")
                .WithFlag(8, FieldMode.WriteOneToClear, writeCallback: (_, value) => { if(value) { readyInterruptFlag[8].Value = false; } }, name: "PLL3RDYC")
                .WithReservedBits(9, 1)
                .WithFlag(10, FieldMode.Write, name: "CSSC")
                .WithFlag(11, FieldMode.WriteOneToClear, writeCallback: (_, value) => { if(value) { readyInterruptFlag[11].Value = false; } }, name: "MSIKRDYC")
                .WithFlag(12, FieldMode.WriteOneToClear, writeCallback: (_, value) => { if(value) { readyInterruptFlag[12].Value = false; } }, name: "SHSIRDYC")
                .WithReservedBits(13, 19)
                .WithWriteCallback((_, __) => UpdateInterrupt())
            ;

            // RM p.540, reset 0x00000000
            Registers.AHB1RSTR.Define(this, 0x00000000, name: "RCC_AHB1RSTR")
                .WithFlag(0, name: "GPDMA1RST")
                .WithFlag(1, name: "CORDICRST")
                .WithFlag(2, name: "FMACRST")
                .WithFlag(3, name: "MDF1RST")
                .WithReservedBits(4, 8)
                .WithFlag(12, name: "CRCRST")
                .WithReservedBits(13, 3)
                .WithFlag(16, name: "TSCRST")
                .WithFlag(17, name: "RAMCFGRST")
                .WithFlag(18, name: "DMA2DRST")
                .WithReservedBits(19, 13)
            ;

            // RM p.542, reset 0x00000000
            Registers.AHB2RSTR1.Define(this, 0x00000000, name: "RCC_AHB2RSTR1")
                .WithFlag(0, name: "GPIOARST")
                .WithFlag(1, name: "GPIOBRST")
                .WithFlag(2, name: "GPIOCRST")
                .WithFlag(3, name: "GPIODRST")
                .WithFlag(4, name: "GPIOERST")
                .WithFlag(5, name: "GPIOFRST")
                .WithFlag(6, name: "GPIOGRST")
                .WithFlag(7, name: "GPIOHRST")
                .WithFlag(8, name: "GPIOIRST")
                .WithReservedBits(9, 1)
                .WithFlag(10, name: "ADC12RST")
                .WithReservedBits(11, 1)
                .WithFlag(12, name: "DCMI_PSSIRST")
                .WithReservedBits(13, 1)
                .WithFlag(14, name: "OTGRST")
                .WithReservedBits(15, 2)
                .WithFlag(17, name: "HASHRST")
                .WithFlag(18, name: "RNGRST")
                .WithReservedBits(19, 2)
                .WithFlag(21, name: "OCTOSPIMRST")
                .WithReservedBits(22, 5)
                .WithFlag(27, name: "SDMMC1RST")
                .WithFlag(28, name: "SDMMC2RST")
                .WithReservedBits(29, 3)
            ;

            // RM p.545, reset 0x00000000
            Registers.AHB2RSTR2.Define(this, 0x00000000, name: "RCC_AHB2RSTR2")
                .WithFlag(0, name: "FSMCRST")
                .WithReservedBits(1, 3)
                .WithFlag(4, name: "OCTOSPI1RST")
                .WithReservedBits(5, 3)
                .WithFlag(8, name: "OCTOSPI2RST")
                .WithReservedBits(9, 23)
            ;

            // RM p.546, reset 0x00000000
            Registers.AHB3RSTR.Define(this, 0x00000000, name: "RCC_AHB3RSTR")
                .WithFlag(0, name: "LPGPIO1RST")
                .WithReservedBits(1, 4)
                .WithFlag(5, name: "ADC4RST")
                .WithFlag(6, name: "DAC1RST")
                .WithReservedBits(7, 2)
                .WithFlag(9, name: "LPDMA1RST")
                .WithFlag(10, name: "ADF1RST")
                .WithReservedBits(11, 21)
            ;

            // RM p.547, reset 0x00000000
            Registers.APB1RSTR1.Define(this, 0x00000000, name: "RCC_APB1RSTR1")
                .WithFlag(0, name: "TIM2RST")
                .WithFlag(1, name: "TIM3RST")
                .WithFlag(2, name: "TIM4RST")
                .WithFlag(3, name: "TIM5RST")
                .WithFlag(4, name: "TIM6RST")
                .WithFlag(5, name: "TIM7RST")
                .WithReservedBits(6, 8)
                .WithFlag(14, name: "SPI2RST")
                .WithReservedBits(15, 2)
                .WithFlag(17, name: "USART2RST")
                .WithFlag(18, name: "USART3RST")
                .WithFlag(19, name: "UART4RST")
                .WithFlag(20, name: "UART5RST")
                .WithFlag(21, name: "I2C1RST")
                .WithFlag(22, name: "I2C2RST")
                .WithReservedBits(23, 1)
                .WithFlag(24, name: "CRSRST")
                .WithReservedBits(25, 7)
            ;

            // RM p.549, reset 0x00000000
            Registers.APB1RSTR2.Define(this, 0x00000000, name: "RCC_APB1RSTR2")
                .WithReservedBits(0, 1)
                .WithFlag(1, name: "I2C4RST")
                .WithReservedBits(2, 3)
                .WithFlag(5, name: "LPTIM2RST")
                .WithReservedBits(6, 3)
                .WithFlag(9, name: "FDCAN1RST")
                .WithReservedBits(10, 13)
                .WithFlag(23, name: "UCPD1RST")
                .WithReservedBits(24, 8)
            ;

            // RM p.550, reset 0x00000000
            Registers.APB2RSTR.Define(this, 0x00000000, name: "RCC_APB2RSTR")
                .WithReservedBits(0, 11)
                .WithFlag(11, name: "TIM1RST")
                .WithFlag(12, name: "SPI1RST")
                .WithFlag(13, name: "TIM8RST")
                .WithFlag(14, name: "USART1RST")
                .WithReservedBits(15, 1)
                .WithFlag(16, name: "TIM15RST")
                .WithFlag(17, name: "TIM16RST")
                .WithFlag(18, name: "TIM17RST")
                .WithReservedBits(19, 2)
                .WithFlag(21, name: "SAI1RST")
                .WithFlag(22, name: "SAI2RST")
                .WithReservedBits(23, 9)
            ;

            // RM p.552, reset 0x00000000
            Registers.APB3RSTR.Define(this, 0x00000000, name: "RCC_APB3RSTR")
                .WithReservedBits(0, 1)
                .WithFlag(1, name: "SYSCFGRST")
                .WithReservedBits(2, 3)
                .WithFlag(5, name: "SPI3RST")
                .WithFlag(6, name: "LPUART1RST")
                .WithFlag(7, name: "I2C3RST")
                .WithReservedBits(8, 3)
                .WithFlag(11, name: "LPTIM1RST")
                .WithFlag(12, name: "LPTIM3RST")
                .WithFlag(13, name: "LPTIM4RST")
                .WithFlag(14, name: "OPAMPRST")
                .WithFlag(15, name: "COMPRST")
                .WithReservedBits(16, 4)
                .WithFlag(20, name: "VREFRST")
                .WithReservedBits(21, 11)
            ;

            // RM p.553, reset 0xD0000100
            Registers.AHB1ENR.Define(this, 0xD0000100, name: "RCC_AHB1ENR")
                .WithFlag(0, name: "GPDMA1EN")
                .WithFlag(1, name: "CORDICEN")
                .WithFlag(2, name: "FMACEN")
                .WithFlag(3, name: "MDF1EN")
                .WithReservedBits(4, 4)
                .WithFlag(8, name: "FLASHEN")
                .WithReservedBits(9, 3)
                .WithFlag(12, name: "CRCEN")
                .WithReservedBits(13, 3)
                .WithFlag(16, name: "TSCEN")
                .WithFlag(17, name: "RAMCFGEN")
                .WithFlag(18, name: "DMA2DEN")
                .WithReservedBits(19, 5)
                .WithFlag(24, name: "GTZC1EN")
                .WithReservedBits(25, 3)
                .WithFlag(28, name: "BKPSRAMEN")
                .WithReservedBits(29, 1)
                .WithFlag(30, name: "DCACHE1EN")
                .WithFlag(31, name: "SRAM1EN")
            ;

            // RM p.556, reset 0xC0000000
            Registers.AHB2ENR1.Define(this, 0xC0000000, name: "RCC_AHB2ENR1")
                .WithFlag(0, name: "GPIOAEN")
                .WithFlag(1, name: "GPIOBEN")
                .WithFlag(2, name: "GPIOCEN")
                .WithFlag(3, name: "GPIODEN")
                .WithFlag(4, name: "GPIOEEN")
                .WithFlag(5, name: "GPIOFEN")
                .WithFlag(6, name: "GPIOGEN")
                .WithFlag(7, name: "GPIOHEN")
                .WithFlag(8, name: "GPIOIEN")
                .WithReservedBits(9, 1)
                .WithFlag(10, name: "ADC12EN")
                .WithReservedBits(11, 1)
                .WithFlag(12, name: "DCMI_PSSIEN")
                .WithReservedBits(13, 1)
                .WithFlag(14, name: "OTGEN")
                .WithReservedBits(15, 2)
                .WithFlag(17, name: "HASHEN")
                .WithFlag(18, name: "RNGEN")
                .WithReservedBits(19, 2)
                .WithFlag(21, name: "OCTOSPIMEN")
                .WithReservedBits(22, 5)
                .WithFlag(27, name: "SDMMC1EN")
                .WithFlag(28, name: "SDMMC2EN")
                .WithReservedBits(29, 1)
                .WithFlag(30, name: "SRAM2EN")
                .WithFlag(31, name: "SRAM3EN")
            ;

            // RM p.560, reset 0x00000000
            Registers.AHB2ENR2.Define(this, 0x00000000, name: "RCC_AHB2ENR2")
                .WithFlag(0, name: "FSMCEN")
                .WithReservedBits(1, 3)
                .WithFlag(4, name: "OCTOSPI1EN")
                .WithReservedBits(5, 3)
                .WithFlag(8, name: "OCTOSPI2EN")
                .WithReservedBits(9, 23)
            ;

            // RM p.561, reset 0x80000000
            Registers.AHB3ENR.Define(this, 0x80000000, name: "RCC_AHB3ENR")
                .WithFlag(0, name: "LPGPIO1EN")
                .WithReservedBits(1, 1)
                .WithFlag(2, name: "PWREN")
                .WithReservedBits(3, 2)
                .WithFlag(5, name: "ADC4EN")
                .WithFlag(6, name: "DAC1EN")
                .WithReservedBits(7, 2)
                .WithFlag(9, name: "LPDMA1EN")
                .WithFlag(10, name: "ADF1EN")
                .WithReservedBits(11, 1)
                .WithFlag(12, name: "GTZC2EN")
                .WithReservedBits(13, 18)
                .WithFlag(31, name: "SRAM4EN")
            ;

            // RM p.562, reset 0x00000000
            Registers.APB1ENR1.Define(this, 0x00000000, name: "RCC_APB1ENR1")
                .WithFlag(0, name: "TIM2EN")
                .WithFlag(1, name: "TIM3EN")
                .WithFlag(2, name: "TIM4EN")
                .WithFlag(3, name: "TIM5EN")
                .WithFlag(4, name: "TIM6EN")
                .WithFlag(5, name: "TIM7EN")
                .WithReservedBits(6, 5)
                .WithFlag(11, name: "WWDGEN")
                .WithReservedBits(12, 2)
                .WithFlag(14, name: "SPI2EN")
                .WithReservedBits(15, 2)
                .WithFlag(17, name: "USART2EN")
                .WithFlag(18, name: "USART3EN")
                .WithFlag(19, name: "UART4EN")
                .WithFlag(20, name: "UART5EN")
                .WithFlag(21, name: "I2C1EN")
                .WithFlag(22, name: "I2C2EN")
                .WithReservedBits(23, 1)
                .WithFlag(24, name: "CRSEN")
                .WithReservedBits(25, 7)
            ;

            // RM p.564, reset 0x00000000
            Registers.APB1ENR2.Define(this, 0x00000000, name: "RCC_APB1ENR2")
                .WithReservedBits(0, 1)
                .WithFlag(1, name: "I2C4EN")
                .WithReservedBits(2, 3)
                .WithFlag(5, name: "LPTIM2EN")
                .WithReservedBits(6, 3)
                .WithFlag(9, name: "FDCAN1EN")
                .WithReservedBits(10, 13)
                .WithFlag(23, name: "UCPD1EN")
                .WithReservedBits(24, 8)
            ;

            // RM p.566, reset 0x00000000
            Registers.APB2ENR.Define(this, 0x00000000, name: "RCC_APB2ENR")
                .WithReservedBits(0, 11)
                .WithFlag(11, name: "TIM1EN")
                .WithFlag(12, name: "SPI1EN")
                .WithFlag(13, name: "TIM8EN")
                .WithFlag(14, name: "USART1EN")
                .WithReservedBits(15, 1)
                .WithFlag(16, name: "TIM15EN")
                .WithFlag(17, name: "TIM16EN")
                .WithFlag(18, name: "TIM17EN")
                .WithReservedBits(19, 2)
                .WithFlag(21, name: "SAI1EN")
                .WithFlag(22, name: "SAI2EN")
                .WithReservedBits(23, 9)
            ;

            // RM p.568, reset 0x00000000
            Registers.APB3ENR.Define(this, 0x00000000, name: "RCC_APB3ENR")
                .WithReservedBits(0, 1)
                .WithFlag(1, name: "SYSCFGEN")
                .WithReservedBits(2, 3)
                .WithFlag(5, name: "SPI3EN")
                .WithFlag(6, name: "LPUART1EN")
                .WithFlag(7, name: "I2C3EN")
                .WithReservedBits(8, 3)
                .WithFlag(11, name: "LPTIM1EN")
                .WithFlag(12, name: "LPTIM3EN")
                .WithFlag(13, name: "LPTIM4EN")
                .WithFlag(14, name: "OPAMPEN")
                .WithFlag(15, name: "COMPEN")
                .WithReservedBits(16, 4)
                .WithFlag(20, name: "VREFEN")
                .WithFlag(21, name: "RTCAPBEN")
                .WithReservedBits(22, 10)
            ;

            // RM p.569, reset 0xFFFFFFFF
            Registers.AHB1SMENR.Define(this, 0xFFFFFFFF, name: "RCC_AHB1SMENR")
                .WithFlag(0, name: "GPDMA1SMEN")
                .WithFlag(1, name: "CORDICSMEN")
                .WithFlag(2, name: "FMACSMEN")
                .WithFlag(3, name: "MDF1SMEN")
                .WithReservedBits(4, 4)
                .WithFlag(8, name: "FLASHSMEN")
                .WithReservedBits(9, 3)
                .WithFlag(12, name: "CRCSMEN")
                .WithReservedBits(13, 3)
                .WithFlag(16, name: "TSCSMEN")
                .WithFlag(17, name: "RAMCFGSMEN")
                .WithFlag(18, name: "DMA2DSMEN")
                .WithReservedBits(19, 5)
                .WithFlag(24, name: "GTZC1SMEN")
                .WithReservedBits(25, 3)
                .WithFlag(28, name: "BKPSRAMSMEN")
                .WithFlag(29, name: "ICACHESMEN")
                .WithFlag(30, name: "DCACHE1SMEN")
                .WithFlag(31, name: "SRAM1SMEN")
            ;

            // RM p.572, reset 0xFFFFFFFF
            Registers.AHB2SMENR1.Define(this, 0xFFFFFFFF, name: "RCC_AHB2SMENR1")
                .WithFlag(0, name: "GPIOASMEN")
                .WithFlag(1, name: "GPIOBSMEN")
                .WithFlag(2, name: "GPIOCSMEN")
                .WithFlag(3, name: "GPIODSMEN")
                .WithFlag(4, name: "GPIOESMEN")
                .WithFlag(5, name: "GPIOFSMEN")
                .WithFlag(6, name: "GPIOGSMEN")
                .WithFlag(7, name: "GPIOHSMEN")
                .WithFlag(8, name: "GPIOISMEN")
                .WithReservedBits(9, 1)
                .WithFlag(10, name: "ADC12SMEN")
                .WithReservedBits(11, 1)
                .WithFlag(12, name: "DCMI_PSSISMEN")
                .WithReservedBits(13, 1)
                .WithFlag(14, name: "OTGSMEN")
                .WithReservedBits(15, 2)
                .WithFlag(17, name: "HASHSMEN")
                .WithFlag(18, name: "RNGSMEN")
                .WithReservedBits(19, 2)
                .WithFlag(21, name: "OCTOSPIMSMEN")
                .WithReservedBits(22, 5)
                .WithFlag(27, name: "SDMMC1SMEN")
                .WithFlag(28, name: "SDMMC2SMEN")
                .WithReservedBits(29, 1)
                .WithFlag(30, name: "SRAM2SMEN")
                .WithFlag(31, name: "SRAM3SMEN")
            ;

            // RM p.576, reset 0xFFFFFFFF
            Registers.AHB2SMENR2.Define(this, 0xFFFFFFFF, name: "RCC_AHB2SMENR2")
                .WithFlag(0, name: "FSMCSMEN")
                .WithReservedBits(1, 3)
                .WithFlag(4, name: "OCTOSPI1SMEN")
                .WithReservedBits(5, 3)
                .WithFlag(8, name: "OCTOSPI2SMEN")
                .WithReservedBits(9, 23)
            ;

            // RM p.577, reset 0xFFFFFFFF
            Registers.AHB3SMENR.Define(this, 0xFFFFFFFF, name: "RCC_AHB3SMENR")
                .WithFlag(0, name: "LPGPIO1SMEN")
                .WithReservedBits(1, 1)
                .WithFlag(2, name: "PWRSMEN")
                .WithReservedBits(3, 2)
                .WithFlag(5, name: "ADC4SMEN")
                .WithFlag(6, name: "DAC1SMEN")
                .WithReservedBits(7, 2)
                .WithFlag(9, name: "LPDMA1SMEN")
                .WithFlag(10, name: "ADF1SMEN")
                .WithReservedBits(11, 1)
                .WithFlag(12, name: "GTZC2SMEN")
                .WithReservedBits(13, 18)
                .WithFlag(31, name: "SRAM4SMEN")
            ;

            // RM p.579, reset 0xFFFFFFFF
            Registers.APB1SMENR1.Define(this, 0xFFFFFFFF, name: "RCC_APB1SMENR1")
                .WithFlag(0, name: "TIM2SMEN")
                .WithFlag(1, name: "TIM3SMEN")
                .WithFlag(2, name: "TIM4SMEN")
                .WithFlag(3, name: "TIM5SMEN")
                .WithFlag(4, name: "TIM6SMEN")
                .WithFlag(5, name: "TIM7SMEN")
                .WithReservedBits(6, 5)
                .WithFlag(11, name: "WWDGSMEN")
                .WithReservedBits(12, 2)
                .WithFlag(14, name: "SPI2SMEN")
                .WithReservedBits(15, 2)
                .WithFlag(17, name: "USART2SMEN")
                .WithFlag(18, name: "USART3SMEN")
                .WithFlag(19, name: "UART4SMEN")
                .WithFlag(20, name: "UART5SMEN")
                .WithFlag(21, name: "I2C1SMEN")
                .WithFlag(22, name: "I2C2SMEN")
                .WithReservedBits(23, 1)
                .WithFlag(24, name: "CRSSMEN")
                .WithReservedBits(25, 7)
            ;

            // RM p.581, reset 0xFFFFFFFF
            Registers.APB1SMENR2.Define(this, 0xFFFFFFFF, name: "RCC_APB1SMENR2")
                .WithReservedBits(0, 1)
                .WithFlag(1, name: "I2C4SMEN")
                .WithReservedBits(2, 3)
                .WithFlag(5, name: "LPTIM2SMEN")
                .WithReservedBits(6, 3)
                .WithFlag(9, name: "FDCAN1SMEN")
                .WithReservedBits(10, 13)
                .WithFlag(23, name: "UCPD1SMEN")
                .WithReservedBits(24, 8)
            ;

            // RM p.583, reset 0xFFFFFFFF
            Registers.APB2SMENR.Define(this, 0xFFFFFFFF, name: "RCC_APB2SMENR")
                .WithReservedBits(0, 11)
                .WithFlag(11, name: "TIM1SMEN")
                .WithFlag(12, name: "SPI1SMEN")
                .WithFlag(13, name: "TIM8SMEN")
                .WithFlag(14, name: "USART1SMEN")
                .WithReservedBits(15, 1)
                .WithFlag(16, name: "TIM15SMEN")
                .WithFlag(17, name: "TIM16SMEN")
                .WithFlag(18, name: "TIM17SMEN")
                .WithReservedBits(19, 2)
                .WithFlag(21, name: "SAI1SMEN")
                .WithFlag(22, name: "SAI2SMEN")
                .WithReservedBits(23, 9)
            ;

            // RM p.585, reset 0xFFFFFFFF
            Registers.APB3SMENR.Define(this, 0xFFFFFFFF, name: "RCC_APB3SMENR")
                .WithReservedBits(0, 1)
                .WithFlag(1, name: "SYSCFGSMEN")
                .WithReservedBits(2, 3)
                .WithFlag(5, name: "SPI3SMEN")
                .WithFlag(6, name: "LPUART1SMEN")
                .WithFlag(7, name: "I2C3SMEN")
                .WithReservedBits(8, 3)
                .WithFlag(11, name: "LPTIM1SMEN")
                .WithFlag(12, name: "LPTIM3SMEN")
                .WithFlag(13, name: "LPTIM4SMEN")
                .WithFlag(14, name: "OPAMPSMEN")
                .WithFlag(15, name: "COMPSMEN")
                .WithReservedBits(16, 4)
                .WithFlag(20, name: "VREFSMEN")
                .WithFlag(21, name: "RTCAPBSMEN")
                .WithReservedBits(22, 10)
            ;

            // RM p.587, reset 0x00000000
            Registers.SRDAMR.Define(this, 0x00000000, name: "RCC_SRDAMR")
                .WithReservedBits(0, 5)
                .WithFlag(5, name: "SPI3AMEN")
                .WithFlag(6, name: "LPUART1AMEN")
                .WithFlag(7, name: "I2C3AMEN")
                .WithReservedBits(8, 3)
                .WithFlag(11, name: "LPTIM1AMEN")
                .WithFlag(12, name: "LPTIM3AMEN")
                .WithFlag(13, name: "LPTIM4AMEN")
                .WithFlag(14, name: "OPAMPAMEN")
                .WithFlag(15, name: "COMPAMEN")
                .WithReservedBits(16, 4)
                .WithFlag(20, name: "VREFAMEN")
                .WithFlag(21, name: "RTCAPBAMEN")
                .WithReservedBits(22, 3)
                .WithFlag(25, name: "ADC4AMEN")
                .WithFlag(26, name: "LPGPIO1AMEN")
                .WithFlag(27, name: "DAC1AMEN")
                .WithFlag(28, name: "LPDMA1AMEN")
                .WithFlag(29, name: "ADF1AMEN")
                .WithReservedBits(30, 1)
                .WithFlag(31, name: "SRAM4AMEN")
            ;

            // RM p.589, reset 0x00000000
            Registers.CCIPR1.Define(this, 0x00000000, name: "RCC_CCIPR1")
                .WithValueField(0, 2, out serialClockSelection[0], name: "USART1SEL")
                .WithValueField(2, 2, out serialClockSelection[1], name: "USART2SEL")
                .WithValueField(4, 2, out serialClockSelection[2], name: "USART3SEL")
                .WithValueField(6, 2, out serialClockSelection[3], name: "UART4SEL")
                .WithValueField(8, 2, out serialClockSelection[4], name: "UART5SEL")
                .WithValueField(10, 2, name: "I2C1SEL")
                .WithValueField(12, 2, name: "I2C2SEL")
                .WithValueField(14, 2, name: "I2C4SEL")
                .WithValueField(16, 2, name: "SPI2SEL")
                .WithValueField(18, 2, name: "LPTIM2SEL")
                .WithValueField(20, 2, name: "SPI1SEL")
                .WithValueField(22, 2, name: "SYSTICKSEL")
                .WithValueField(24, 2, name: "FDCANSEL")
                .WithValueField(26, 2, name: "ICLKSEL")
                .WithReservedBits(28, 1)
                .WithValueField(29, 3, name: "TIMICSEL")
                .WithChangeCallback((_, __) => UpdateClocks())
            ;

            // RM p.592, reset 0x00000000
            Registers.CCIPR2.Define(this, 0x00000000, name: "RCC_CCIPR2")
                .WithValueField(0, 3, name: "MDF1SEL")
                .WithReservedBits(3, 2)
                .WithValueField(5, 3, name: "SAI1SEL")
                .WithValueField(8, 3, name: "SAI2SEL")
                .WithReservedBits(11, 1)
                .WithValueField(12, 2, name: "RNGSEL")
                .WithFlag(14, name: "SDMMCSEL")
                .WithReservedBits(15, 5)
                .WithValueField(20, 2, name: "OCTOSPISEL")
                .WithReservedBits(22, 10)
            ;

            // RM p.595, reset 0x00000000
            Registers.CCIPR3.Define(this, 0x00000000, name: "RCC_CCIPR3")
                .WithValueField(0, 3, out lpuartClockSelection, name: "LPUART1SEL")
                .WithValueField(3, 2, name: "SPI3SEL")
                .WithReservedBits(5, 1)
                .WithValueField(6, 2, name: "I2C3SEL")
                .WithValueField(8, 2, name: "LPTIM34SEL")
                .WithValueField(10, 2, name: "LPTIM1SEL")
                .WithValueField(12, 3, name: "ADCDACSEL")
                .WithFlag(15, name: "DAC1SEL")
                .WithValueField(16, 3, name: "ADF1SEL")
                .WithReservedBits(19, 13)
                .WithChangeCallback((_, __) => UpdateClocks())
            ;

            // RM0456 section 11.8.49, backup domain reset value 0x0000 0000 (STM32U575/585).
            // Write-protected by PWR_DBPR.DBP, see WriteDoubleWord.
            Registers.BDCR.Define(this, 0x00000000, name: "RCC_BDCR")
                .WithFlag(0, out lseOn, name: "LSEON")
                .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => LseReady, name: "LSERDY")
                .WithFlag(2, name: "LSEBYP")
                .WithValueField(3, 2, name: "LSEDRV")
                .WithFlag(5, name: "LSECSSON")
                .WithFlag(6, FieldMode.Read, valueProviderCallback: _ => false, name: "LSECSSD")
                .WithFlag(7, out lseSystemEnable, name: "LSESYSEN")
                .WithValueField(8, 2, out rtcSelection, name: "RTCSEL")
                .WithReservedBits(10, 1)
                .WithFlag(11, FieldMode.Read, valueProviderCallback: _ => lseSystemEnable.Value && LseReady, name: "LSESYSRDY")
                .WithFlag(12, name: "LSEGFON")
                .WithReservedBits(13, 2)
                .WithFlag(15, out rtcEnable, name: "RTCEN")
                .WithFlag(16, writeCallback: (_, value) => { if(value) { ResetBackupDomain(); } }, name: "BDRST")
                .WithReservedBits(17, 7)
                .WithFlag(24, name: "LSCOEN")
                .WithFlag(25, name: "LSCOSEL")
                .WithFlag(26, out lsiOn, name: "LSION")
                .WithFlag(27, FieldMode.Read, valueProviderCallback: _ => LsiReady, name: "LSIRDY")
                .WithFlag(28, name: "LSIPREDIV")
                .WithReservedBits(29, 3)
                .WithChangeCallback((_, __) => UpdateClocks())
            ;

            // RM0456 section 11.8.50, reset 0x0C00 4400: PINRSTF and BORRSTF set after power-on,
            // MSISSRANGE = MSIKSRANGE = 4 MHz. RMVF clears the reset flags.
            Registers.CSR.Define(this, 0x0C004400, name: "RCC_CSR")
                .WithReservedBits(0, 8)
                .WithValueField(8, 4, out msikStandbyRange, name: "MSIKSRANGE")
                .WithValueField(12, 4, out msisStandbyRange, name: "MSISSRANGE")
                .WithReservedBits(16, 7)
                .WithFlag(23, FieldMode.WriteOneToClear, writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            foreach(var flag in resetFlags)
                            {
                                flag.Value = false;
                            }
                        }
                    }, name: "RMVF")
                .WithReservedBits(24, 1)
                .WithFlags(25, 7, out resetFlags, FieldMode.Read, name: "RSTF")
                .WithChangeCallback((_, __) => UpdateClocks())
            ;

            // RM p.602, reset 0x00000000
            Registers.SECCFGR.Define(this, 0x00000000, name: "RCC_SECCFGR")
                .WithFlag(0, name: "HSISEC")
                .WithFlag(1, name: "HSESEC")
                .WithFlag(2, name: "MSISEC")
                .WithFlag(3, name: "LSISEC")
                .WithFlag(4, name: "LSESEC")
                .WithFlag(5, name: "SYSCLKSEC")
                .WithFlag(6, name: "PRESCSEC")
                .WithFlag(7, name: "PLL1SEC")
                .WithFlag(8, name: "PLL2SEC")
                .WithFlag(9, name: "PLL3SEC")
                .WithFlag(10, name: "ICLKSEC")
                .WithFlag(11, name: "HSI48SEC")
                .WithFlag(12, name: "RMVFSEC")
                .WithReservedBits(13, 19)
            ;

            // RM p.603, reset 0x00000000
            Registers.PRIVCFGR.Define(this, 0x00000000, name: "RCC_PRIVCFGR")
                .WithFlag(0, name: "SPRIV")
                .WithFlag(1, name: "NSPRIV")
                .WithReservedBits(2, 30)
            ;
        }

        private void DefinePll(int index, Registers configuration, Registers dividers, Registers fraction, uint configurationReset,
            uint dividersReset, string prefix, bool hasMBoost)
        {
            var configurationRegister = configuration.Define(this, configurationReset, name: $"RCC_{prefix}CFGR")
                .WithValueField(0, 2, out pllSource[index], writeCallback: (oldValue, _) => KeepWhilePllOn(index, pllSource[index], oldValue), name: $"{prefix}SRC")
                .WithValueField(2, 2, name: $"{prefix}RGE")
                .WithFlag(4, out pllFractionalEnable[index], name: $"{prefix}FRACEN")
                .WithReservedBits(5, 3)
                .WithValueField(8, 4, out pllPrescaler[index], writeCallback: (oldValue, _) => KeepWhilePllOn(index, pllPrescaler[index], oldValue), name: $"{prefix}M");
            if(hasMBoost)
            {
                configurationRegister.WithValueField(12, 4, name: $"{prefix}MBOOST");
            }
            else
            {
                configurationRegister.WithReservedBits(12, 4);
            }
            configurationRegister
                .WithFlag(16, name: $"{prefix}PEN")
                .WithFlag(17, name: $"{prefix}QEN")
                .WithFlag(18, out pllROutputEnable[index], name: $"{prefix}REN")
                .WithReservedBits(19, 13)
                .WithChangeCallback((_, __) => UpdateClocks());

            dividers.Define(this, dividersReset, name: $"RCC_{prefix}DIVR")
                .WithValueField(0, 9, out pllMultiplier[index], writeCallback: (oldValue, _) => KeepWhilePllOn(index, pllMultiplier[index], oldValue), name: $"{prefix}N")
                .WithValueField(9, 7, out pllDividerP[index], writeCallback: (oldValue, _) => KeepWhilePllOn(index, pllDividerP[index], oldValue), name: $"{prefix}P")
                .WithValueField(16, 7, out pllDividerQ[index], writeCallback: (oldValue, _) => KeepWhilePllOn(index, pllDividerQ[index], oldValue), name: $"{prefix}Q")
                .WithReservedBits(23, 1)
                .WithValueField(24, 7, out pllDividerR[index], writeCallback: (oldValue, _) => KeepWhilePllOn(index, pllDividerR[index], oldValue), name: $"{prefix}R")
                .WithReservedBits(31, 1)
                .WithChangeCallback((_, __) => UpdateClocks());

            // FRACN can be written at any time (RM0456 section 11.8.13).
            fraction.Define(this, 0x00000000, name: $"RCC_{prefix}FRACR")
                .WithReservedBits(0, 3)
                .WithValueField(3, 13, out pllFraction[index], name: $"{prefix}FRACN")
                .WithReservedBits(16, 16)
                .WithChangeCallback((_, __) => UpdateClocks());
        }

        // PLLxSRC, PLLxM and the PLLxDIVR fields can be written only when the PLL is disabled
        // (PLLxON = 0 and PLLxRDY = 0), RM0456 sections 11.8.9-11.8.17.
        private void KeepWhilePllOn(int index, IValueRegisterField field, ulong oldValue)
        {
            if(pllOn[index].Value && field.Value != oldValue)
            {
                this.Log(LogLevel.Warning, "PLL{0} configuration write ignored while the PLL is enabled", index + 1);
                field.Value = oldValue;
            }
        }

        private void OnClockControlChanged()
        {
            // MSIS and HSI16 are forced on, HSE and PLL1 cannot be switched off, while they feed
            // the system clock directly or through PLL1 (RM0456 section 11.8.1).
            var sysclk = systemClockStatus;
            var pll1Input = (PllSource)pllSource[0].Value;
            if(sysclk == SystemClockSource.Msis || (sysclk == SystemClockSource.Pll1 && pll1Input == PllSource.Msis))
            {
                msisOn.Value = true;
            }
            if(sysclk == SystemClockSource.Hsi16 || (sysclk == SystemClockSource.Pll1 && pll1Input == PllSource.Hsi16))
            {
                hsiOn.Value = true;
            }
            if(sysclk == SystemClockSource.Hse || (sysclk == SystemClockSource.Pll1 && pll1Input == PllSource.Hse))
            {
                hseOn.Value = true;
            }
            if(sysclk == SystemClockSource.Pll1)
            {
                pllOn[0].Value = true;
            }
            TrySwitchSystemClock();
            UpdateClocks();
        }

        private void TrySwitchSystemClock()
        {
            var requested = (SystemClockSource)systemClockSwitch.Value;
            if(requested != systemClockStatus && IsSystemClockSourceReady(requested))
            {
                systemClockStatus = requested;
            }
        }

        private bool IsSystemClockSourceReady(SystemClockSource source)
        {
            switch(source)
            {
            case SystemClockSource.Msis:
                return MsisReady;
            case SystemClockSource.Hsi16:
                return HsiReady;
            case SystemClockSource.Hse:
                return HseReady;
            case SystemClockSource.Pll1:
                return PllReady(0);
            default:
                throw new ArgumentException("Unreachable: invalid system clock source");
            }
        }

        private bool PllInputReady(int index)
        {
            switch((PllSource)pllSource[index].Value)
            {
            case PllSource.Msis:
                return MsisReady;
            case PllSource.Hsi16:
                return HsiReady;
            case PllSource.Hse:
                return HseReady;
            default:
                return false;
            }
        }

        private ulong PllInputFrequency(int index)
        {
            switch((PllSource)pllSource[index].Value)
            {
            case PllSource.Msis:
                return MsisFrequency;
            case PllSource.Hsi16:
                return Hsi16Frequency;
            case PllSource.Hse:
                return hseFrequency;
            default:
                return 0;
            }
        }

        private bool PllReady(int index) => pllOn[index].Value && PllInputReady(index);

        // VCO = input / M * (N + FRACN / 2^13); pll_r_ck = VCO / R (RM0456 sections 11.8.9-11.8.13).
        private ulong PllROutput(int index)
        {
            if(!PllReady(index) || !pllROutputEnable[index].Value)
            {
                return 0;
            }
            var fractional = pllFractionalEnable[index].Value ? pllFraction[index].Value : 0;
            var multiplier = (pllMultiplier[index].Value + 1) * 8192 + fractional;
            var vco = (ulong)((decimal)PllInputFrequency(index) / (pllPrescaler[index].Value + 1) * multiplier / 8192);
            return vco / (pllDividerR[index].Value + 1);
        }

        private bool MsisReady => msisOn.Value;

        private bool MsikReady => msikOn.Value;

        private bool HsiReady => hsiOn.Value;

        private bool Hsi48Ready => hsi48On.Value;

        private bool ShsiReady => shsiOn.Value;

        private bool HseReady => hseOn.Value && hseFrequency != 0;

        private bool LsiReady => lsiOn.Value;

        private bool LseReady => lseOn.Value && lseFrequency != 0;

        private ulong SystemClock
        {
            get
            {
                switch(systemClockStatus)
                {
                case SystemClockSource.Msis:
                    return MsisFrequency;
                case SystemClockSource.Hsi16:
                    return Hsi16Frequency;
                case SystemClockSource.Hse:
                    return hseFrequency;
                case SystemClockSource.Pll1:
                    return PllROutput(0);
                default:
                    throw new ArgumentException("Unreachable: invalid system clock source");
                }
            }
        }

        // HPRE: 0xxx /1, 1000 /2, 1001 /4, 1010 /8, 1011 /16, 1100 /64, 1101 /128, 1110 /256,
        // 1111 /512 (RM0456 section 11.8.7).
        private ulong AhbClock
        {
            get
            {
                var hpre = ahbPrescaler.Value;
                if((hpre & 0x8) == 0)
                {
                    return SystemClock;
                }
                var step = (int)(hpre & 0x7);
                var logDivisor = step < 4 ? step + 1 : step + 2;
                return SystemClock >> logDivisor;
            }
        }

        // PPREx: 0xx /1, 100 /2, 101 /4, 110 /8, 111 /16 (RM0456 sections 11.8.7, 11.8.8).
        // Same decoding as PrescaleApbAhb in STM32WBA_RCC.
        private static ulong PrescaleApb(ulong input, ulong ppre)
        {
            if((ppre & 4) == 0)
            {
                return input;
            }
            return input >> (int)((ppre & 3) + 1);
        }

        // MSISRANGE/MSIKRANGE encoding (RM0456 section 11.8.2).
        private static ulong MsiRangeFrequency(ulong range)
        {
            return MsiRangeFrequencies[range & 0xF];
        }

        private void UpdateClocks()
        {
            UpdateReadyFlags();
            if(nvic != null)
            {
                nvic.Frequency = AhbClock;
            }
            UpdateSerialKernelClocks();
        }

        // USARTxSEL/UARTxSEL in RCC_CCIPR1: 00 PCLK (PCLK2 for USART1, PCLK1 otherwise), 01 SYSCLK,
        // 10 HSI16, 11 LSE. LPUART1SEL in RCC_CCIPR3: 000 PCLK3, 001 SYSCLK, 010 HSI16, 011 LSE,
        // 100 MSIK (RM0456 sections 11.8.46 and 11.8.48). An oscillator that is off provides 0 Hz.
        private void UpdateSerialKernelClocks()
        {
            if(serialPorts == null)
            {
                return;
            }
            for(var index = 0; index < serialPorts.Length; index++)
            {
                if(serialPorts[index] == null || serialClockSelection[index] == null)
                {
                    continue;
                }
                var pclk = index == 0 ? Apb2Frequency : Apb1Frequency;
                serialPorts[index].Frequency = SerialKernelClock(serialClockSelection[index].Value, pclk);
            }
            if(lpuart1 != null && lpuartClockSelection != null)
            {
                lpuart1.Frequency = lpuartClockSelection.Value == 4
                    ? (MsikReady || msikKernelOn.Value ? MsikFrequency : 0)
                    : lpuartClockSelection.Value > 4 ? 0 : SerialKernelClock(lpuartClockSelection.Value, Apb3Frequency);
            }
        }

        private ulong SerialKernelClock(ulong selection, ulong pclk)
        {
            switch(selection)
            {
            case 0:
                return pclk;
            case 1:
                return SystemClock;
            case 2:
                return HsiReady || hsiKernelOn.Value ? Hsi16Frequency : 0;
            case 3:
                return LseReady ? lseFrequency : 0;
            default:
                return 0;
            }
        }

        // xxxRDYF is set when the oscillator becomes ready and xxxRDYIE is set (RM0456
        // section 11.8.19). The RCC interrupt is the OR of the flags.
        private void UpdateReadyFlags()
        {
            var ready = ReadyMask();
            var rising = ready & ~readyState;
            readyState = ready;
            for(var bit = 0; bit < ReadyInterruptBits.Length; bit++)
            {
                var flagBit = ReadyInterruptBits[bit];
                if((rising & (1u << flagBit)) != 0 && readyInterruptEnable[flagBit].Value)
                {
                    readyInterruptFlag[flagBit].Value = true;
                }
            }
            UpdateInterrupt();
        }

        private uint ReadyMask()
        {
            var mask = 0u;
            mask |= LsiReady ? 1u << 0 : 0;
            mask |= LseReady ? 1u << 1 : 0;
            mask |= MsisReady ? 1u << 2 : 0;
            mask |= HsiReady ? 1u << 3 : 0;
            mask |= HseReady ? 1u << 4 : 0;
            mask |= Hsi48Ready ? 1u << 5 : 0;
            mask |= PllReady(0) ? 1u << 6 : 0;
            mask |= PllReady(1) ? 1u << 7 : 0;
            mask |= PllReady(2) ? 1u << 8 : 0;
            mask |= MsikReady ? 1u << 11 : 0;
            mask |= ShsiReady ? 1u << 12 : 0;
            return mask;
        }

        private void UpdateInterrupt()
        {
            var pending = false;
            foreach(var bit in ReadyInterruptBits)
            {
                pending |= readyInterruptFlag[bit].Value;
            }
            IRQ.Set(pending);
        }

        private void ResetBackupDomain()
        {
            lseOn.Value = false;
            lseSystemEnable.Value = false;
            lsiOn.Value = false;
            rtcSelection.Value = 0;
            rtcEnable.Value = false;
        }

        private SystemClockSource systemClockStatus;
        private uint readyState;

        private IFlagRegisterField msisOn;
        private IFlagRegisterField msikOn;
        private IFlagRegisterField hsiOn;
        private IFlagRegisterField hsi48On;
        private IFlagRegisterField shsiOn;
        private IFlagRegisterField hseOn;
        private readonly IFlagRegisterField[] pllOn;
        private IFlagRegisterField msiRangeSelect;
        private IValueRegisterField msisRange;
        private IValueRegisterField msisStandbyRange;
        private IValueRegisterField systemClockSwitch;
        private IValueRegisterField ahbPrescaler;
        private IValueRegisterField apb1Prescaler;
        private IValueRegisterField apb2Prescaler;
        private IValueRegisterField apb3Prescaler;
        private readonly IValueRegisterField[] pllSource;
        private readonly IValueRegisterField[] pllPrescaler;
        private readonly IFlagRegisterField[] pllFractionalEnable;
        private readonly IFlagRegisterField[] pllROutputEnable;
        private readonly IValueRegisterField[] pllMultiplier;
        private readonly IValueRegisterField[] pllDividerP;
        private readonly IValueRegisterField[] pllDividerQ;
        private readonly IValueRegisterField[] pllDividerR;
        private readonly IValueRegisterField[] pllFraction;
        private readonly IFlagRegisterField[] readyInterruptEnable = new IFlagRegisterField[32];
        private readonly IFlagRegisterField[] readyInterruptFlag = new IFlagRegisterField[32];
        private IFlagRegisterField lseOn;
        private IFlagRegisterField lseSystemEnable;
        private IFlagRegisterField lsiOn;
        private IValueRegisterField rtcSelection;
        private IFlagRegisterField rtcEnable;
        private IFlagRegisterField[] resetFlags;

        private readonly IHasFrequency nvic;
        private readonly STM32U5_PWR pwr;
        private readonly ulong hseFrequency;
        private readonly ulong lseFrequency;
        private readonly ulong lsiFrequency;
        private readonly IHasFrequency[] serialPorts;
        private readonly IHasFrequency lpuart1;
        private readonly IValueRegisterField[] serialClockSelection = new IValueRegisterField[5];
        private IValueRegisterField lpuartClockSelection;
        private IFlagRegisterField hsiKernelOn;
        private IFlagRegisterField msikKernelOn;
        private IValueRegisterField msikRange;
        private IValueRegisterField msikStandbyRange;

        private const int PllCount = 3;
        private const ulong Hsi16Frequency = 16000000;
        private const ulong DefaultLsiFrequency = 32000;

        // CIER/CIFR/CICR bit positions of the ready interrupts (stm32u575xx.h).
        private static readonly int[] ReadyInterruptBits = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 11, 12 };

        private static readonly ulong[] MsiRangeFrequencies =
        {
            48000000, 24000000, 16000000, 12000000, 4000000, 2000000, 1330000, 1000000,
            3072000, 1536000, 1024000, 768000, 400000, 200000, 133000, 100000,
        };

        private enum SystemClockSource
        {
            Msis = 0,
            Hsi16 = 1,
            Hse = 2,
            Pll1 = 3,
        }

        private enum PllSource
        {
            None = 0,
            Msis = 1,
            Hsi16 = 2,
            Hse = 3,
        }

        private enum Registers
        {
            CR = 0x000, // RCC_CR
            ICSCR1 = 0x008, // RCC_ICSCR1
            ICSCR2 = 0x00C, // RCC_ICSCR2
            ICSCR3 = 0x010, // RCC_ICSCR3
            CRRCR = 0x014, // RCC_CRRCR
            CFGR1 = 0x01C, // RCC_CFGR1
            CFGR2 = 0x020, // RCC_CFGR2
            CFGR3 = 0x024, // RCC_CFGR3
            PLL1CFGR = 0x028, // RCC_PLL1CFGR
            PLL2CFGR = 0x02C, // RCC_PLL2CFGR
            PLL3CFGR = 0x030, // RCC_PLL3CFGR
            PLL1DIVR = 0x034, // RCC_PLL1DIVR
            PLL1FRACR = 0x038, // RCC_PLL1FRACR
            PLL2DIVR = 0x03C, // RCC_PLL2DIVR
            PLL2FRACR = 0x040, // RCC_PLL2FRACR
            PLL3DIVR = 0x044, // RCC_PLL3DIVR
            PLL3FRACR = 0x048, // RCC_PLL3FRACR
            CIER = 0x050, // RCC_CIER
            CIFR = 0x054, // RCC_CIFR
            CICR = 0x058, // RCC_CICR
            AHB1RSTR = 0x060, // RCC_AHB1RSTR
            AHB2RSTR1 = 0x064, // RCC_AHB2RSTR1
            AHB2RSTR2 = 0x068, // RCC_AHB2RSTR2
            AHB3RSTR = 0x06C, // RCC_AHB3RSTR
            APB1RSTR1 = 0x074, // RCC_APB1RSTR1
            APB1RSTR2 = 0x078, // RCC_APB1RSTR2
            APB2RSTR = 0x07C, // RCC_APB2RSTR
            APB3RSTR = 0x080, // RCC_APB3RSTR
            AHB1ENR = 0x088, // RCC_AHB1ENR
            AHB2ENR1 = 0x08C, // RCC_AHB2ENR1
            AHB2ENR2 = 0x090, // RCC_AHB2ENR2
            AHB3ENR = 0x094, // RCC_AHB3ENR
            APB1ENR1 = 0x09C, // RCC_APB1ENR1
            APB1ENR2 = 0x0A0, // RCC_APB1ENR2
            APB2ENR = 0x0A4, // RCC_APB2ENR
            APB3ENR = 0x0A8, // RCC_APB3ENR
            AHB1SMENR = 0x0B0, // RCC_AHB1SMENR
            AHB2SMENR1 = 0x0B4, // RCC_AHB2SMENR1
            AHB2SMENR2 = 0x0B8, // RCC_AHB2SMENR2
            AHB3SMENR = 0x0BC, // RCC_AHB3SMENR
            APB1SMENR1 = 0x0C4, // RCC_APB1SMENR1
            APB1SMENR2 = 0x0C8, // RCC_APB1SMENR2
            APB2SMENR = 0x0CC, // RCC_APB2SMENR
            APB3SMENR = 0x0D0, // RCC_APB3SMENR
            SRDAMR = 0x0D8, // RCC_SRDAMR
            CCIPR1 = 0x0E0, // RCC_CCIPR1
            CCIPR2 = 0x0E4, // RCC_CCIPR2
            CCIPR3 = 0x0E8, // RCC_CCIPR3
            BDCR = 0x0F0, // RCC_BDCR
            CSR = 0x0F4, // RCC_CSR
            SECCFGR = 0x110, // RCC_SECCFGR
            PRIVCFGR = 0x114, // RCC_PRIVCFGR
        }
    }
}
