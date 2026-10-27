*** Settings ***
Documentation       STM32U575 USART/UART/LPUART at register level (RM0456 Rev 7 sections 66, 67):
...                 reset values per instance, prescaler, fields locked while enabled, kernel clock
...                 from RCC, transmission, reception and FIFO flags. The CPU is halted.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create USART Machine

*** Variables ***
${USART1}           0x40013800
${UART4}            0x40004C00
${LPUART1}          0x46002400
${CR1}              0x00
${CR3}              0x08
${BRR}              0x0C
${ISR}              0x1C
${ICR}              0x20
${RDR}              0x24
${TDR}              0x28
${PRESC}            0x2C
${AUTOCR}           0x30
${RCC_CR}           0x46020C00
${RCC_CFGR2}        0x46020C20
${RCC_CCIPR1}       0x46020CE0
${RCC_CCIPR3}       0x46020CE8

${UE}               0x00000001
${RE}               0x00000004
${TE}               0x00000008
${FIFOEN}           0x20000000
${IDLE}             0x00000010
${IDLEIE}           0x00000010
${IDLECF}           0x00000010
${RXNE}             0x00000020
${TC}               0x00000040
${TXFE}             0x00800000
${RXFF}             0x01000000
${RXFT}             0x04000000
${RXFTIE}           0x10000000

*** Keywords ***
Create USART Machine
    Create STM32U575 Machine
    Execute Command                 cpu IsHalted true

Register
    [Arguments]                     ${base}    ${offset}
    ${address}=                     Evaluate           ${base} + ${offset}
    RETURN                          ${address}

Write USART
    [Arguments]                     ${base}    ${offset}    ${value}
    ${address}=                     Register           ${base}    ${offset}
    Write Word                      ${address}         ${value}

USART Should Be
    [Arguments]                     ${base}    ${offset}    ${expected}
    ${address}=                     Register           ${base}    ${offset}
    Word Should Be                  ${address}         ${expected}

USART Bits Should Be Set
    [Arguments]                     ${base}    ${offset}    ${mask}
    ${address}=                     Register           ${base}    ${offset}
    Bits Should Be Set              ${address}         ${mask}

USART Bits Should Be Clear
    [Arguments]                     ${base}    ${offset}    ${mask}
    ${address}=                     Register           ${base}    ${offset}
    Bits Should Be Clear            ${address}         ${mask}

Receive Characters
    [Arguments]                     ${text}
    Execute Command                 usart1 WriteLine "${text}"
    # Reception is paced by the baud rate.
    Execute Command                 emulation RunFor "0.01"

*** Test Cases ***
Should Have Instance Specific Reset Values
    [Documentation]                 RM0456 66.8.11 (full and basic USART), 67.7.9 (LPUART), 66.8.16.
    USART Should Be                 ${USART1}    ${ISR}       0x028000C0
    USART Should Be                 ${UART4}     ${ISR}       0x008000C0
    USART Should Be                 ${LPUART1}   ${ISR}       0x000000C0
    FOR    ${base}    IN    ${USART1}    ${UART4}    ${LPUART1}
        USART Should Be             ${base}    ${CR1}       0
        USART Should Be             ${base}    ${BRR}       0
        USART Should Be             ${base}    ${PRESC}     0
        USART Should Be             ${base}    ${AUTOCR}    0x80000000
    END

Should Divide The Kernel Clock With The Prescaler
    # 4 MHz kernel clock after reset; BRR = 35.
    Write USART                     ${USART1}    ${BRR}      35
    Property Should Be              usart1 BaudRate    114285
    # PRESCALER = 0011: divided by 6 (RM0456 66.8.15).
    Write USART                     ${USART1}    ${PRESC}    3
    Property Should Be              usart1 BaudRate    19047

Should Keep Configuration Fields While Enabled
    Write USART                     ${USART1}    ${CR1}      ${UE}
    Write USART                     ${USART1}    ${PRESC}    5
    USART Should Be                 ${USART1}    ${PRESC}    0
    ${value}=                       Evaluate           ${UE} | ${FIFOEN}
    Write USART                     ${USART1}    ${CR1}      ${value}
    USART Bits Should Be Clear      ${USART1}    ${CR1}      ${FIFOEN}
    Write USART                     ${USART1}    ${CR1}      0
    Write USART                     ${USART1}    ${PRESC}    5
    USART Should Be                 ${USART1}    ${PRESC}    5

Should Take The Kernel Clock From RCC
    Property Should Be              usart1 Frequency    4000000
    # PPRE1 = /2: PCLK1 feeds USART2 (USART2SEL = 00).
    Write Word                      ${RCC_CFGR2}       0x00000040
    Property Should Be              usart2 Frequency    2000000
    Property Should Be              usart1 Frequency    4000000
    # USART1SEL = 10 (HSI16): no clock while HSI16 is off, 16 MHz once it is on.
    Write Word                      ${RCC_CCIPR1}      0x00000002
    Property Should Be              usart1 Frequency    0
    Write Word                      ${RCC_CR}          0x00000135
    Property Should Be              usart1 Frequency    16000000
    # LPUART1SEL = 100 (MSIK, 4 MHz after reset).
    Write Word                      ${RCC_CCIPR3}      0x00000004
    Property Should Be              lpuart1 Frequency    4000000

Should Transmit A Character
    Create Terminal Tester          sysbus.usart1
    Write USART                     ${USART1}    ${BRR}      35
    ${value}=                       Evaluate           ${UE} | ${TE}
    Write USART                     ${USART1}    ${CR1}      ${value}
    Write USART                     ${USART1}    ${TDR}      0x41
    Wait For Prompt On Uart         A
    USART Bits Should Be Set        ${USART1}    ${ISR}      ${TC}
    # TCCF clears TC.
    Write USART                     ${USART1}    ${ICR}      ${TC}
    USART Bits Should Be Clear      ${USART1}    ${ISR}      ${TC}

Should Receive Characters
    Write USART                     ${USART1}    ${BRR}      35
    ${value}=                       Evaluate           ${UE} | ${RE}
    Write USART                     ${USART1}    ${CR1}      ${value}
    USART Bits Should Be Clear      ${USART1}    ${ISR}      ${RXNE}
    Receive Characters              Z
    USART Bits Should Be Set        ${USART1}    ${ISR}      ${RXNE}
    USART Should Be                 ${USART1}    ${RDR}      0x5A

Should Detect An Idle Line After Reception
    [Documentation]                 RM0456 66.8.10: IDLE after a reception, interrupt with IDLEIE,
    ...                             cleared by IDLECF, set again only after new data.
    Write USART                     ${USART1}    ${BRR}      35
    ${value}=                       Evaluate           ${UE} | ${RE} | ${IDLEIE}
    Write USART                     ${USART1}    ${CR1}      ${value}
    Execute Command                 emulation RunFor "0.01"
    USART Bits Should Be Clear      ${USART1}    ${ISR}      ${IDLE}
    Interrupt Line Should Be        usart1             False
    Receive Characters              Z
    USART Bits Should Be Set        ${USART1}    ${ISR}      ${IDLE}
    Interrupt Line Should Be        usart1             True
    Write USART                     ${USART1}    ${ICR}      ${IDLECF}
    USART Bits Should Be Clear      ${USART1}    ${ISR}      ${IDLE}
    Interrupt Line Should Be        usart1             False
    Execute Command                 emulation RunFor "0.01"
    USART Bits Should Be Clear      ${USART1}    ${ISR}      ${IDLE}
    Receive Characters              Y
    USART Bits Should Be Set        ${USART1}    ${ISR}      ${IDLE}

Should Report FIFO Thresholds And Full FIFO
    Write USART                     ${USART1}    ${BRR}      35
    # RXFTCFG = 010: threshold at half the FIFO depth (4 characters); RXFTIE (RM0456 66.8.4).
    ${cr3_value}=                   Evaluate           (2 << 25) | ${RXFTIE}
    Write USART                     ${USART1}    ${CR3}      ${cr3_value}
    ${cr1_value}=                   Evaluate           ${UE} | ${RE} | ${FIFOEN}
    Write USART                     ${USART1}    ${CR1}      ${cr1_value}
    Interrupt Line Should Be        usart1             False
    Receive Characters              ab
    # WriteLine adds a line ending: 3 characters.
    USART Bits Should Be Clear      ${USART1}    ${ISR}      ${RXFT}
    Receive Characters              cdef
    USART Bits Should Be Set        ${USART1}    ${ISR}      ${RXFT}
    Interrupt Line Should Be        usart1             True
    USART Bits Should Be Clear      ${USART1}    ${ISR}      ${RXFF}
    Receive Characters              g
    # 3 + 5 + 2 = 10 characters: FIFO (8) and RDR full.
    USART Bits Should Be Set        ${USART1}    ${ISR}      ${RXFF}

Should Report An Empty Transmit FIFO
    [Documentation]                 USART: TXFE set from reset; LPUART: only in FIFO mode (67.7.9).
    USART Bits Should Be Set        ${USART1}    ${ISR}      ${TXFE}
    USART Bits Should Be Clear      ${LPUART1}   ${ISR}      ${TXFE}
    Write USART                     ${LPUART1}   ${CR1}      ${FIFOEN}
    USART Bits Should Be Set        ${LPUART1}   ${ISR}      ${TXFE}
