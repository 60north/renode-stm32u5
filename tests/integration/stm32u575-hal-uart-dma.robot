*** Settings ***
Documentation       Unmodified STM32CubeU5 HAL (v1.9.0 components): USART1 reception by GPDMA1
...                 channel 2 on the USART1_RX request (firmware-tests/stm32cube/uart-dma), normal
...                 DMA mode. HAL_UART_Receive_DMA of 12 bytes, completion by
...                 HAL_UART_RxCpltCallback, received text checked by the firmware.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Run HAL UART DMA Firmware

*** Variables ***
${ELF}              ${ROOT}/build/firmware/hal-uart-dma.elf
${UART}             sysbus.usart1
${STEP_DONE}        10
${UNMODELLED_ACCESS}    Unhandled (read|write)|non existing peripheral

*** Keywords ***
Run HAL UART DMA Firmware
    Create STM32U575 Machine
    Create Log Tester               0
    Execute Command                 $bin=@${ELF}
    Execute Command                 macro reset "sysbus LoadELF $bin"
    Execute Command                 runMacro $reset
    Create Terminal Tester          ${UART}    defaultPauseEmulation=true

Symbol Should Be
    [Arguments]                     ${symbol}    ${expected}
    ${address}=                     Execute Command    sysbus GetSymbolAddress "${symbol}"
    ${address}=                     Strip String       ${address}
    Word Should Be                  ${address}         ${expected}

*** Test Cases ***
Should Receive Text By DMA
    Wait For Line On Uart           STM32U575 HAL UART DMA ready
    Write To Uart                   hello renode
    Execute Command                 emulation RunFor "0.01"
    Symbol Should Be                failedStep         0
    Symbol Should Be                receiveError       0
    Symbol Should Be                receiveComplete    1
    Symbol Should Be                dataOk             1
    Symbol Should Be                step               ${STEP_DONE}
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true
