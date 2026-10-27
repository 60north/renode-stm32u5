*** Settings ***
Documentation       Unmodified STM32CubeU5 HAL (v1.9.0 components): USART1 reception into a 16-byte ring
...                 by GPDMA1 channel 3 in circular linked-list mode, configured as STM32CubeMX does
...                 (firmware-tests/stm32cube/uart-dma-circular). 40 characters wrap the ring 2.5 times.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Run HAL UART Circular DMA Firmware

*** Variables ***
${ELF}              ${ROOT}/build/firmware/hal-uart-dma-circular.elf
${UART}             sysbus.usart1
${STEP_RUNNING}     9
${UNMODELLED_ACCESS}    Unhandled (read|write)|non existing peripheral

*** Keywords ***
Run HAL UART Circular DMA Firmware
    Create STM32U575 Machine
    Create Log Tester               0
    Execute Command                 $bin=@${ELF}
    Execute Command                 macro reset "sysbus LoadELF $bin"
    Execute Command                 runMacro $reset
    Create Terminal Tester          ${UART}    defaultPauseEmulation=true

Symbol Address
    [Arguments]                     ${symbol}
    ${address}=                     Execute Command    sysbus GetSymbolAddress "${symbol}"
    ${address}=                     Strip String       ${address}
    RETURN                          ${address}

Symbol Should Be
    [Arguments]                     ${symbol}    ${expected}
    ${address}=                     Symbol Address     ${symbol}
    Word Should Be                  ${address}         ${expected}

*** Test Cases ***
Should Wrap The Receive Ring With Half And Full Events
    Wait For Line On Uart           STM32U575 HAL UART circular DMA ready
    Execute Command                 emulation RunFor "0.01"
    Symbol Should Be                step               ${STEP_RUNNING}
    Write To Uart                   abcdefghijklmnopqrstuvwxyz0123456789ABCD
    Execute Command                 emulation RunFor "0.05"
    Symbol Should Be                failedStep         0
    Symbol Should Be                errorCount         0
    # Half events after 8, 24 and 40 bytes; full events after 16 and 32 bytes.
    Symbol Should Be                halfCount          3
    Symbol Should Be                fullCount          2
    # The last 16 characters, wrapped: ring[0..7] = "6789ABCD", ring[8..15] = "yz012345".
    ${ring}=                        Symbol Address     rxRing
    ${bytes}=                       Execute Command    sysbus ReadBytes ${ring} 16
    ${bytes}=                       Evaluate           " ".join($bytes.split())
    Should Contain                  ${bytes}           0x36, 0x37, 0x38, 0x39, 0x41, 0x42, 0x43, 0x44, 0x79, 0x7A, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true
