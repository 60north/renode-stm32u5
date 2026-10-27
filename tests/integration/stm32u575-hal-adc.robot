*** Settings ***
Documentation       Unmodified STM32CubeU5 HAL (v1.9.0 components) with ADC1 (firmware-tests/stm32cube/adc):
...                 12-bit software-triggered conversions of channel 5 after offset calibration (the
...                 extended procedure: DBGMCU_IDCODE reports revision W), with the input voltage set
...                 from Renode (VREF+ 3.3 V in the platform description).
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Run HAL ADC Firmware

*** Variables ***
${ELF}              ${ROOT}/build/firmware/hal-adc.elf
${ADC1}             0x42028000
${STEP_DONE}        6
${UNMODELLED_ACCESS}    Unhandled (read|write)|non existing peripheral

*** Keywords ***
Run HAL ADC Firmware
    Create STM32U575 Machine
    Create Log Tester               0
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

Converted Value Should Be
    [Arguments]                     ${volts}    ${expected}
    Execute Command                 sysbus.adc1.adc1-channel5 Volts ${volts}
    Execute Command                 emulation RunFor "0.05"
    Symbol Should Be                value              ${expected}

*** Test Cases ***
Should Initialize And Calibrate ADC1 With HAL
    Symbol Should Be                failedStep         0
    ${step}=                        Read Symbol        step
    Should Be True                  ${step} >= ${STEP_DONE}    msg=step ${step}
    ${conversions}=                 Read Symbol        conversions
    Should Be True                  ${conversions} > 0    msg=no conversion
    # CFGR1.RES = 01: 12 bits on ADC1 (RM0456 33.7.4).
    ${cfgr1}=                       Read Word          ${ADC1} + 0x0C
    ${res}=                         Evaluate           (${cfgr1} >> 2) & 3
    Should Be Equal As Integers     ${res}             1

Should Convert The Input Voltage With 12-Bit Resolution
    # value = V / 3.3 V * 4096, truncated
    Converted Value Should Be       1.65               2048
    Converted Value Should Be       0.825              1024
    Converted Value Should Be       0                  0

Should Not Touch Unmodelled Registers
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true
