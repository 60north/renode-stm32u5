*** Settings ***
Documentation       STM32U575 clock initialisation to 160 MHz by bare-metal firmware
...                 (firmware-tests/baremetal/clock): voltage range 1, EPOD booster, PLL1 from MSIS,
...                 four flash wait states, switch to PLL1. Each stage of the firmware has a timeout.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Run Clock Init Firmware

*** Variables ***
${ELF}              ${ROOT}/build/firmware/clock-init.elf
${STAGE_DONE}       6

*** Keywords ***
Run Clock Init Firmware
    Create STM32U575 Machine
    Execute Command                 $bin=@${ELF}
    Execute Command                 macro reset "sysbus LoadELF $bin"
    Execute Command                 runMacro $reset
    Execute Command                 emulation RunFor "0.01"

Symbol Should Be
    [Arguments]                     ${symbol}    ${expected}
    ${address}=                     Execute Command    sysbus GetSymbolAddress "${symbol}"
    ${address}=                     Strip String       ${address}
    Word Should Be                  ${address}         ${expected}

Read Symbol
    [Arguments]                     ${symbol}
    ${address}=                     Execute Command    sysbus GetSymbolAddress "${symbol}"
    ${address}=                     Strip String       ${address}
    ${value}=                       Read Word          ${address}
    RETURN                          ${value}

*** Test Cases ***
Should Complete Every Clock Initialisation Stage
    Symbol Should Be                failedStage        0
    Symbol Should Be                stage              ${STAGE_DONE}

Should Run The Core At 160 MHz
    Property Should Be              rcc SystemClockFrequency    160000000
    Property Should Be              nvic Frequency     160000000
    # SWS = PLL1, PLL1RDY, flash latency 4, voltage range 1 with booster ready.
    Field Should Be                 0x46020C1C         2    2    3
    Bits Should Be Set              0x46020C00         0x02000000
    Field Should Be                 0x40022000         0    4    4
    Field Should Be                 0x4602080C         16    2    3
    Bits Should Be Set              0x4602080C         0x0000C000

Should Tick At 1 kHz After Switching To 160 MHz
    [Documentation]                 SysTick is reloaded for 160 MHz after the switch: 100 ticks in 100 ms.
    ${before}=                      Read Symbol        sysTickCount
    Execute Command                 emulation RunFor "0.1"
    ${after}=                       Read Symbol        sysTickCount
    ${ticks}=                       Evaluate           ${after} - ${before}
    Should Be Equal As Integers     ${ticks}           100
