*** Settings ***
Documentation       STM32U575 RCC behaviour at register level (RM0456 Rev 7 section 11).
...                 Register offsets and bit positions: stm32u575xx.h.
Resource            ${CURDIR}/../common/stm32u575.resource

*** Variables ***
${RCC_CR}           0x46020C00
${RCC_ICSCR1}       0x46020C08
${RCC_CFGR1}        0x46020C1C
${RCC_CFGR2}        0x46020C20
${RCC_CFGR3}        0x46020C24
${RCC_PLL1CFGR}     0x46020C28
${RCC_PLL1DIVR}     0x46020C34
${RCC_PLL1FRACR}    0x46020C38
${RCC_CIER}         0x46020C50
${RCC_CIFR}         0x46020C54
${RCC_CICR}         0x46020C58
${RCC_BDCR}         0x46020CF0
${RCC_CSR}          0x46020CF4
${PWR_DBPR}         0x46020828

${CR_RESET}         0x00000035
${HSION}            0x00000100
${HSIRDY}           0x00000400
${HSI48ON}          0x00001000
${HSI48RDY}         0x00002000
${SHSION}           0x00004000
${SHSIRDY}          0x00008000
${HSEON}            0x00010000
${HSERDY}           0x00020000
${PLL1ON}           0x01000000
${PLL1RDY}          0x02000000

# PLL1: source MSIS, M = 1, R output enabled; N = 80, P = Q = R = 2.
${PLL1CFGR_MSIS}    0x00040001
# PLL1: source HSE, M = 4, R output enabled.
${PLL1CFGR_HSE}     0x00040303
${PLL1DIVR_N80}     0x0101024F

*** Keywords ***
Configure PLL1 And Switch System Clock
    [Arguments]                     ${configuration}    ${control}
    Write Word                      ${RCC_PLL1CFGR}    ${configuration}
    Write Word                      ${RCC_PLL1DIVR}    ${PLL1DIVR_N80}
    Write Word                      ${RCC_CR}          ${control}
    Bits Should Be Set              ${RCC_CR}          ${PLL1RDY}
    Write Word                      ${RCC_CFGR1}       0x3
    Field Should Be                 ${RCC_CFGR1}       2    2    3

*** Test Cases ***
Should Run From MSIS At 4 MHz After Reset
    Create STM32U575 Machine
    Field Should Be                 ${RCC_CFGR1}       2    2    0
    Property Should Be              rcc SystemClockFrequency    4000000
    Property Should Be              nvic Frequency     4000000

Should Report Internal Oscillators Ready Only While Enabled
    Create STM32U575 Machine
    Bits Should Be Clear            ${RCC_CR}          ${HSIRDY} | ${HSI48RDY} | ${SHSIRDY}
    ${on}=                          Evaluate           ${CR_RESET} | ${HSION} | ${HSI48ON} | ${SHSION}
    Write Word                      ${RCC_CR}          ${on}
    Bits Should Be Set              ${RCC_CR}          ${HSIRDY} | ${HSI48RDY} | ${SHSIRDY}
    Write Word                      ${RCC_CR}          ${CR_RESET}
    Bits Should Be Clear            ${RCC_CR}          ${HSIRDY} | ${HSI48RDY} | ${SHSIRDY}

Should Not Report HSE Ready Without An External Oscillator
    Create STM32U575 Machine
    ${on}=                          Evaluate           ${CR_RESET} | ${HSEON}
    Write Word                      ${RCC_CR}          ${on}
    Bits Should Be Clear            ${RCC_CR}          ${HSERDY}

Should Report HSE Ready With An External Oscillator
    Create STM32U575 Machine With External Oscillators
    ${on}=                          Evaluate           ${CR_RESET} | ${HSEON}
    Write Word                      ${RCC_CR}          ${on}
    Bits Should Be Set              ${RCC_CR}          ${HSERDY}

Should Run At 160 MHz From PLL1 Fed By MSIS
    Create STM32U575 Machine
    ${control}=                     Evaluate           ${CR_RESET} | ${PLL1ON}
    Configure PLL1 And Switch System Clock    ${PLL1CFGR_MSIS}    ${control}
    Property Should Be              rcc SystemClockFrequency    160000000
    Property Should Be              nvic Frequency     160000000

Should Run At 160 MHz From PLL1 Fed By HSE
    Create STM32U575 Machine With External Oscillators
    ${control}=                     Evaluate           ${CR_RESET} | ${HSEON} | ${PLL1ON}
    Configure PLL1 And Switch System Clock    ${PLL1CFGR_HSE}    ${control}
    Property Should Be              rcc SystemClockFrequency    160000000

Should Apply The PLL1 Fractional Multiplier
    [Documentation]                 VCO = 4 MHz x (80 + 4096 / 8192) = 322 MHz, R = 2.
    Create STM32U575 Machine
    Write Word                      ${RCC_PLL1FRACR}    0x00008000
    ${configuration}=               Evaluate           ${PLL1CFGR_MSIS} | 0x10
    ${control}=                     Evaluate           ${CR_RESET} | ${PLL1ON}
    Configure PLL1 And Switch System Clock    ${configuration}    ${control}
    Property Should Be              rcc SystemClockFrequency    161000000

Should Not Switch To A Source That Is Not Ready
    Create STM32U575 Machine
    Write Word                      ${RCC_CFGR1}       0x2
    Field Should Be                 ${RCC_CFGR1}       2    2    0
    Property Should Be              rcc SystemClockFrequency    4000000

Should Ignore PLL1 Configuration Writes While PLL1 Is On
    Create STM32U575 Machine
    Write Word                      ${RCC_PLL1CFGR}    ${PLL1CFGR_MSIS}
    Write Word                      ${RCC_PLL1DIVR}    ${PLL1DIVR_N80}
    ${control}=                     Evaluate           ${CR_RESET} | ${PLL1ON}
    Write Word                      ${RCC_CR}          ${control}
    Write Word                      ${RCC_PLL1DIVR}    0x0101013F
    Word Should Be                  ${RCC_PLL1DIVR}    ${PLL1DIVR_N80}

Should Keep The System Clock Sources Enabled
    Create STM32U575 Machine
    # MSISON is set by hardware while MSIS is the system clock.
    Write Word                      ${RCC_CR}          0x00000034
    Bits Should Be Set              ${RCC_CR}          0x00000005
    ${control}=                     Evaluate           ${CR_RESET} | ${PLL1ON}
    Configure PLL1 And Switch System Clock    ${PLL1CFGR_MSIS}    ${control}
    # PLL1ON cannot be cleared while PLL1 is the system clock; MSIS feeds PLL1.
    Write Word                      ${RCC_CR}          0x00000000
    Bits Should Be Set              ${RCC_CR}          ${PLL1ON} | ${PLL1RDY} | 0x00000005

Should Select The MSIS Range From ICSCR1 Or CSR
    Create STM32U575 Machine
    # MSIRGSEL = 1, MSISRANGE = 0 (48 MHz), MSIKRANGE unchanged (4).
    Write Word                      ${RCC_ICSCR1}      0x04800000
    Property Should Be              rcc SystemClockFrequency    48000000
    # MSISRANGE = 8 (3.072 MHz).
    Write Word                      ${RCC_ICSCR1}      0x84800000
    Property Should Be              rcc SystemClockFrequency    3072000
    # MSIRGSEL = 0: range from RCC_CSR.MSISSRANGE (4 MHz after reset).
    Write Word                      ${RCC_ICSCR1}      0x84000000
    Property Should Be              rcc SystemClockFrequency    4000000

Should Divide The Bus Clocks
    Create STM32U575 Machine
    # HPRE /2, PPRE1 /4, PPRE2 /2; PPRE3 /16.
    Write Word                      ${RCC_CFGR2}       0x00000458
    Write Word                      ${RCC_CFGR3}       0x00000070
    Property Should Be              rcc AhbFrequency   2000000
    Property Should Be              rcc Apb1Frequency    500000
    Property Should Be              rcc Apb2Frequency    1000000
    Property Should Be              rcc Apb3Frequency    125000
    Property Should Be              nvic Frequency     2000000
    # HPRE /64.
    Write Word                      ${RCC_CFGR2}       0x0000000C
    Property Should Be              rcc AhbFrequency   62500

Should Raise The Ready Interrupt Only When Enabled
    Create STM32U575 Machine
    ${on}=                          Evaluate           ${CR_RESET} | ${HSION}
    Write Word                      ${RCC_CR}          ${on}
    Word Should Be                  ${RCC_CIFR}        0
    Interrupt Line Should Be        rcc                False
    Write Word                      ${RCC_CR}          ${CR_RESET}
    Write Word                      ${RCC_CIER}        0x00000008
    Write Word                      ${RCC_CR}          ${on}
    Word Should Be                  ${RCC_CIFR}        0x00000008
    Interrupt Line Should Be        rcc                True
    Write Word                      ${RCC_CICR}        0x00000008
    Word Should Be                  ${RCC_CIFR}        0
    Interrupt Line Should Be        rcc                False

Should Protect The Backup Domain Until DBP Is Set
    Create STM32U575 Machine
    Write Word                      ${RCC_BDCR}        0x04000000
    Word Should Be                  ${RCC_BDCR}        0
    Write Word                      ${PWR_DBPR}        0x1
    Write Word                      ${RCC_BDCR}        0x04000000
    # LSION and LSIRDY.
    Word Should Be                  ${RCC_BDCR}        0x0C000000

Should Start LSE And Reset The Backup Domain
    Create STM32U575 Machine With External Oscillators
    Write Word                      ${PWR_DBPR}        0x1
    # LSEON and LSESYSEN: LSERDY and LSESYSRDY follow.
    Write Word                      ${RCC_BDCR}        0x00000081
    Word Should Be                  ${RCC_BDCR}        0x00000883
    # BDRST clears the backup domain configuration.
    Write Word                      ${RCC_BDCR}        0x00010081
    Word Should Be                  ${RCC_BDCR}        0x00010000

Should Clear Reset Flags With RMVF
    Create STM32U575 Machine
    Word Should Be                  ${RCC_CSR}         0x0C004400
    Write Word                      ${RCC_CSR}         0x00804400
    Word Should Be                  ${RCC_CSR}         0x00004400

Should Keep Peripheral Clock Enables
    [Documentation]                 Enable bits are stored; clock gating does not affect the models.
    Create STM32U575 Machine
    # RCC_AHB1ENR GPDMA1EN, RCC_AHB2ENR1 GPIOAEN, RCC_APB1ENR1 I2C1EN, RCC_APB1ENR2 FDCAN1EN,
    # RCC_APB2ENR SPI1EN and USART1EN.
    Bits Should Be Clear            0x46020C88         0x00000001
    Write Word                      0x46020C88         0xD0000101
    Bits Should Be Set              0x46020C88         0x00000001
    Write Word                      0x46020C8C         0xC0000001
    Bits Should Be Set              0x46020C8C         0x00000001
    Write Word                      0x46020C9C         0x00200000
    Word Should Be                  0x46020C9C         0x00200000
    Write Word                      0x46020CA0         0x00000200
    Word Should Be                  0x46020CA0         0x00000200
    Write Word                      0x46020CA4         0x00005000
    Word Should Be                  0x46020CA4         0x00005000
    Write Word                      0x46020CA4         0x00000000
    Word Should Be                  0x46020CA4         0

Should Not Lock PLL1 Without An Input Clock
    Create STM32U575 Machine
    # PLL1SRC = 00 (no clock), R output enabled.
    Write Word                      ${RCC_PLL1CFGR}    0x00040000
    ${control}=                     Evaluate           ${CR_RESET} | ${PLL1ON}
    Write Word                      ${RCC_CR}          ${control}
    Bits Should Be Clear            ${RCC_CR}          ${PLL1RDY}
    Write Word                      ${RCC_CFGR1}       0x3
    Field Should Be                 ${RCC_CFGR1}       2    2    0
