*** Settings ***
Documentation       Unmodified STM32CubeU5 HAL (v1.9.0 components) programming the flash memory
...                 (firmware-tests/stm32cube/flash): page erase, quad-word programming, PROGERR when a
...                 quad-word is not erased, all-zero programming allowed (RM0456 section 7.3.9), data kept
...                 over a reset.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Run HAL Flash Firmware

*** Variables ***
${ELF}              ${ROOT}/build/firmware/hal-flash.elf
${STEP_DONE}        9
${STEP_FOUND}       10
${MARKER}           0x4D4D4D31
${HAL_ERROR}        1
${HAL_FLASH_ERROR_PROG}    0x8
${UNMODELLED_ACCESS}    Unhandled (read|write)|non existing peripheral

*** Keywords ***
Run HAL Flash Firmware
    Create STM32U575 Machine
    Create Log Tester               0
    Execute Command                 $bin=@${ELF}
    Execute Command                 macro reset "sysbus LoadELF $bin"
    Execute Command                 runMacro $reset
    Execute Command                 emulation RunFor "0.05"

Read Symbol
    [Arguments]                     ${symbol}    ${index}=0
    ${address}=                     Execute Command    sysbus GetSymbolAddress "${symbol}"
    ${address}=                     Evaluate           int("${address.strip()}", 16) + 4 * ${index}
    ${value}=                       Read Word          ${address}
    RETURN                          ${value}

Symbol Should Be
    [Arguments]                     ${symbol}    ${expected}    ${index}=0
    ${value}=                       Read Symbol        ${symbol}    ${index}
    Should Be Equal As Integers     ${value}           ${expected}    msg=${symbol} word ${index}

*** Test Cases ***
Should Erase And Program A Page With HAL
    Symbol Should Be                failedStep         0
    Symbol Should Be                step               ${STEP_DONE}
    Symbol Should Be                erasedWord         0xFFFFFFFF
    Symbol Should Be                programmedWords    ${MARKER}       0
    Symbol Should Be                programmedWords    0x11111111      1
    Symbol Should Be                programmedWords    0x22222222      2
    Symbol Should Be                programmedWords    0x33333333      3

Should Report PROGERR When The Quad-Word Is Not Erased
    Symbol Should Be                overwriteStatus    ${HAL_ERROR}
    Symbol Should Be                overwriteError     ${HAL_FLASH_ERROR_PROG}
    # Programming all zeros over a programmed quad-word is allowed.
    Symbol Should Be                zeroStatus         0
    Symbol Should Be                zeroWord           0

Should Keep The Programmed Data Over A Reset
    Execute Command                 machine Reset
    Execute Command                 runMacro $reset
    Execute Command                 emulation RunFor "0.05"
    Symbol Should Be                foundAfterReset    1
    Symbol Should Be                step               ${STEP_FOUND}

Should Not Touch Unmodelled Registers
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true
