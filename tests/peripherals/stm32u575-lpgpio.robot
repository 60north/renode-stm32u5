*** Settings ***
Documentation       STM32U575 LPGPIO1 at register level (RM0456 Rev 7 section 14) with an LED on
...                 output 3 and a button on input 5 (tests/common/stm32u575-lpgpio-fixture.repl).
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create LPGPIO Fixture

*** Variables ***
${LPGPIO}           0x46020000
${MODER}            0x00
${IDR}              0x10
${ODR}              0x14
${BSRR}             0x18
${BRR}              0x28
${PIN3}             0x00000008
${PIN5}             0x00000020

*** Keywords ***
Create LPGPIO Fixture
    Create STM32U575 Machine        ${CURDIR}/../common/stm32u575-lpgpio-fixture.repl
    Execute Command                 cpu IsHalted true

LED Should Be
    [Arguments]                     ${expected}
    ${state}=                       Execute Command    sysbus.lpgpio1.lpLed State
    ${state}=                       Strip String       ${state}
    Should Be Equal                 ${state}           ${expected}

*** Test Cases ***
Should Have Documented Reset Values
    # RM0456 section 14.4: LPGPIO_MODER, LPGPIO_ODR reset 0 (all inputs).
    Word Should Be                  ${LPGPIO} + ${MODER}    0
    Word Should Be                  ${LPGPIO} + ${ODR}      0
    Word Should Be                  ${LPGPIO} + ${IDR}      0

Should Drive An Output Only In Output Mode
    Write Word                      ${LPGPIO} + ${ODR}      ${PIN3}
    LED Should Be                   False
    Write Word                      ${LPGPIO} + ${MODER}    ${PIN3}
    LED Should Be                   True
    Word Should Be                  ${LPGPIO} + ${IDR}      ${PIN3}

Should Set And Reset Bits With BSRR And BRR
    Write Word                      ${LPGPIO} + ${MODER}    ${PIN3}
    Write Word                      ${LPGPIO} + ${BSRR}     ${PIN3}
    LED Should Be                   True
    # Set and reset of the same bit: set wins.
    Write Word                      ${LPGPIO} + ${BSRR}     0x00080008
    Word Should Be                  ${LPGPIO} + ${ODR}      ${PIN3}
    Write Word                      ${LPGPIO} + ${BSRR}     0x00080000
    LED Should Be                   False
    Write Word                      ${LPGPIO} + ${ODR}      ${PIN3}
    Write Word                      ${LPGPIO} + ${BRR}      ${PIN3}
    Word Should Be                  ${LPGPIO} + ${ODR}      0
    LED Should Be                   False

Should Read An Input Into IDR
    Execute Command                 sysbus.lpgpio1.lpButton Press
    Execute Command                 emulation RunFor "0.0001"
    Word Should Be                  ${LPGPIO} + ${IDR}      ${PIN5}
    Execute Command                 sysbus.lpgpio1.lpButton Release
    Execute Command                 emulation RunFor "0.0001"
    Word Should Be                  ${LPGPIO} + ${IDR}      0
