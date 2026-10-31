*** Settings ***
Documentation       STM32U575 SDMMC1 internal DMA (RM0456 Rev 7 section 31.5.6) with a 1 MB SD card,
...                 without firmware: commands and IDMA registers written as STM32Cube HAL_SD
...                 writes them, the data path advanced in virtual time.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create Machine With Card

*** Variables ***
${SDMMC}            0x420C8000
${CLKCR}            0x04
${CMD}              0x0C
${DLEN}             0x28
${DCTRL}            0x2C
${STA}              0x34
${ICR}              0x38
${IDMACTRL}         0x50
${IDMABSIZE}        0x54
${IDMABASER}        0x58
${IDMALAR}          0x64
${IDMABAR}          0x68
# CMD: WAITRESP short (bit 8), CMDTRANS (bit 6), CPSMEN (bit 12), with the command index.
${CMD17}            ${{ 17 | 0x1140 }}
${CMD18}            ${{ 18 | 0x1140 }}
${CMD24}            ${{ 24 | 0x1140 }}
# DCTRL: DBLOCKSIZE = 9 (512 bytes), DTDIR (bit 1) from the card.
${TO_CARD}          0x90
${FROM_CARD}        0x92
${STA_RXOVERR}      0x00000020
${STA_DATAEND}      0x00000100
${STA_DPSMACT}      0x00001000
${STA_IDMATE}       0x08000000
${STA_IDMABTC}      0x10000000
# IDMALAR: ULA (31), ULS (30), ABR (29); IDMABSIZE: 512 bytes = 16 x 32 (bits 16:5).
${ULA_ULS_ABR}      0xE0000000
${ULA_ULS}          0xC0000000
${BUFFER_512}       0x200
${WRITE_BUFFER}     0x20001000
${READ_BUFFER}      0x20002000
${LIST}             0x20003000

*** Keywords ***
Create Machine With Card
    Create STM32U575 Machine
    Execute Command                 cpu IsHalted true
    Execute Command                 machine LoadPlatformDescriptionFromString "sdcard: SD.STM32U5_SDCard @ sdmmc1 { capacity: 0x100000 }"
    # SDMMC_CK = 48 MHz / 2, 4-bit bus.
    Write Word                      ${SDMMC} + ${CLKCR}    0x00004001

Clear Flags
    Write Word                      ${SDMMC} + ${ICR}      0x1FE007FF

Fill Words
    [Arguments]                     ${address}    ${count}    ${value}
    FOR    ${index}    IN RANGE    ${count}
        Write Word                  ${address} + ${index} * 4    ${value} + ${index}
    END

Write Block To Card
    Fill Words                      ${WRITE_BUFFER}    128    0x11110000
    Write Word                      ${SDMMC} + ${DLEN}         512
    Write Word                      ${SDMMC} + ${DCTRL}        ${TO_CARD}
    Write Word                      ${SDMMC} + ${IDMABASER}    ${WRITE_BUFFER}
    Write Word                      ${SDMMC} + ${IDMACTRL}     1
    Write Word                      ${SDMMC} + ${CMD}          ${CMD24}
    Execute Command                 emulation RunFor "0.01"
    Bits Should Be Set              ${SDMMC} + ${STA}          ${STA_DATAEND}
    Clear Flags

*** Test Cases ***
Should Write And Read A Block In Single Buffer Mode
    Write Block To Card
    Fill Words                      ${READ_BUFFER}     128    0xFFFF0000
    Write Word                      ${SDMMC} + ${DLEN}         512
    Write Word                      ${SDMMC} + ${DCTRL}        ${FROM_CARD}
    Write Word                      ${SDMMC} + ${IDMABASER}    ${READ_BUFFER}
    Write Word                      ${SDMMC} + ${IDMACTRL}     1
    # 1-bit bus, CLKDIV = 1023: SDMMC_CK = 48 MHz / 2046, the 512 bytes take about 175 ms.
    Write Word                      ${SDMMC} + ${CLKCR}        0x000003FF
    Write Word                      ${SDMMC} + ${CMD}          ${CMD17}
    # The transfer takes its bus time: DPSMACT while it runs, then DATAEND.
    Execute Command                 emulation RunFor "0.01"
    Bits Should Be Set              ${SDMMC} + ${STA}          ${STA_DPSMACT}
    Bits Should Be Clear            ${SDMMC} + ${STA}          ${STA_DATAEND}
    Execute Command                 emulation RunFor "0.2"
    Bits Should Be Set              ${SDMMC} + ${STA}          ${STA_DATAEND}
    Bits Should Be Clear            ${SDMMC} + ${STA}          ${STA_DPSMACT}
    Word Should Be                  ${READ_BUFFER}             0x11110000
    Word Should Be                  ${READ_BUFFER} + 508       0x1111007F

Should Fill Two Linked List Buffers And End With DATAEND
    Write Block To Card
    Fill Words                      ${READ_BUFFER}     256    0xFFFF0000
    # Item at IDMABA + 0: the second 512-byte buffer, acknowledged, end of list (ULA = 0).
    Write Word                      ${LIST}            0x20000000
    Write Word                      ${LIST} + 4        ${READ_BUFFER} + 512
    Write Word                      ${LIST} + 8        ${BUFFER_512}
    Write Word                      ${SDMMC} + ${IDMABASER}    ${READ_BUFFER}
    Write Word                      ${SDMMC} + ${IDMABSIZE}    ${BUFFER_512}
    Write Word                      ${SDMMC} + ${IDMABAR}      ${LIST}
    Write Word                      ${SDMMC} + ${IDMALAR}      ${ULA_ULS_ABR}
    Write Word                      ${SDMMC} + ${DLEN}         1024
    Write Word                      ${SDMMC} + ${DCTRL}        ${FROM_CARD}
    Write Word                      ${SDMMC} + ${IDMACTRL}     3
    Write Word                      ${SDMMC} + ${CMD}          ${CMD18}
    Execute Command                 emulation RunFor "0.01"
    Bits Should Be Set              ${SDMMC} + ${STA}          ${STA_DATAEND} | ${STA_IDMABTC}
    Bits Should Be Clear            ${SDMMC} + ${STA}          ${STA_IDMATE} | ${STA_RXOVERR}
    Word Should Be                  ${SDMMC} + ${IDMALAR}      0x20000000
    Word Should Be                  ${READ_BUFFER}             0x11110000
    # The second card block was never written: zeros in the second buffer.
    Word Should Be                  ${READ_BUFFER} + 512       0

Should Stop With IDMATE On An Unacknowledged Linked List Buffer
    # Item at IDMABA + 0 with ABR = 0: the buffer is not ready (RM0456 31.5.6 "Linked list
    # acknowledgment"): IDMA transfer error with RXOVERR, no DATAEND.
    Write Word                      ${LIST}            ${ULA_ULS}
    Write Word                      ${LIST} + 4        ${READ_BUFFER} + 512
    Write Word                      ${LIST} + 8        ${BUFFER_512}
    Write Word                      ${SDMMC} + ${IDMABASER}    ${READ_BUFFER}
    Write Word                      ${SDMMC} + ${IDMABSIZE}    ${BUFFER_512}
    Write Word                      ${SDMMC} + ${IDMABAR}      ${LIST}
    Write Word                      ${SDMMC} + ${IDMALAR}      ${ULA_ULS_ABR}
    Write Word                      ${SDMMC} + ${DLEN}         1024
    Write Word                      ${SDMMC} + ${DCTRL}        ${FROM_CARD}
    Write Word                      ${SDMMC} + ${IDMACTRL}     3
    Write Word                      ${SDMMC} + ${CMD}          ${CMD18}
    Execute Command                 emulation RunFor "0.01"
    Bits Should Be Set              ${SDMMC} + ${STA}          ${STA_IDMABTC} | ${STA_IDMATE} | ${STA_RXOVERR}
    Bits Should Be Clear            ${SDMMC} + ${STA}          ${STA_DATAEND} | ${STA_DPSMACT}
    # IDMATEC, IDMABTCC and RXOVERRC clear the flags.
    Write Word                      ${SDMMC} + ${ICR}          ${STA_IDMATE} | ${STA_IDMABTC} | ${STA_RXOVERR}
    Bits Should Be Clear            ${SDMMC} + ${STA}          ${STA_IDMATE} | ${STA_IDMABTC} | ${STA_RXOVERR}
