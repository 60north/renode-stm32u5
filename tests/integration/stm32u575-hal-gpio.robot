*** Settings ***
Documentation       Unmodified STM32CubeU5 HAL (v1.9.0 components) drives GPIO and EXTI on the test
...                 fixture with an LED on PA5 and buttons on PB13 and PC13
...                 (firmware-tests/stm32cube/gpio): HAL_GPIO_Init, TogglePin, ReadPin, WritePin and
...                 a rising-edge EXTI interrupt handled by HAL_GPIO_EXTI_IRQHandler.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Run HAL GPIO Firmware

*** Variables ***
${ELF}              ${ROOT}/build/firmware/hal-gpio.elf
${FIXTURE}          ${CURDIR}/../common/stm32u575-gpio-fixture.repl
${UNMODELLED_ACCESS}    Unhandled (read|write)|non existing peripheral

*** Keywords ***
Run HAL GPIO Firmware
    Create STM32U575 Machine        ${FIXTURE}
    Create Log Tester               0
    Execute Command                 $bin=@${ELF}
    Execute Command                 macro reset "sysbus LoadELF $bin"
    Execute Command                 runMacro $reset
    Execute Command                 emulation RunFor "0.01"

Symbol Should Be
    [Arguments]                     ${symbol}    ${expected}
    ${address}=                     Execute Command    sysbus GetSymbolAddress "${symbol}"
    ${address}=                     Strip String       ${address}
    Word Should Be                  ${address}         ${expected}

Set Button And Run
    [Arguments]                     ${button}    ${action}
    Execute Command                 sysbus.gpioPort${button} ${action}
    Execute Command                 emulation RunFor "0.001"

LED Should Be
    [Arguments]                     ${expected}
    ${state}=                       Execute Command    sysbus.gpioPortA.led State
    ${state}=                       Strip String       ${state}
    Should Be Equal                 ${state}           ${expected}

*** Test Cases ***
Should Initialise The Pins And Toggle The Output
    Symbol Should Be                ready              1
    Symbol Should Be                toggleOk           1
    # PA5 output, PB13 input, PC13 input (EXTI); RM0456 section 13.4.1 mode encoding.
    Field Should Be                 0x42020000         10    2    1
    Field Should Be                 0x42020400         26    2    0
    Field Should Be                 0x42020800         26    2    0

Should Mirror An Input On The Output
    LED Should Be                   False
    Set Button And Run              B.buttonB13    Press
    Symbol Should Be                mirroredLevel      1
    LED Should Be                   True
    Set Button And Run              B.buttonB13    Release
    Symbol Should Be                mirroredLevel      0
    LED Should Be                   False

Should Call The HAL Callback On Each Rising Edge
    Symbol Should Be                risingEdges        0
    Set Button And Run              C.buttonC13    Press
    Symbol Should Be                risingEdges        1
    Symbol Should Be                lastRisingPin      0x2000
    # A falling edge is not selected.
    Set Button And Run              C.buttonC13    Release
    Symbol Should Be                risingEdges        1
    Set Button And Run              C.buttonC13    Press
    Symbol Should Be                risingEdges        2

Should Not Touch Unmodelled Registers
    Set Button And Run              B.buttonB13    Press
    Set Button And Run              C.buttonC13    Press
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true
