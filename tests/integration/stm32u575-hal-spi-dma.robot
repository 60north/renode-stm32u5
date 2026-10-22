*** Settings ***
Documentation       Unmodified STM32CubeU5 HAL (v1.9.0 components), SPI1 with GPDMA1 in normal mode:
...                 transmission on the SPI1_TX request (firmware-tests/stm32cube/spi-dma, 8 bytes to
...                 a slave that logs each byte) and full-duplex transfer with SPI1_TX and SPI1_RX
...                 requests through a loopback (firmware-tests/stm32cube/spi-dma-loopback, 16 bytes).
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Run HAL SPI DMA Firmware

*** Variables ***
${ELF}              ${ROOT}/build/firmware/hal-spi-dma.elf
${LOOPBACK_ELF}     ${ROOT}/build/firmware/hal-spi-dma-loopback.elf
${STEP_DONE}        6
${UNMODELLED_ACCESS}    Unhandled (read|write)|non existing peripheral

*** Keywords ***
Run HAL SPI DMA Firmware
    Create STM32U575 Machine
    Create Log Tester               0
    Execute Command                 machine LoadPlatformDescriptionFromString "slave: Mocks.DummySPISlave @ spi1"
    Execute Command                 logLevel 0 sysbus.spi1.slave
    Execute Command                 $bin=@${ELF}
    Execute Command                 macro reset "sysbus LoadELF $bin"
    Execute Command                 runMacro $reset
    Execute Command                 emulation RunFor "0.1"

Run HAL SPI DMA Loopback Firmware
    Create STM32U575 Machine
    Create Log Tester               0
    Execute Command                 machine LoadPlatformDescriptionFromString "loopback: SPI.SPILoopback @ spi1"
    Execute Command                 $bin=@${LOOPBACK_ELF}
    Execute Command                 macro reset "sysbus LoadELF $bin"
    Execute Command                 runMacro $reset
    Execute Command                 emulation RunFor "0.1"

Symbol Should Be
    [Arguments]                     ${symbol}    ${expected}
    ${address}=                     Execute Command    sysbus GetSymbolAddress "${symbol}"
    ${address}=                     Strip String       ${address}
    Word Should Be                  ${address}         ${expected}

*** Test Cases ***
Should Complete The DMA Transmission
    Symbol Should Be                failedStep         0
    Symbol Should Be                transferError      0
    Symbol Should Be                transferComplete   1
    Symbol Should Be                step               ${STEP_DONE}

Should Send Every Byte To The Slave In Order
    Wait For Log Entry              Data received: 0xA1 (idx: 0)    timeout=0
    Wait For Log Entry              Data received: 0xD4 (idx: 3)    timeout=0
    Wait For Log Entry              Data received: 0x18 (idx: 7)    timeout=0
    Should Not Be In Log            (idx: 8)    treatAsRegex=false

Should Not Touch Unmodelled Registers
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true

Should Receive By DMA What Was Sent By DMA
    [Setup]                         Run HAL SPI DMA Loopback Firmware
    Symbol Should Be                failedStep         0
    Symbol Should Be                transferError      0
    Symbol Should Be                transferComplete   1
    Symbol Should Be                dataOk             1
    Symbol Should Be                step               ${STEP_DONE}
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true
