*** Settings ***
Documentation       STM32U575 EXTI (RM0456 Rev 7 section 23) driven by GPIO inputs: edge selection,
...                 pending flags, interrupt mask, software trigger and port selection. The NVIC
...                 pending bit of EXTI13_IRQn (IRQ 24) shows the interrupt output while the CPU is
...                 halted.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create EXTI Fixture

*** Variables ***
${EXTI_RTSR1}       0x46022000
${EXTI_FTSR1}       0x46022004
${EXTI_SWIER1}      0x46022008
${EXTI_RPR1}        0x4602200C
${EXTI_FPR1}        0x46022010
${EXTI_EXTICR4}     0x4602206C
${EXTI_IMR1}        0x46022080
${NVIC_ISER0}       0xE000E100
${NVIC_ISPR0}       0xE000E200
${NVIC_ICPR0}       0xE000E280
${LINE13}           0x00002000
${IRQ24}            0x01000000
# EXTICR4 field EXTI13 (bits 15:8): 0x02 = PC13, 0x01 = PB13 (RM0456 section 23.6.8).
${SELECT_PC13}      0x00000200
${SELECT_PB13}      0x00000100

*** Keywords ***
Create EXTI Fixture
    Create STM32U575 Machine        ${CURDIR}/../common/stm32u575-gpio-fixture.repl
    Execute Command                 cpu IsHalted true
    # PC13 and PB13 as inputs; enable IRQ 24 in the NVIC.
    Write Word                      0x42020800         0xF3FFFFFF
    Write Word                      0x42020400         0xF3FFFEBF
    Write Word                      ${NVIC_ISER0}      ${IRQ24}

Set Button
    [Arguments]                     ${button}    ${action}
    Execute Command                 sysbus.gpioPort${button} ${action}
    Execute Command                 emulation RunFor "0.0001"

Clear Pending
    Write Word                      ${EXTI_RPR1}       ${LINE13}
    Write Word                      ${EXTI_FPR1}       ${LINE13}
    Write Word                      ${NVIC_ICPR0}      ${IRQ24}

*** Test Cases ***
Should Raise The Interrupt On An Enabled Rising Edge
    Write Word                      ${EXTI_EXTICR4}    ${SELECT_PC13}
    Write Word                      ${EXTI_RTSR1}      ${LINE13}
    Write Word                      ${EXTI_IMR1}       ${LINE13}
    Set Button                      C.buttonC13    Press
    Word Should Be                  ${EXTI_RPR1}       ${LINE13}
    Word Should Be                  ${EXTI_FPR1}       0
    Bits Should Be Set              ${NVIC_ISPR0}      ${IRQ24}

Should Raise The Interrupt On An Enabled Falling Edge
    Write Word                      ${EXTI_EXTICR4}    ${SELECT_PC13}
    Write Word                      ${EXTI_FTSR1}      ${LINE13}
    Write Word                      ${EXTI_IMR1}       ${LINE13}
    Set Button                      C.buttonC13    Press
    Word Should Be                  ${EXTI_FPR1}       0
    Bits Should Be Clear            ${NVIC_ISPR0}      ${IRQ24}
    Set Button                      C.buttonC13    Release
    Word Should Be                  ${EXTI_FPR1}       ${LINE13}
    Word Should Be                  ${EXTI_RPR1}       0
    Bits Should Be Set              ${NVIC_ISPR0}      ${IRQ24}

Should Set The Pending Flag But Not Interrupt While Masked
    Write Word                      ${EXTI_EXTICR4}    ${SELECT_PC13}
    Write Word                      ${EXTI_RTSR1}      ${LINE13}
    Set Button                      C.buttonC13    Press
    Word Should Be                  ${EXTI_RPR1}       ${LINE13}
    Bits Should Be Clear            ${NVIC_ISPR0}      ${IRQ24}
    # Unmasking a pending line raises the interrupt.
    Write Word                      ${EXTI_IMR1}       ${LINE13}
    Bits Should Be Set              ${NVIC_ISPR0}      ${IRQ24}

Should Ignore Edges That Are Not Selected
    Write Word                      ${EXTI_EXTICR4}    ${SELECT_PC13}
    Write Word                      ${EXTI_IMR1}       ${LINE13}
    Set Button                      C.buttonC13    Press
    Set Button                      C.buttonC13    Release
    Word Should Be                  ${EXTI_RPR1}       0
    Word Should Be                  ${EXTI_FPR1}       0
    Bits Should Be Clear            ${NVIC_ISPR0}      ${IRQ24}

Should Clear The Interrupt When The Pending Flag Is Cleared
    Write Word                      ${EXTI_EXTICR4}    ${SELECT_PC13}
    Write Word                      ${EXTI_RTSR1}      ${LINE13}
    Write Word                      ${EXTI_IMR1}       ${LINE13}
    Set Button                      C.buttonC13    Press
    Clear Pending
    Word Should Be                  ${EXTI_RPR1}       0
    Bits Should Be Clear            ${NVIC_ISPR0}      ${IRQ24}

Should Trigger A Software Event Whatever The Edge Selection
    Write Word                      ${EXTI_IMR1}       ${LINE13}
    Write Word                      ${EXTI_SWIER1}     ${LINE13}
    Word Should Be                  ${EXTI_SWIER1}     0
    Word Should Be                  ${EXTI_RPR1}       ${LINE13}
    Bits Should Be Set              ${NVIC_ISPR0}      ${IRQ24}

Should Listen Only To The Selected Port
    Write Word                      ${EXTI_EXTICR4}    ${SELECT_PC13}
    Write Word                      ${EXTI_RTSR1}      ${LINE13}
    Write Word                      ${EXTI_IMR1}       ${LINE13}
    Set Button                      B.buttonB13    Press
    Word Should Be                  ${EXTI_RPR1}       0
    Set Button                      B.buttonB13    Release
    Write Word                      ${EXTI_EXTICR4}    ${SELECT_PB13}
    Set Button                      B.buttonB13    Press
    Word Should Be                  ${EXTI_RPR1}       ${LINE13}
    Bits Should Be Set              ${NVIC_ISPR0}      ${IRQ24}

Should Not Treat A Source Change As An Edge
    [Documentation]                 PB13 is high when EXTI13 is switched from PC13 to PB13.
    Write Word                      ${EXTI_EXTICR4}    ${SELECT_PC13}
    Write Word                      ${EXTI_RTSR1}      ${LINE13}
    Write Word                      ${EXTI_IMR1}       ${LINE13}
    Set Button                      B.buttonB13    Press
    Write Word                      ${EXTI_EXTICR4}    ${SELECT_PB13}
    Word Should Be                  ${EXTI_RPR1}       0
