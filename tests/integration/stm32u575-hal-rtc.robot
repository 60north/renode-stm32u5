*** Settings ***
Documentation       Unmodified STM32CubeU5 HAL (v1.9.0 components) with the RTC (firmware-tests/stm32cube/rtc):
...                 LSE clock, calendar set to 2026-10-23 12:34:56, alarm A at 12:35:00 and the wake-up
...                 timer every second, both on the RTC interrupt.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Run HAL RTC Firmware

*** Variables ***
${ELF}              ${ROOT}/build/firmware/hal-rtc.elf
${RTC}              0x46007800
${STEP_DONE}        9

*** Keywords ***
Run HAL RTC Firmware
    Create STM32U575 Machine With External Oscillators
    Execute Command                 $bin=@${ELF}
    Execute Command                 macro reset "sysbus LoadELF $bin"
    Execute Command                 runMacro $reset
    Execute Command                 emulation RunFor "0.05"

Read Symbol
    [Arguments]                     ${symbol}
    ${address}=                     Execute Command    sysbus GetSymbolAddress "${symbol}"
    ${address}=                     Strip String       ${address}
    ${value}=                       Read Word          ${address}
    RETURN                          ${value}

Symbol Should Be
    [Arguments]                     ${symbol}    ${expected}
    ${value}=                       Read Symbol        ${symbol}
    Should Be Equal As Integers     ${value}           ${expected}    msg=${symbol}

*** Test Cases ***
Should Initialize The RTC With HAL
    Symbol Should Be                failedStep         0
    Symbol Should Be                step               ${STEP_DONE}
    # PRER = PREDIV_A 127, PREDIV_S 255; CR: ALRAE, WUTE, ALRAIE, WUTIE, WUCKSEL = 100, and
    # BYPSHAD, which the model always reports set (shadow registers are not modelled).
    Word Should Be                  ${RTC} + 0x10      0x007F00FF
    Word Should Be                  ${RTC} + 0x18      0x00005524

Should Keep The Calendar Set By HAL
    # DR: year 26, Friday, October 23 (BCD).
    Word Should Be                  ${RTC} + 0x04      0x0026B023
    Execute Command                 emulation RunFor "2"
    Symbol Should Be                hours              12
    Symbol Should Be                minutes            34
    Symbol Should Be                seconds            58
    Symbol Should Be                year               26
    Symbol Should Be                month              10
    Symbol Should Be                day                23
    Symbol Should Be                weekDay            5

Should Raise The Wake-Up Interrupt Every Second
    ${start}=                       Read Symbol        wakeupCount
    Execute Command                 emulation RunFor "3"
    ${end}=                         Read Symbol        wakeupCount
    ${count}=                       Evaluate           ${end} - ${start}
    Should Be True                  2 <= ${count} <= 4    msg=${count} wake-up events in 3 s

Should Raise Alarm A At The Programmed Time
    Execute Command                 emulation RunFor "3"
    Symbol Should Be                alarmCount         0
    Execute Command                 emulation RunFor "2"
    Symbol Should Be                alarmCount         1
    Symbol Should Be                alarmMinute        35
