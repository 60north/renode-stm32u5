*** Settings ***
Documentation       STM32U575 OCTOSPI1 indirect mode (RM0456 Rev 7 section 28) with a
...                 GenericSpiFlash in single-line mode, and the OCTOSPIM reset values, without
...                 firmware: register sequences as STM32Cube HAL_OSPI_Command/Transmit/Receive
...                 write them.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create Machine With Flash

*** Variables ***
${OCTOSPI}          0x420D1400
${OCTOSPIM}         0x420C4000
${CR}               0x000
${SR}               0x020
${DLR}              0x040
${AR}               0x048
${DR}               0x050
${CCR}              0x100
${IR}               0x110
${MAPPED}           0x90000000
# CR: EN; FMODE (bits 29:28) 00 indirect write, 01 indirect read.
${INDIRECT_WRITE}   0x00000001
${INDIRECT_READ}    0x10000001
# CCR: IMODE = 1 line; ADMODE = 1 line, ADSIZE = 24 bits; DMODE = 1 line.
${INSTRUCTION_ONLY}    0x00000001
${INSTRUCTION_DATA}    0x01000001
${INSTRUCTION_ADDRESS_DATA}    0x01002101
${SR_TCF}           0x2
${SR_FTF}           0x4

*** Keywords ***
Create Machine With Flash
    Create STM32U575 Machine
    Execute Command                 cpu IsHalted true
    # 16 MB of flash memory, mapped at the OCTOSPI1 memory-mapped region.
    Execute Command                 machine LoadPlatformDescriptionFromString "octoFlashMemory: Memory.MappedMemory @ sysbus 0x90000000 { size: 0x1000000 }"
    Execute Command                 machine LoadPlatformDescriptionFromString "octoFlash: SPI.GenericSpiFlash @ octospi1 { underlyingMemory: octoFlashMemory; manufacturerId: 0xC2; memoryType: 0x85 }"

Byte Should Be
    [Arguments]                     ${address}    ${expected}
    ${address}=                     Evaluate           hex(int(${address}))
    ${value}=                       Execute Command    sysbus ReadByte ${address}
    ${value}=                       Strip String       ${value}
    Should Be Equal As Integers     ${value}           ${expected}    msg=${address}

*** Test Cases ***
Should Have The OCTOSPIM Reset Values
    # RM0456 section 29.5: OCTOSPIM_CR 0, OCTOSPIM_P1CR 0x0301 0111, OCTOSPIM_P2CR 0x0705 0333.
    Word Should Be                  ${OCTOSPIM}        0
    Word Should Be                  ${OCTOSPIM} + 4    0x03010111
    Word Should Be                  ${OCTOSPIM} + 8    0x07050333

Should Read The Flash Identification In Indirect Read Mode
    # READ ID (0x9F): instruction only, three data bytes; the transfer starts on the IR write.
    Write Word                      ${OCTOSPI} + ${CR}     ${INDIRECT_READ}
    Write Word                      ${OCTOSPI} + ${DLR}    2
    Write Word                      ${OCTOSPI} + ${CCR}    ${INSTRUCTION_DATA}
    Bits Should Be Clear            ${OCTOSPI} + ${SR}     ${SR_TCF}
    Write Word                      ${OCTOSPI} + ${IR}     0x9F
    Bits Should Be Set              ${OCTOSPI} + ${SR}     ${SR_TCF}
    # FLEVEL (bits 13:8) = 3 bytes; FTF with FTHRES = 0: one byte or more.
    ${status}=                      Read Word          ${OCTOSPI} + ${SR}
    Should Be Equal As Integers     ${{ (${status} >> 8) & 0x3F }}    3
    Bits Should Be Set              ${OCTOSPI} + ${SR}     ${SR_FTF}
    # Manufacturer, memory type, capacity code of 16 MB (0x18).
    Byte Should Be                  ${OCTOSPI} + ${DR}     0xC2
    Byte Should Be                  ${OCTOSPI} + ${DR}     0x85
    Byte Should Be                  ${OCTOSPI} + ${DR}     0x18
    # The FIFO is empty: FTF cleared (RM0456 OCTOSPI_CR FTHRES: FTHRES + 1 bytes).
    Bits Should Be Clear            ${OCTOSPI} + ${SR}     ${SR_FTF}

Should Program And Read Back A Word With A 24-Bit Address
    # WRITE ENABLE (0x06): instruction only, starts on the IR write.
    Write Word                      ${OCTOSPI} + ${CR}     ${INDIRECT_WRITE}
    Write Word                      ${OCTOSPI} + ${CCR}    ${INSTRUCTION_ONLY}
    Write Word                      ${OCTOSPI} + ${IR}     0x06
    Bits Should Be Set              ${OCTOSPI} + ${SR}     ${SR_TCF}
    Write Word                      ${OCTOSPI} + 0x024     ${SR_TCF}
    # PAGE PROGRAM (0x02) at 0x010203, four bytes: starts with the data written to DR.
    Write Word                      ${OCTOSPI} + ${CCR}    ${INSTRUCTION_ADDRESS_DATA}
    Write Word                      ${OCTOSPI} + ${DLR}    3
    Write Word                      ${OCTOSPI} + ${IR}     0x02
    Write Word                      ${OCTOSPI} + ${AR}     0x010203
    Bits Should Be Clear            ${OCTOSPI} + ${SR}     ${SR_TCF}
    Write Word                      ${OCTOSPI} + ${DR}     0x44332211
    Bits Should Be Set              ${OCTOSPI} + ${SR}     ${SR_TCF}
    # The address went most significant byte first.
    Word Should Be                  ${MAPPED} + 0x010203    0x44332211
    # READ (0x03) at 0x010203: starts on the AR write.
    Write Word                      ${OCTOSPI} + 0x024     ${SR_TCF}
    Write Word                      ${OCTOSPI} + ${CR}     ${INDIRECT_READ}
    Write Word                      ${OCTOSPI} + ${CCR}    ${INSTRUCTION_ADDRESS_DATA}
    Write Word                      ${OCTOSPI} + ${DLR}    3
    Write Word                      ${OCTOSPI} + ${IR}     0x03
    Bits Should Be Clear            ${OCTOSPI} + ${SR}     ${SR_TCF}
    Write Word                      ${OCTOSPI} + ${AR}     0x010203
    Bits Should Be Set              ${OCTOSPI} + ${SR}     ${SR_TCF}
    Word Should Be                  ${OCTOSPI} + ${DR}     0x44332211
