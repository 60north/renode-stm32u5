*** Settings ***
Documentation       Unmodified STM32CubeU5 HAL (v1.9.0 components) on SPI1 (full-featured) and SPI3
...                 (limited) with a loopback slave, so every received frame equals the sent one
...                 (firmware-tests/stm32cube/spi): HAL_SPI_TransmitReceive with 8-bit and 16-bit
...                 frames, HAL_SPI_TransmitReceive_IT, and an 8-bit transfer on SPI3.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Run HAL SPI Firmware

*** Variables ***
${ELF}              ${ROOT}/build/firmware/hal-spi.elf
${STEP_DONE}        12
${UNMODELLED_ACCESS}    Unhandled (read|write)|non existing peripheral

*** Keywords ***
Run HAL SPI Firmware
    Create STM32U575 Machine
    Create Log Tester               0
    Execute Command                 machine LoadPlatformDescriptionFromString "loopback1: SPI.SPILoopback @ spi1"
    Execute Command                 machine LoadPlatformDescriptionFromString "loopback3: SPI.SPILoopback @ spi3"
    Execute Command                 $bin=@${ELF}
    Execute Command                 macro reset "sysbus LoadELF $bin"
    Execute Command                 runMacro $reset
    Execute Command                 emulation RunFor "0.1"

Symbol Should Be
    [Arguments]                     ${symbol}    ${expected}
    ${address}=                     Execute Command    sysbus GetSymbolAddress "${symbol}"
    ${address}=                     Strip String       ${address}
    Word Should Be                  ${address}         ${expected}

*** Test Cases ***
Should Complete All Transfers
    Symbol Should Be                failedStep         0
    Symbol Should Be                step               ${STEP_DONE}

Should Loop Back 8-Bit Frames By Polling
    Symbol Should Be                polling8Ok         1

Should Loop Back 8-Bit Frames By Interrupt
    Symbol Should Be                interrupt8Ok       1
    Symbol Should Be                transferComplete   1

Should Loop Back 16-Bit Frames
    Symbol Should Be                polling16Ok        1

Should Loop Back 8-Bit Frames On The Limited SPI3
    Symbol Should Be                limited8Ok         1

Should Not Touch Unmodelled Registers
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true
